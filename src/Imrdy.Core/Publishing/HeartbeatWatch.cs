namespace Imrdy.Core.Publishing;

/// <summary>
/// The receiver's side of the file-sink liveness signal: a cached snapshot of every beat in
/// the local heartbeat directory, and the question the tray asks of it.
/// <para>
/// <b>Display-only.</b> This resolves at render time and persists nothing, the same shape
/// <c>DisplayStatus.Resolve</c> takes and for the same reason. Writing a disconnected status
/// back into a session's state file is how the status mechanism this project already replaced
/// froze sessions at a value that could never be re-derived — and it would be worse here,
/// since the receiver's ingest merge would then own a field the publisher also writes.
/// </para>
/// <para>
/// <b>Absence is not disconnection.</b> A machine with no beat file at all answers false. A
/// publisher that does not write beats — an older build, or a Windows tray publishing over a
/// file sink — therefore behaves exactly as it did before this existed, rather than showing a
/// permanent false alarm. Only a beat that is present <em>and</em> stale says gone, which also
/// makes this additive across a partial upgrade in the spirit of D28.
/// </para>
/// <para>
/// Reading is cached because the callers are hot: the tray asks per session on its 100 ms
/// drain tick. <see cref="Refresh"/> is what touches the disk, and the tray calls it from the
/// 5 s aging tick it already owns — no new timer, and a cadence well inside
/// <see cref="PublisherHeartbeat.StaleAfter"/>.
/// </para>
/// </summary>
public sealed class HeartbeatWatch
{
    private readonly string _directory;
    private IReadOnlyDictionary<string, DateTimeOffset> _beats =
        new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

    /// <param name="heartbeatDirectory">
    /// Where publishers write their beats on this machine — <c>ImrdyPaths.Heartbeats</c> for
    /// the tray. A file sink writes into the sibling of the sessions directory it targets, so
    /// the two ends meet here.
    /// </param>
    public HeartbeatWatch(string heartbeatDirectory) => _directory = heartbeatDirectory;

    /// <summary>
    /// The current snapshot, keyed by <see cref="PublisherHeartbeat.TokenFor"/> token. Exposed
    /// because <see cref="IsDisconnected"/> answers about a publisher the caller can already
    /// name, and the connections surfaces have the opposite problem: they need to know a
    /// file-sink publisher exists at all. The keys are tokens and a token is lossy, so name
    /// them through <see cref="HeartbeatMachines.Resolve"/> rather than rendering a key.
    /// </summary>
    public IReadOnlyDictionary<string, DateTimeOffset> Beats => _beats;

    /// <summary>
    /// Re-reads every beat. Two failure levels, each deliberate. A <em>directory</em> that
    /// cannot be listed — the ordinary case on a receiver no file-sink publisher has ever
    /// written to — leaves the previous snapshot standing untouched, so a transient IO blip
    /// cannot silently mark every publisher gone. A <em>single</em> beat that is unreadable or
    /// half-written is left out of the new snapshot, which makes it absent, which fails open.
    /// </summary>
    public void Refresh()
    {
        var beats = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);

        try
        {
            foreach (var file in Directory.GetFiles(_directory, "*" + PublisherHeartbeat.FileExtension))
            {
                if (PublisherHeartbeat.TryParse(ReadOrNull(file), out var beat))
                {
                    beats[Path.GetFileNameWithoutExtension(file)] = beat;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Keep the snapshot we already have rather than publishing an empty one.
            return;
        }

        _beats = beats;
    }

    /// <summary>
    /// Whether this publisher's beat has gone stale. False when it never beat at all — see the
    /// class remarks on why absence has to fail open.
    /// </summary>
    public bool IsDisconnected(string machine, DateTimeOffset now) =>
        _beats.TryGetValue(PublisherHeartbeat.TokenFor(machine), out var beat)
        && PublisherHeartbeat.IsStale(beat, now);

    private static string? ReadOrNull(string file)
    {
        try
        {
            return File.ReadAllText(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}
