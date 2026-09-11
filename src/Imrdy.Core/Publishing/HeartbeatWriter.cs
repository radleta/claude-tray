using System.Text;
using Microsoft.Extensions.Logging;

namespace Imrdy.Core.Publishing;

/// <summary>
/// Writes this publisher's beat into every file-sink link it is registered for. Owned by
/// <see cref="DaemonHost"/> and driven from the daemon's existing loop tick — there is no
/// timer here and there must not be one, for the same reason the tray's publisher rides the
/// drain tick rather than a second <c>FileSystemWatcher</c>.
/// <para>
/// <b>Unconditional, never on session activity.</b> <see cref="WriteIfDue"/> is called from
/// the loop whether or not anything published, which is the property that separates a
/// heartbeat from inference-from-silence: a publisher with no sessions, or with sessions that
/// have gone quiet, keeps beating and keeps reading as alive. Wiring this into the publish
/// path instead would make a quiet publisher look gone, which is precisely the failure the
/// receiver-side read exists to avoid.
/// </para>
/// <para>
/// It reads the registered links per beat rather than capturing them, so a link added through
/// the connections window starts beating on the next period with no daemon restart (D25).
/// <para>
/// The machine name is a delegate for a narrower reason: the beat's filename and the
/// <c>origin_machine</c> the sinks stamp must come from one source, or a publisher reads as two
/// machines with one of them permanently stale. The daemon hands both this and
/// <see cref="SinkContext"/> the same value resolved once at startup, so today a rename needs a
/// daemon restart to take effect on either — the delegate keeps them tied together, it does not
/// make either live.
/// </para>
/// </summary>
public sealed class HeartbeatWriter
{
    private readonly Func<IReadOnlyList<PublisherEntry>> _entries;
    private readonly Func<string> _originMachine;
    private readonly ILogger _logger;

    private DateTimeOffset _lastBeat = DateTimeOffset.MinValue;

    /// <summary>
    /// Last logged failure reason <b>per link</b>. One shared field would defeat itself the
    /// moment two file links are configured: the live link's success would clear the key the
    /// dead one had just set, and the warning this dedup exists to suppress would be written on
    /// every beat instead of once. Bounded by the number of registered links.
    /// </summary>
    private readonly Dictionary<string, string> _lastFailures = new(StringComparer.Ordinal);

    public HeartbeatWriter(
        Func<IReadOnlyList<PublisherEntry>> entries,
        Func<string> originMachine,
        ILogger logger)
    {
        _entries = entries;
        _originMachine = originMachine;
        _logger = logger;
    }

    /// <summary>
    /// Beats if a full <see cref="PublisherHeartbeat.Interval"/> has passed. Called on every
    /// loop tick; the period is counted here rather than by a timer of its own.
    /// </summary>
    /// <returns>True when this call actually wrote, for tests to step deterministically.</returns>
    public bool WriteIfDue(DateTimeOffset now)
    {
        if (now - _lastBeat < PublisherHeartbeat.Interval)
        {
            return false;
        }

        _lastBeat = now;

        var machine = _originMachine();
        var beat = Encoding.UTF8.GetBytes(PublisherHeartbeat.Format(now));

        foreach (var entry in _entries())
        {
            if (entry.Enabled && SinkFactory.IsFileEndpoint(entry.Endpoint))
            {
                TryWrite(entry, machine, beat);
            }
        }

        return true;
    }

    /// <summary>
    /// One beat, written directly rather than through <c>AtomicFileWriter</c> — the same rule
    /// D15 puts on session writes, since a rename across the mount is what produces the
    /// ambiguous <c>Deleted</c>-then-<c>Renamed</c> pair on the far side.
    /// <para>
    /// A failure here must never reach the daemon's loop: an unreachable mount is exactly the
    /// state the receiver is about to read as disconnected, and throwing would stop publishing
    /// as well. Repeats of the same reason are logged once, since a dead mount would otherwise
    /// write a warning every five seconds for as long as it stays dead.
    /// </para>
    /// </summary>
    private void TryWrite(PublisherEntry entry, string machine, byte[] beat)
    {
        var path = PublisherHeartbeat.PathFor(entry.Endpoint!, machine);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, beat);
            _lastFailures.Remove(entry.Name);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            if (_lastFailures.TryGetValue(entry.Name, out var previous) && previous == ex.Message)
            {
                return;
            }

            _lastFailures[entry.Name] = ex.Message;
            _logger.LogWarning(
                ex,
                "Heartbeat for {Machine} could not be written to link {Name} at {Path}",
                machine,
                entry.Name,
                path);
        }
    }
}
