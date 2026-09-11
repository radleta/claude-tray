using System.Collections.Concurrent;
using Imrdy.Core.State;
using Microsoft.Extensions.Logging;

namespace Imrdy.Core.Publishing;

/// <summary>
/// What a snapshot does with one state file it just read. Three arms rather than a bool,
/// because the answer is a decision and not a filter: a session this machine does not own is
/// left alone, a live one is published, and an ended one is <em>retired</em> — see
/// <see cref="SessionPublisher.SnapshotActionFor"/> for why the third arm exists.
/// </summary>
public enum SnapshotAction
{
    /// <summary>Not ours to say anything about (D5).</summary>
    Skip,

    /// <summary>Ours and still displayable: send its state.</summary>
    Publish,

    /// <summary>Ours and ended: send a removal, so the receiver drops its copy.</summary>
    Retire,
}

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
    /// Brings a receiver to current: every local session that has not ended is published, and
    /// every local session that has is retired. Sent on connect (D12), because pure deltas
    /// leave a session that went quiet before the receiver connected invisible forever.
    /// <para>
    /// Live sessions are filtered by <see cref="SnapshotActionFor"/>, per the user's ruling
    /// r-4. This is the path that emptied a publisher's entire unswept session directory at a
    /// receiver when a link was registered; on a TCP link the same dump happens on every
    /// connect, not once.
    /// </para>
    /// </summary>
    public async Task PublishSnapshotAsync(CancellationToken cancellationToken)
    {
        foreach (var state in _reader.ReadAllStateFiles(_sessionsDir))
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Ownership is recorded for every file read, whatever we then do with it:
            // RemoveSessionAsync needs the answer once the file is gone, and a session we
            // decline to emit is still ours to mirror the removal of.
            RecordOwnership(state);

            switch (SnapshotActionFor(state))
            {
                case SnapshotAction.Publish:
                    await EmitAsync(state, cancellationToken).ConfigureAwait(false);
                    break;

                case SnapshotAction.Retire:
                    await EmitRemoveAsync(state.SessionId, cancellationToken).ConfigureAwait(false);
                    break;
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

        await EmitRemoveAsync(sessionId, cancellationToken).ConfigureAwait(false);
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
    /// What a snapshot does with this session: nothing if it is not ours (D5), publish it if it
    /// is ours and still worth displaying (<see cref="SessionDisplayFilter"/>, the user's ruling
    /// r-4), and otherwise <em>retire</em> it — send the removal that drops the receiver's copy.
    /// <para>
    /// <b>The third arm is not an optimisation; it is what makes D13 true.</b> D13 drops an
    /// event while a receiver is unreachable and never queues it, on the stated grounds that
    /// "the state file on disk is already the buffer, and the connect snapshot delivers current
    /// state on reconnect". A snapshot that merely <em>skips</em> an ended session falsifies
    /// that sentence for the one status that matters: the publisher's directory is never swept,
    /// so a missed <c>end</c> delta would be skipped by every later snapshot forever, and the
    /// receiver — whose two eviction paths both key on the state file being gone — would draw
    /// that dead session's chip until the user cleared it by hand. Retiring converges instead,
    /// and costs less than publishing would: a removal frame is a fraction of a session frame,
    /// and the receiver's delete is a no-op once its copy is already gone.
    /// </para>
    /// <para>
    /// Snapshots only, and deliberately so. A snapshot enumerates a directory that nothing
    /// sweeps, so it is where a publisher's whole history arrives at once. Filtering deltas
    /// would be worse than useless — it would suppress the very event that tells a receiver a
    /// session ended, freezing the chip at its last live status instead of retiring it.
    /// </para>
    /// <para>
    /// A session that has merely gone <em>quiet</em> is published at any age, because that is
    /// exactly the state imrdy exists to report. An age term was built here and removed on the
    /// user's ruling r-5; see <see cref="SessionDisplayFilter"/> before adding one back.
    /// </para>
    /// <para>
    /// Public and static for the same reason <see cref="IsLocallyOwned"/> is: a TCP sink
    /// assembles its own connect snapshot (D12) from <c>SinkContext.LocalSnapshot</c>. Two call
    /// sites, one decision — a filter on <see cref="PublishSnapshotAsync"/> alone would leave
    /// the TCP connect dump entirely intact. It returns a decision rather than a pair of
    /// predicates on purpose: two booleans answering "publish?" and "retire?" are two things a
    /// later edit can push out of agreement, and a session that is neither published nor
    /// retired is the defect this arm exists to fix.
    /// </para>
    /// </summary>
    public static SnapshotAction SnapshotActionFor(StateFileModel state) =>
        !IsLocallyOwned(state) ? SnapshotAction.Skip
        : SessionDisplayFilter.WouldDisplay(state) ? SnapshotAction.Publish
        : SnapshotAction.Retire;

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

    /// <summary>
    /// Sends one removal to every sink. Shared by the two reasons a session stops being
    /// published — its file disappeared (<see cref="RemoveSessionAsync"/>) and it ended
    /// (<see cref="SnapshotAction.Retire"/>) — so the two cannot drift on what a removal does.
    /// The ownership guard is the caller's: the file-gone path has to consult a record, and the
    /// snapshot path has the state file in hand.
    /// </summary>
    private async Task EmitRemoveAsync(string sessionId, CancellationToken cancellationToken)
    {
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
