using Imrdy.Core.State;
using Imrdy.Core.Validation;

namespace Imrdy.Core.Publishing;

/// <summary>
/// Writes a remote session into a sessions directory, applying <see cref="RemoteSessionMerge"/>
/// on the way in. This is the only writer of remote session files (D14), and it has exactly two
/// callers by design: <see cref="FileSink"/>, where a WSL publisher's write into the host's
/// directory <em>is</em> the ingest, and <see cref="WireListener"/>, where a TCP frame is. A
/// third copy of merge-then-write is how the two writers drift apart.
/// <para>
/// It is also the trust boundary. The session id here arrives off the wire or off another
/// machine's mount — unlike the hook path, which validates before it composes a filename — so
/// both verbs validate the id with <see cref="SessionIdValidator"/> and then assert the
/// composed path is still inside the sessions directory, the same
/// <c>GetFullPath</c>-then-<c>StartsWith</c> shape the pack loaders use. Both guards are here
/// rather than in one caller so the file sink and the listener cannot diverge.
/// </para>
/// <para>
/// The write is direct, never a temp-then-rename (D15): an atomic write onto an existing target
/// reaches the host's watcher as <c>Deleted</c> then <c>Renamed</c>, which is not tellable from
/// a session ending. Torn reads are tolerated instead — <c>ReadStateFile</c> returns null and
/// the receiver skips. The cost of a write is therefore a watcher event and a full drain, which
/// is why the write is also <em>skipped</em> when the merge produces exactly the bytes already
/// on disk (<see cref="StateFileReader.WriteStateFileIfChanged"/>). Fewer writes, each one
/// unchanged in kind — not a different kind of write.
/// </para>
/// </summary>
public sealed class SessionIngest
{
    /// <summary>
    /// Longest machine name and session id echoed into a refusal reason. A wire field can be
    /// up to the 64 KiB line cap, and that reason becomes a log line and a link's
    /// <c>LastError</c> in the connections window.
    /// </summary>
    private const int MaxEchoedLength = 64;

    private readonly string _sessionsDir;
    private readonly StateFileReader _reader;

    public SessionIngest(string sessionsDir, StateFileReader reader)
    {
        _sessionsDir = sessionsDir;
        _reader = reader;
    }

    /// <param name="originMachine">
    /// The publisher this payload arrived from. Stamped here rather than trusted from the
    /// payload, so a relayed or misconfigured publisher cannot claim to be another machine.
    /// Escaped and bounded on the way in: it is written to disk, logged, and rendered in the
    /// tooltip and the connections window, and a receiver two hops away must not have to
    /// re-sanitize it.
    /// </param>
    /// <param name="refusal">Why the payload was rejected, for the caller to log and surface.</param>
    /// <returns>False when the id was refused and nothing was written.</returns>
    public bool Apply(StateFileModel incoming, string originMachine, out string? refusal)
    {
        if (!TryResolvePath(incoming.SessionId, out var path, out refusal))
        {
            return false;
        }

        var origin = LogFieldEscaper.EscapeBounded(originMachine, MaxEchoedLength);
        var merged = RemoteSessionMerge.Merge(incoming, _reader.ReadStateFile(path), origin);

        // A payload identical to what is already on disk is not written at all. A connect
        // snapshot repeats on every dial over a directory nothing sweeps, so without this a
        // reconnect costs one direct write and one watcher event per session for a receiver
        // whose state did not move. Skipping does not weaken delivery: the merge above ran, and
        // the file already holds its result.
        _reader.WriteStateFileIfChanged(path, merged);
        return true;
    }

    /// <summary>
    /// Mirrors a publisher's delete. It flows through the receiver's existing grace-period
    /// delete path unchanged, because it is the same file disappearing.
    /// </summary>
    /// <returns>False when the id was refused and nothing was deleted.</returns>
    public bool Remove(string sessionId, out string? refusal)
    {
        if (!TryResolvePath(sessionId, out _, out refusal))
        {
            return false;
        }

        _reader.RemoveStateFile(_sessionsDir, sessionId);
        return true;
    }

    /// <summary>
    /// The one place a session id becomes a path on this seam. Validation first, containment
    /// second: the character rule alone already rejects <c>..</c>, a separator and a drive
    /// letter, and the containment assert is what keeps that true if the rule is ever widened.
    /// </summary>
    private bool TryResolvePath(string? sessionId, out string path, out string? refusal)
    {
        path = string.Empty;

        if (!SessionIdValidator.IsValid(sessionId))
        {
            refusal = $"session id '{LogFieldEscaper.EscapeBounded(sessionId, MaxEchoedLength)}' " +
                      "is empty or carries characters outside [A-Za-z0-9_-]";
            return false;
        }

        var candidate = Path.GetFullPath(Path.Combine(_sessionsDir, $"{sessionId}.json"));
        var root = Path.GetFullPath(_sessionsDir) + Path.DirectorySeparatorChar;

        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            refusal = $"session id '{LogFieldEscaper.EscapeBounded(sessionId, MaxEchoedLength)}' " +
                      "resolves outside the sessions directory";
            return false;
        }

        path = candidate;
        refusal = null;
        return true;
    }
}
