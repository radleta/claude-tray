using System.Net.Sockets;
using Imrdy.Core.State;
using Microsoft.Extensions.Logging;

namespace Imrdy.Core.Publishing;

/// <summary>
/// The network sink: dials a receiver and writes newline-delimited JSON frames over one
/// persistent connection (D8). The publisher dials, the receiver listens, and nothing travels
/// back (D4) — a read here exists only to notice the peer closing.
/// <para>
/// Nothing is ever queued. While the link is down, events are dropped (D13): the state file on
/// disk is already the buffer, and the full snapshot sent at connect (D12) delivers current
/// state the moment the link returns. That is why a receiver that was off for an hour comes
/// back correct rather than replaying an hour of deltas.
/// </para>
/// <para>
/// The dial loop is a long-lived background task started at construction, so the link exists
/// whether or not sessions are changing — a publisher that has gone quiet must still read as
/// connected on the receiver. It must be disposed, or its socket and its loop leak;
/// <see cref="SinkRegistry"/> disposes an evicted sink for exactly this reason.
/// </para>
/// </summary>
public sealed class TcpSink : ISessionSink, IDisposable
{
    private static readonly TimeSpan InitialBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan DisposeGrace = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long a connection must last before it counts as a link that <em>held</em>, and so
    /// before the backoff is allowed back down to <see cref="InitialBackoff"/>.
    /// <para>
    /// The receiver never acknowledges anything (D4), so there is no application-level signal
    /// that a peer accepted us — only how long it left the socket open. A refusing receiver
    /// closes at once: <c>WireListener.TryAcceptHello</c> reads one line and drops the
    /// connection on an auth-key mismatch or a schema-major mismatch, which is a single round
    /// trip on a tailnet. A real link lasts until the publisher or the receiver stops. Five
    /// seconds is far above the first and far below the second, so it separates them without
    /// having to guess at either precisely.
    /// </para>
    /// <para>
    /// It is measured from the <em>end of the connect snapshot</em>, not from the TCP connect, so
    /// a slow snapshot cannot spend the budget on its own and buy a refusing peer the reset. That
    /// is the stricter of the two available points, and deliberately: the snapshot's cost grows
    /// with a sessions directory nothing sweeps, so measuring from the connect would weaken this
    /// guard exactly as the problem it guards against got worse. The price of the stricter point
    /// is that a genuinely healthy link dropping just after a slow snapshot backs off further than
    /// it needed — capped at <see cref="MaxBackoff"/> and self-correcting on the next link that
    /// holds, where the looser direction restores the defect outright.
    /// </para>
    /// <para>
    /// It is a property of one connection, never of a session's age. The session-age lever is
    /// closed by the user's ruling r-5 and nothing here reopens it.
    /// </para>
    /// </summary>
    private static readonly TimeSpan LinkHeldMinimum = TimeSpan.FromSeconds(5);

    private readonly string _linkName;
    private readonly string _host;
    private readonly int _port;
    private readonly SinkContext _context;
    private readonly ILogger _logger;

    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Lock _healthGate = new();
    private readonly Task _dialLoop;
    private readonly HashSet<string> _delivered = [];

    private TcpClient? _client;
    private Stream? _stream;
    private SinkState _state = SinkState.Dialing;
    private DateTimeOffset? _lastSuccessAt;
    private string? _lastError;
    private int _disposed;

    /// <param name="linkName">
    /// The registered link this sink serves, and what <see cref="SinkHealth.Name"/> reports so
    /// <c>imrdy links</c> can say which link is unhealthy.
    /// </param>
    public TcpSink(string linkName, string host, int port, SinkContext context, ILogger logger)
    {
        _linkName = linkName;
        _host = host;
        _port = port;
        _context = context;
        _logger = logger;

        _dialLoop = Task.Run(() => DialLoopAsync(_cts.Token));
    }

    public SinkHealth Health
    {
        get
        {
            lock (_healthGate)
            {
                return new SinkHealth(_linkName, _state, _lastSuccessAt, _lastError, _delivered.Count);
            }
        }
    }

    public async Task PublishAsync(StateFileModel state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // origin_machine is stamped here, on the wire only — the publisher's own state file
        // stays exactly what the hook wrote (D5).
        var stamped = state with { OriginMachine = _context.ResolveOriginMachine() };

        if (await SendAsync(WireFrame.Session(stamped), "publish", state.SessionId, cancellationToken)
            .ConfigureAwait(false))
        {
            lock (_healthGate)
            {
                _delivered.Add(state.SessionId);
            }
        }
    }

    public async Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (await SendAsync(WireFrame.Remove(sessionId), "remove", sessionId, cancellationToken)
            .ConfigureAwait(false))
        {
            lock (_healthGate)
            {
                _delivered.Remove(sessionId);
            }
        }
    }

    /// <summary>
    /// Writes one frame if the link is up. Returns false when the frame was dropped — no
    /// connection, or a frame too long to be legal on the wire. A drop is never retried and
    /// never queued.
    /// </summary>
    private async Task<bool> SendAsync(
        WireFrame frame,
        string operation,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var stream = Volatile.Read(ref _stream);
        if (stream is null)
        {
            return false;
        }

        var line = WireProtocol.Serialize(frame);
        if (line.Length > WireProtocol.MaxLineBytes)
        {
            // Dropping one oversize frame beats sending it: the receiver's rule is to close
            // the connection over a long line, which would cost every other session too.
            _logger.LogWarning(
                "Sink {Link}: {Operation} frame for session {SessionId} is {Bytes} bytes, over the {Limit}-byte line limit, and was dropped",
                _linkName,
                operation,
                sessionId,
                line.Length,
                WireProtocol.MaxLineBytes);

            RecordError($"frame for session {sessionId} exceeded the {WireProtocol.MaxLineBytes}-byte line limit");
            return false;
        }

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await stream.WriteAsync(line, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            RecordFailure(ex, operation, sessionId);
            DropConnection();
            return false;
        }
        finally
        {
            _writeGate.Release();
        }

        lock (_healthGate)
        {
            _lastSuccessAt = DateTimeOffset.UtcNow;
            _lastError = null;
        }

        return true;
    }

    private async Task DialLoopAsync(CancellationToken cancellationToken)
    {
        var backoff = InitialBackoff;

        while (!cancellationToken.IsCancellationRequested)
        {
            // Null while we are still talking, and for a connect that threw — so a hello write
            // that faults is correctly not a link that held.
            DateTimeOffset? snapshotDoneAt = null;

            try
            {
                SetState(SinkState.Dialing);
                snapshotDoneAt = await ConnectAsync(cancellationToken).ConfigureAwait(false);

                // Held until the receiver closes or the link faults; the loop then redials.
                await AwaitPeerCloseAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // LastError is a string for the UI; the full exception goes to the log.
                _logger.LogWarning(ex, "Sink {Link}: connection to {Host}:{Port} failed", _linkName, _host, _port);
                SetFailed(ex.Message);
            }
            finally
            {
                DropConnection();
            }

            // Reset only for a link that went on holding after we stopped talking. Resetting on
            // "the connect returned" reads a socket the peer is about to close as a healthy link,
            // and a receiver that refuses our auth key does exactly that — which left this loop
            // redialling at 1 Hz forever, re-reading the whole sessions directory and re-sending
            // it each time, with MaxBackoff never engaging. The clock starts at the end of the
            // snapshot, so the snapshot's own duration can never pay for the reset.
            if (snapshotDoneAt is { } readyAt
                && DateTimeOffset.UtcNow - readyAt >= LinkHeldMinimum)
            {
                backoff = InitialBackoff;
            }

            try
            {
                await Task.Delay(backoff, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            backoff = Min(backoff + backoff, MaxBackoff);
        }
    }

    /// <returns>
    /// When the connect snapshot finished — deliberately the <em>latest</em> of the points this
    /// could be taken, so that only the link's life <em>after</em> we stopped talking counts
    /// toward <see cref="LinkHeldMinimum"/>. Starting the clock at the TCP connect instead would
    /// let a slow snapshot spend the whole budget on its own, handing a refusing peer the reset —
    /// and the snapshot is exactly the thing that grows without bound on an unswept directory, so
    /// that guard would weaken as the problem worsened.
    /// </returns>
    private async Task<DateTimeOffset> ConnectAsync(CancellationToken cancellationToken)
    {
        var client = new TcpClient();

        try
        {
            await client.ConnectAsync(_host, _port, cancellationToken).ConfigureAwait(false);

            var stream = client.GetStream();
            var hello = WireProtocol.Serialize(
                WireFrame.Hello(_context.ResolveOriginMachine(), _context.ResolveAuthKey()));

            await stream.WriteAsync(hello, cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);

            _client = client;
            Volatile.Write(ref _stream, stream);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        lock (_healthGate)
        {
            _state = SinkState.Connected;
            _lastError = null;
        }

        _logger.LogInformation("Sink {Link}: connected to {Host}:{Port}", _linkName, _host, _port);

        await SendConnectSnapshotAsync(cancellationToken).ConfigureAwait(false);
        return DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// The snapshot D12 requires at connect. Without it a session that went quiet before
    /// this receiver connected would stay invisible to it forever, and a session removed while
    /// the link was down would linger as a ghost.
    /// <para>
    /// Its contents are whatever <c>SinkContext.LocalSnapshot</c> yields — this machine's own
    /// sessions, ended ones included — and <see cref="SessionPublisher.SnapshotActionFor"/>
    /// decides what each one becomes on the wire. An ended session is retired rather than
    /// published, so this snapshot converges the receiver in both directions: the live sessions
    /// it has not seen appear, and the ones that ended while the link was down are dropped.
    /// That second half is what D13 means when it says nothing needs queueing.
    /// </para>
    /// <para>
    /// It matters most here rather than on the one-shot startup snapshot: this runs on every
    /// dial, so a list that published everything would re-dump the publisher's entire unswept
    /// session history at each reconnect.
    /// </para>
    /// </summary>
    private async Task SendConnectSnapshotAsync(CancellationToken cancellationToken)
    {
        foreach (var state in _context.LocalSnapshot())
        {
            cancellationToken.ThrowIfCancellationRequested();

            switch (SessionPublisher.SnapshotActionFor(state))
            {
                case SnapshotAction.Publish:
                    await PublishAsync(state, cancellationToken).ConfigureAwait(false);
                    break;

                case SnapshotAction.Retire:
                    await RemoveAsync(state.SessionId, cancellationToken).ConfigureAwait(false);
                    break;
            }
        }
    }

    /// <summary>
    /// Blocks until the receiver closes the connection. The receiver never sends anything
    /// (D4), so any byte that does arrive is from a peer speaking a protocol this one does not
    /// and is discarded; only end-of-stream is meaningful.
    /// </summary>
    private async Task AwaitPeerCloseAsync(CancellationToken cancellationToken)
    {
        var stream = Volatile.Read(ref _stream);
        if (stream is null)
        {
            return;
        }

        var scratch = new byte[256];
        while (await stream.ReadAsync(scratch, cancellationToken).ConfigureAwait(false) > 0)
        {
        }

        _logger.LogInformation("Sink {Link}: receiver {Host}:{Port} closed the connection", _linkName, _host, _port);
    }

    private void DropConnection()
    {
        Volatile.Write(ref _stream, null);
        _client?.Dispose();
        _client = null;
    }

    private void SetState(SinkState state)
    {
        lock (_healthGate)
        {
            _state = state;
        }
    }

    private void SetFailed(string message)
    {
        lock (_healthGate)
        {
            _state = SinkState.Failed;
            _lastError = message;
        }
    }

    private void RecordError(string message)
    {
        lock (_healthGate)
        {
            _lastError = message;
        }
    }

    private void RecordFailure(Exception ex, string operation, string sessionId)
    {
        _logger.LogWarning(
            ex,
            "Sink {Link}: {Operation} of session {SessionId} failed",
            _linkName,
            operation,
            sessionId);

        SetFailed(ex.Message);
    }

    private static TimeSpan Min(TimeSpan a, TimeSpan b) => a < b ? a : b;

    public void Dispose()
    {
        // Idempotent: a registry eviction and a caller's own using-block can both land here.
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _cts.Cancel();
        DropConnection();

        // The loop is parked on a socket read or a backoff delay; both observe the token.
        // A loop that has not noticed within the grace window is abandoned rather than
        // deadlocking whoever is reconfiguring the registry.
        try
        {
            _dialLoop.Wait(DisposeGrace);
        }
        catch (AggregateException)
        {
            // Cancellation, or a fault the loop already logged.
        }

        _cts.Dispose();
        _writeGate.Dispose();
    }
}
