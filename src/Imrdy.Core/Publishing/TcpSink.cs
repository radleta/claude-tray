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
            try
            {
                SetState(SinkState.Dialing);
                await ConnectAsync(cancellationToken).ConfigureAwait(false);
                backoff = InitialBackoff;

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

    private async Task ConnectAsync(CancellationToken cancellationToken)
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
    }

    /// <summary>
    /// The full snapshot D12 requires at connect. Without it a session that went quiet before
    /// this receiver connected would stay invisible to it forever, and a session removed while
    /// the link was down would linger as a ghost.
    /// </summary>
    private async Task SendConnectSnapshotAsync(CancellationToken cancellationToken)
    {
        foreach (var state in _context.LocalSnapshot())
        {
            cancellationToken.ThrowIfCancellationRequested();
            await PublishAsync(state, cancellationToken).ConfigureAwait(false);
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
