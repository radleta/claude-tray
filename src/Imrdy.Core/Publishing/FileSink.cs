using Imrdy.Core.State;
using Microsoft.Extensions.Logging;

namespace Imrdy.Core.Publishing;

/// <summary>
/// The WSL sink: writes session state straight into the host's sessions directory through
/// a mounted path such as <c>/mnt/c/Users/&lt;user&gt;/.imrdy/sessions</c>. A write made
/// from inside WSL2 through <c>/mnt/c</c> does fire the host's <c>FileSystemWatcher</c>,
/// which is what makes this sink work with no listener, firewall rule, or mirrored
/// networking mode.
/// <para>
/// The write is direct, never a temp-then-rename (D15). An atomic write onto an existing
/// target is reported to the host as <c>Deleted</c> then <c>Renamed</c> about 2ms apart,
/// which the receiver cannot tell from a real session ending. Do not "improve" this into
/// an atomic write: the rename is exactly what reintroduces that ambiguity. Torn reads are
/// already tolerated — <c>ReadStateFile</c> returns null and the receiver skips.
/// </para>
/// <para>
/// There is no receiver-side ingest step on this path — the publisher's write lands in the
/// receiver's sessions directory directly — so this sink is the writer, and it goes through
/// <see cref="SessionIngest"/> for exactly the same merge a TCP frame gets. The receiver's
/// field ownership (D34) is what makes that necessary.
/// </para>
/// </summary>
public sealed class FileSink : ISessionSink
{
    private readonly string _targetSessionsDir;
    private readonly string _linkName;
    private readonly Func<string> _originMachine;
    private readonly SessionIngest _ingest;
    private readonly ILogger _logger;
    private readonly Lock _gate = new();

    private DateTimeOffset? _lastSuccessAt;
    private string? _lastError;
    private readonly HashSet<string> _delivered = [];

    /// <param name="targetSessionsDir">The receiver's sessions directory, as this machine sees it.</param>
    /// <param name="linkName">
    /// The registered link this sink serves. This is what <see cref="SinkHealth.Name"/>
    /// reports, so <c>imrdy links</c> can say which link is unhealthy — not to be confused
    /// with <paramref name="originMachine"/>, which every sink on this publisher shares.
    /// </param>
    /// <param name="originMachine">
    /// The name this publisher stamps as <c>origin_machine</c>. Resolved per write rather than
    /// captured, because <c>network.machineName</c> live-reloads (D25) and a sink outlives many
    /// reloads: a name captured at construction would keep stamping the old one until restart.
    /// </param>
    public FileSink(
        string targetSessionsDir,
        string linkName,
        Func<string> originMachine,
        StateFileReader reader,
        ILogger logger)
    {
        _targetSessionsDir = targetSessionsDir;
        _linkName = linkName;
        _originMachine = originMachine;
        _ingest = new SessionIngest(targetSessionsDir, reader);
        _logger = logger;
    }

    public SinkHealth Health
    {
        get
        {
            lock (_gate)
            {
                return new SinkHealth(
                    _linkName,
                    SinkState.FileSink,
                    _lastSuccessAt,
                    _lastError,
                    _delivered.Count);
            }
        }
    }

    public Task PublishAsync(StateFileModel state, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!_ingest.Apply(state, _originMachine(), out var refusal))
            {
                RecordRefusal(refusal);
                return Task.CompletedTask;
            }

            RecordSuccess(state.SessionId);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecordFailure(ex, "publish", state.SessionId);
        }

        return Task.CompletedTask;
    }

    public Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            if (!_ingest.Remove(sessionId, out var refusal))
            {
                RecordRefusal(refusal);
                return Task.CompletedTask;
            }

            lock (_gate)
            {
                _delivered.Remove(sessionId);
                _lastSuccessAt = DateTimeOffset.UtcNow;
                _lastError = null;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            RecordFailure(ex, "remove", sessionId);
        }

        return Task.CompletedTask;
    }

    private void RecordSuccess(string sessionId)
    {
        lock (_gate)
        {
            _delivered.Add(sessionId);
            _lastSuccessAt = DateTimeOffset.UtcNow;
            _lastError = null;
        }
    }

    /// <summary>
    /// A locally-derived id that the ingest guard rejected. Not expected on this path — the id
    /// comes off this machine's own state files — so it is logged as a warning rather than
    /// swallowed, and it still becomes the link's LastError so the operator can see it.
    /// </summary>
    private void RecordRefusal(string? refusal)
    {
        _logger.LogWarning("File sink into {TargetDir} refused a session: {Reason}", _targetSessionsDir, refusal);

        lock (_gate)
        {
            _lastError = refusal;
        }
    }

    private void RecordFailure(Exception ex, string operation, string sessionId)
    {
        // LastError is a string for the UI to render; the full exception goes to the log.
        _logger.LogWarning(
            ex,
            "File sink {Operation} failed for session {SessionId} into {TargetDir}",
            operation,
            sessionId,
            _targetSessionsDir);

        lock (_gate)
        {
            _lastError = ex.Message;
        }
    }
}
