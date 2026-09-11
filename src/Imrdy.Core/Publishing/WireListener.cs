using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Imrdy.Core.Validation;
using Microsoft.Extensions.Logging;

namespace Imrdy.Core.Publishing;

/// <summary>
/// The receiving half of the TCP layer: binds and accepts the connections publishers dial
/// (D8), authenticates each one, and writes what arrives into this machine's own sessions
/// directory through <see cref="SessionIngest"/>.
/// <para>
/// The bind is <c>0.0.0.0</c> on purpose (D9). Windows enforces inbound access at the firewall,
/// not by interface binding, so a narrow bind would break reachability from WSL without buying
/// a security property; the scoping is firewall rules for the tailnet and loopback.
/// </para>
/// <para>
/// Nothing travels back to a publisher (D4), so this never writes to a connection. A publisher
/// that disconnects keeps its sessions on disk in their last-known state (D20) — evicting them
/// here would make a transient drop look like every session ending at once.
/// </para>
/// </summary>
public sealed class WireListener : IDisposable
{
    /// <summary>
    /// Connections handled at once. Every one of them is an unauthenticated peer until its
    /// hello lands, so this is what stops a peer parking one task per connection until the
    /// process exits. Far above the handful of machines D24 describes.
    /// </summary>
    private const int MaxConcurrentConnections = 16;

    /// <summary>
    /// Distinct machines tracked in <see cref="_links"/>. The table is keyed on a name the
    /// peer chooses and is rendered by both connection surfaces, so without a cap a peer that
    /// never authenticates could grow it without bound and flood the window. At the cap a new
    /// name is refused with a log line only and no entry — an operator with more machines than
    /// this has a different problem than the one this table answers.
    /// </summary>
    private const int MaxTrackedLinks = 64;

    /// <summary>Longest machine name kept. A wire field can be up to the 64 KiB line cap.</summary>
    private const int MaxMachineNameLength = 64;

    /// <summary>
    /// How long a peer has to send its hello. A connection that sends nothing otherwise holds
    /// a slot forever, and <see cref="MaxConcurrentConnections"/> would then be a cap on how
    /// many it takes to lock the listener out rather than a defence.
    /// </summary>
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _accepts = new(MaxConcurrentConnections, MaxConcurrentConnections);
    private readonly int _port;
    private readonly Func<string?> _resolveAuthKey;
    private readonly SessionIngest _ingest;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, InboundLink> _links = new(StringComparer.OrdinalIgnoreCase);

    private TcpListener? _listener;

    /// <param name="resolveAuthKey">
    /// Read per connection rather than captured, because <c>config.json</c> live-reloads (D25).
    /// Null means no key is configured and any publisher is accepted.
    /// </param>
    public WireListener(int port, Func<string?> resolveAuthKey, SessionIngest ingest, ILogger logger)
    {
        _port = port;
        _resolveAuthKey = resolveAuthKey;
        _ingest = ingest;
        _logger = logger;
    }

    /// <summary>The port actually bound, which differs from the requested one only when 0 was requested.</summary>
    public int BoundPort =>
        _listener?.LocalEndpoint is IPEndPoint endpoint ? endpoint.Port : _port;

    /// <summary>
    /// Binds and starts accepting. Returns as soon as the socket is listening, so a caller can
    /// read <see cref="BoundPort"/>; the accept loop runs until the token trips. A bind failure
    /// is logged and swallowed — a port already in use must not take the tray down with it.
    /// </summary>
    public bool Start(CancellationToken cancellationToken)
    {
        try
        {
            _listener = new TcpListener(IPAddress.Any, _port);
            _listener.Start();
        }
        catch (SocketException ex)
        {
            _logger.LogWarning(ex, "Wire listener could not bind port {Port}; inbound publishing is off", _port);
            _listener = null;
            return false;
        }

        if (string.IsNullOrEmpty(_resolveAuthKey()))
        {
            // D10's key is what makes a misconfigured machine fail loudly instead of injecting
            // sessions into the wrong tray, and null means none is configured — so every peer
            // the firewall lets through is accepted. Whether to bind at all in that state is the
            // operator's call; being told is not.
            _logger.LogWarning(
                "Wire listener on port {Port} has no auth key configured: every publisher that reaches this port is accepted. Set network.authKey to require one",
                BoundPort);
        }

        _logger.LogInformation("Wire listener accepting publishers on port {Port}", BoundPort);
        _ = Task.Run(() => AcceptLoopAsync(cancellationToken), CancellationToken.None);
        return true;
    }

    /// <summary>
    /// One <see cref="SinkHealth"/> per publisher seen since start, for <c>imrdy links</c> and
    /// the connections window. A publisher that has disconnected stays in this list reporting
    /// <see cref="SinkState.Failed"/>: its sessions are still on disk, and the operator needs to
    /// know they are stale rather than see the link disappear.
    /// </summary>
    public IReadOnlyList<SinkHealth> Health()
    {
        lock (_gate)
        {
            return _links.Values.Select(link => link.Snapshot()).ToList();
        }
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        var listener = _listener;
        if (listener is null)
        {
            return;
        }

        while (!cancellationToken.IsCancellationRequested)
        {
            // Taken before the accept, so a peer over the cap waits in the OS backlog rather
            // than in a task of its own. Released by the handler when the connection ends.
            try
            {
                await _accepts.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                _accepts.Release();
                return;
            }

            _ = Task.Run(() => HandleConnectionAsync(client, cancellationToken), CancellationToken.None);
        }
    }

    private async Task HandleConnectionAsync(TcpClient client, CancellationToken cancellationToken)
    {
        var peer = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        string? machine = null;

        // A refusal already said why this link died. The ordinary end-of-connection path below
        // would overwrite that reason with a bare "disconnected", which is the silence D10 and
        // D28 exist to prevent.
        var refused = false;

        // The hello has its own deadline; everything after it is a live link that may sit idle
        // between hook events for as long as the publisher stays up, which is the whole point
        // of a persistent connection (D8).
        using var helloCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        helloCts.CancelAfter(HelloTimeout);

        try
        {
            using (client)
            {
                var reader = new WireLineReader(client.GetStream());

                while (true)
                {
                    var readToken = machine is null ? helloCts.Token : cancellationToken;
                    var result = await reader.ReadLineAsync(readToken).ConfigureAwait(false);

                    if (result.Status == WireReadStatus.EndOfStream)
                    {
                        break;
                    }

                    if (result.Status == WireReadStatus.LineTooLong)
                    {
                        Refuse(
                            machine ?? peer,
                            $"a line exceeded the {WireProtocol.MaxLineBytes}-byte limit");
                        refused = true;
                        break;
                    }

                    if (!WireProtocol.TryParse(result.Line.Span, out var frame) || frame is null)
                    {
                        _logger.LogDebug("Wire listener: unparseable line from {Peer}, skipped", peer);
                        continue;
                    }

                    if (machine is null)
                    {
                        if (!TryAcceptHello(frame, peer, out machine))
                        {
                            break;
                        }

                        continue;
                    }

                    if (!Ingest(frame, machine, peer))
                    {
                        refused = true;
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // The hello deadline, not shutdown: a peer that connected and said nothing.
            Refuse(peer, $"no hello frame within {HelloTimeout.TotalSeconds:0} seconds");
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException)
        {
            _logger.LogWarning(ex, "Wire listener: connection from {Peer} faulted", peer);
            MarkDisconnected(machine ?? peer, LogFieldEscaper.Escape(ex.Message));
            return;
        }
        finally
        {
            // The accept loop took a slot for this connection before it accepted it; nothing
            // else releases one, so a handler that returns without this permanently narrows
            // how many publishers the listener can hold.
            _accepts.Release();
        }

        if (machine is not null && !refused)
        {
            MarkDisconnected(machine, "disconnected");
        }
    }

    /// <summary>
    /// The name a peer chose for itself, made safe to keep. It becomes a dictionary key, a log
    /// field, the <c>origin_machine</c> stamped on every session it delivers, and a row in the
    /// connections window — so it is escaped against CWE-117 and bounded well under the 64 KiB
    /// a wire field can carry.
    /// </summary>
    private static string SanitizeMachineName(string? name, string peer) =>
        string.IsNullOrWhiteSpace(name)
            ? peer
            : LogFieldEscaper.EscapeBounded(name, MaxMachineNameLength);

    /// <summary>
    /// Validates the connect frame. Every rejection is recorded as that link's last error, never
    /// as silence — a machine refused for a bad key or a skewed schema has to show as a dead
    /// link the operator can see (D10, D28).
    /// </summary>
    private bool TryAcceptHello(WireFrame frame, string peer, out string? machine)
    {
        machine = null;

        if (frame.Type != WireFrameTypes.Hello)
        {
            Refuse(
                peer,
                $"first frame was '{LogFieldEscaper.EscapeBounded(frame.Type, MaxMachineNameLength)}', expected '{WireFrameTypes.Hello}'");
            return false;
        }

        var name = SanitizeMachineName(frame.Machine, peer);

        if (!WireProtocol.IsCompatible(frame.SchemaVersion))
        {
            Refuse(
                name,
                $"schema major {LogFieldEscaper.EscapeBounded(frame.SchemaVersion, MaxMachineNameLength)} is not compatible with {WireProtocol.SchemaVersion}");
            return false;
        }

        if (!KeyMatches(frame.Key))
        {
            Refuse(name, "auth key mismatch");
            return false;
        }

        machine = name;
        MarkConnected(name);
        _logger.LogInformation("Wire listener: publisher {Machine} connected from {Peer}", name, peer);
        return true;
    }

    private bool KeyMatches(string? offered)
    {
        var expected = _resolveAuthKey();
        if (string.IsNullOrEmpty(expected))
        {
            return true;
        }

        return offered is not null
            && CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expected),
                Encoding.UTF8.GetBytes(offered));
    }

    /// <summary>
    /// Applies one post-hello frame. An unknown <c>type</c> is skipped rather than fatal: within
    /// a schema major a peer one revision ahead may send frames this build has no name for (D28).
    /// A session id the ingest guard refuses is the opposite — a peer composing a path out of
    /// this machine's sessions directory is hostile, not merely newer — so the connection ends
    /// and the reason becomes that link's last error.
    /// </summary>
    /// <returns>False when the connection must be torn down.</returns>
    private bool Ingest(WireFrame frame, string machine, string peer)
    {
        switch (frame.Type)
        {
            case WireFrameTypes.Session when frame.State is not null:
                if (!_ingest.Apply(frame.State, machine, out var applyRefusal))
                {
                    Refuse(machine, applyRefusal ?? "refused");
                    return false;
                }

                MarkDelivered(machine, frame.State.SessionId);
                break;

            case WireFrameTypes.Remove when frame.SessionId is not null:
                if (!_ingest.Remove(frame.SessionId, out var removeRefusal))
                {
                    Refuse(machine, removeRefusal ?? "refused");
                    return false;
                }

                MarkRemoved(machine, frame.SessionId);
                break;

            default:
                _logger.LogDebug(
                    "Wire listener: frame type '{Type}' from {Peer} is not handled in schema major {Major}, skipped",
                    LogFieldEscaper.EscapeBounded(frame.Type, MaxMachineNameLength),
                    peer,
                    WireProtocol.SchemaVersion);
                break;
        }

        return true;
    }

    /// <summary>
    /// The link record for one machine, created on first sight. Bounded at
    /// <see cref="MaxTrackedLinks"/>: <see cref="Refuse"/> runs before a peer has proved
    /// anything, so an unauthenticated peer reconnecting under a fresh name each time would
    /// otherwise grow this table — which both connection surfaces render — until the process
    /// exits. Null at the cap; the caller has already logged the reason either way.
    /// </summary>
    private InboundLink? LinkFor(string name)
    {
        if (_links.TryGetValue(name, out var link))
        {
            return link;
        }

        if (_links.Count >= MaxTrackedLinks)
        {
            return null;
        }

        link = new InboundLink(name);
        _links[name] = link;
        return link;
    }

    private void MarkConnected(string machine)
    {
        lock (_gate)
        {
            var link = LinkFor(machine);
            if (link is null) return;
            link.State = SinkState.Connected;
            link.LastError = null;
        }
    }

    private void MarkDelivered(string machine, string sessionId)
    {
        lock (_gate)
        {
            var link = LinkFor(machine);
            if (link is null) return;
            link.Sessions.Add(sessionId);
            link.LastSuccessAt = DateTimeOffset.UtcNow;
            link.LastError = null;
        }
    }

    private void MarkRemoved(string machine, string sessionId)
    {
        lock (_gate)
        {
            var link = LinkFor(machine);
            if (link is null) return;
            link.Sessions.Remove(sessionId);
            link.LastSuccessAt = DateTimeOffset.UtcNow;
        }
    }

    private void MarkDisconnected(string machine, string reason)
    {
        lock (_gate)
        {
            var link = LinkFor(machine);
            if (link is null) return;
            link.State = SinkState.Failed;
            link.LastError = reason;
        }
    }

    private void Refuse(string name, string reason)
    {
        _logger.LogWarning("Wire listener: refused publisher {Machine} — {Reason}", name, reason);
        MarkDisconnected(name, reason);
    }

    public void Dispose()
    {
        // The semaphore is deliberately not disposed: a handler still winding down releases its
        // slot in a finally, and disposing under it turns shutdown into an unobserved throw.
        _listener?.Stop();
        _listener = null;
    }

    private sealed class InboundLink(string name)
    {
        public HashSet<string> Sessions { get; } = new(StringComparer.Ordinal);

        public SinkState State { get; set; } = SinkState.Dialing;

        public DateTimeOffset? LastSuccessAt { get; set; }

        public string? LastError { get; set; }

        public SinkHealth Snapshot() => new(name, State, LastSuccessAt, LastError, Sessions.Count);
    }
}
