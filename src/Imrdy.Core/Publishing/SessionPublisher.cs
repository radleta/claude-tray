using System.Collections.Concurrent;
using Imrdy.Core.State;
using Microsoft.Extensions.Logging;

namespace Imrdy.Core.Publishing;

/// <summary>
/// The publish pipeline, identical on every publisher: watch the local sessions directory,
/// read the state, stamp origin, emit. Only the sink differs (D3).
/// <para>
/// A long-running process owns this — the tray on Windows, the daemon elsewhere — never the
/// hook (D6). The hook runs hundreds of times per session and its latency is the user's
/// Claude session latency, so it must never wait on a socket or a slow mount.
/// </para>
/// </summary>
public sealed class SessionPublisher
{
    private readonly string _sessionsDir;
    private readonly StateFileReader _reader;
    private readonly Func<IReadOnlyList<ISessionSink>> _sinks;
    private readonly ILogger _logger;

    /// <summary>
    /// Whether each session seen so far was locally owned, recorded every time its state file
    /// is read here and every time the host tells us through <see cref="RecordOwnership"/>. It
    /// exists for one question a deleted file can no longer answer: was this session ours to
    /// publish?
    /// <para>
    /// A session with no entry is treated as <em>not</em> ours. The question this answers is
    /// whether a removal may leave this machine (D4), so the failure mode worth having is the
    /// silent one: an unmirrored removal leaves a stale session on a receiver, which D20
    /// already tolerates and the connections window's clear action removes, while a wrongly
    /// mirrored one deletes another machine's live state file. The host closes the gap rather
    /// than relying on this default — <c>TrayApp.RemoveSession</c> records the origin it still
    /// holds in memory immediately before deleting the file.
    /// </para>
    /// </summary>
    private readonly ConcurrentDictionary<string, bool> _locallyOwned = new(StringComparer.Ordinal);

    /// <param name="sinks">
    /// Resolved per emit, not captured once: the connections window rewrites the record list
    /// and both config surfaces live-reload (D25), so a sink added there must take effect
    /// with no restart.
    /// </param>
    public SessionPublisher(
        string sessionsDir,
        StateFileReader reader,
        Func<IReadOnlyList<ISessionSink>> sinks,
        ILogger logger)
    {
        _sessionsDir = sessionsDir;
        _reader = reader;
        _sinks = sinks;
        _logger = logger;
    }

    /// <summary>
    /// Emits every local session at once. Sent on connect (D12), because pure deltas leave a
    /// session that went quiet before the receiver connected invisible forever.
    /// </summary>
    public async Task PublishSnapshotAsync(CancellationToken cancellationToken)
    {
        foreach (var state in _reader.ReadAllStateFiles(_sessionsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (RecordOwnership(state))
            {
                await EmitAsync(state, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Emits one session after its state file changed. A file that cannot be read is skipped
    /// rather than reported: a torn read is the normal mid-write case and the next event
    /// carries the same state.
    /// </summary>
    public async Task PublishSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        var state = _reader.ReadStateFile(Path.Combine(_sessionsDir, $"{sessionId}.json"));

        if (state is not null && RecordOwnership(state))
        {
            await EmitAsync(state, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Mirrors a <em>local</em> state file's disappearance (D12), and only a local one. The
    /// file is gone by the time this runs, so the origin comes from what was last read off it
    /// rather than from the file itself.
    /// <para>
    /// D5's guard belongs here as much as on the publish path, and its absence was a
    /// would-ship bug: clearing a remote session on the receiver deletes that session's file,
    /// which the watcher reports as a removal, which was then mirrored back to the machine the
    /// session came from and deleted its live state file there. That is a command travelling
    /// receiver → publisher, which D4 forbids outright, and it contradicts D16, where clearing
    /// clears current state and expects the publisher to repopulate it.
    /// </para>
    /// <para>
    /// Only a session recorded as ours is mirrored. An unrecorded one is not: see
    /// <see cref="RecordOwnership"/> for why the unknown case fails closed.
    /// </para>
    /// </summary>
    public async Task RemoveSessionAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (!_locallyOwned.TryRemove(sessionId, out var wasLocal) || !wasLocal)
        {
            _logger.LogDebug(
                "Not mirroring the removal of session {SessionId}: it is not known to have originated here (D4, D5)",
                sessionId);
            return;
        }

        foreach (var sink in _sinks())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await sink.RemoveAsync(sessionId, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogSinkFault(ex, sink, "remove", sessionId);
            }
        }
    }

    /// <summary>
    /// Emits one coalesced batch, mapping each change to a publish or a removal. Both hosts
    /// call this — the daemon's own event loop and the tray's 100ms drain tick — so the
    /// change-kind mapping has one definition rather than one per host.
    /// </summary>
    public async Task DrainAsync(IReadOnlyList<SessionChange> batch, CancellationToken cancellationToken)
    {
        foreach (var change in batch)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (change.Kind == SessionChangeKind.Removed)
            {
                await RemoveSessionAsync(change.SessionId, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await PublishSessionAsync(change.SessionId, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// D5's no-re-publish guard. A null <c>origin_machine</c> means the session originated
    /// here; anything else arrived from another publisher and re-emitting it would loop it
    /// back out through this machine under a second origin.
    /// <para>
    /// Public because a TCP sink assembles its own connect snapshot (D12) and has to apply the
    /// same guard. One definition, two callers — a sink that skipped it would relay another
    /// machine's sessions on every reconnect.
    /// </para>
    /// </summary>
    public static bool IsLocallyOwned(StateFileModel state) => state.OriginMachine is null;

    /// <summary>
    /// Remembers whether this session is ours, and answers the same question. Every read of a
    /// state file goes through here so <see cref="RemoveSessionAsync"/> has an answer once the
    /// file is gone.
    /// <para>
    /// Public because the publisher is not the only reader of these files. The tray reads them
    /// on its own path and holds a session's state in memory when it deletes it, so it can
    /// answer for a session this publisher has never read — a remote file left on disk across a
    /// tray restart, cleared from the connections window before any snapshot ran. Without that
    /// call the removal would be an unknown, and an unknown is not mirrored (D4).
    /// </para>
    /// </summary>
    /// <returns>True when the session originated on this machine.</returns>
    public bool RecordOwnership(StateFileModel state)
    {
        var local = IsLocallyOwned(state);
        _locallyOwned[state.SessionId] = local;
        return local;
    }

    private async Task EmitAsync(StateFileModel state, CancellationToken cancellationToken)
    {
        foreach (var sink in _sinks())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                // origin_machine is stamped on the wire by the sink, never written into this
                // publisher's own state file (D5) — that file stays exactly what the hook wrote.
                await sink.PublishAsync(state, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unreachable sink must not stop the others. Nothing is queued for a
                // retry either: the state file on disk is already the buffer, and the
                // connect snapshot delivers current state on reconnect (D13).
                LogSinkFault(ex, sink, "publish", state.SessionId);
            }
        }
    }

    private void LogSinkFault(Exception ex, ISessionSink sink, string operation, string sessionId)
    {
        _logger.LogWarning(
            ex,
            "Sink {Sink} failed to {Operation} session {SessionId}",
            sink.Health.Name,
            operation,
            sessionId);
    }
}
