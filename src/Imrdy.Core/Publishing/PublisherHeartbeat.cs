namespace Imrdy.Core.Publishing;

/// <summary>
/// The file sink's liveness signal, and the one place its shape, its path and its staleness
/// threshold are defined. Both ends read this class: the publisher to write a beat, the
/// receiver to decide whether one has gone stale.
/// <para>
/// <b>Why it exists.</b> A TCP link gets liveness free from its socket — the connection
/// dropping <em>is</em> the signal, which is why <c>WireListener</c> can flip a publisher to
/// <see cref="SinkState.Failed"/> and why D20's disconnected treatment works on that path. A
/// file-sink publisher opens no connection, so the receiver had nothing to observe and a
/// terminated WSL distro rendered exactly like a merely-quiet one. This gives the file sink
/// what TCP gets from its transport: an explicit positive signal, written on a fixed interval
/// by the exact process whose liveness is being measured.
/// </para>
/// <para>
/// <b>It is not inference from silence.</b> The mechanism <c>agent-liveness-roster</c> deleted
/// read liveness out of the <em>absence</em> of unrelated events, so a quiet-but-working
/// session looked gone. The beat here is written unconditionally on the daemon's own tick and
/// never on session activity (see <see cref="HeartbeatWriter"/>), so a publisher with nothing
/// to say keeps beating and "quiet" can never be read as "gone".
/// </para>
/// <para>
/// <b>Not a session file.</b> The beat lives in a <c>heartbeats</c> directory beside
/// <c>sessions</c>, not inside it: the receiver's watcher and
/// <c>StateFileReader.ReadAllStateFiles</c> treat that directory as sessions, and a file there
/// that failed to parse as a <c>StateFileModel</c> is a phantom tray dot waiting to happen.
/// The sibling sits on the same mount, so it inherits the same measured drvfs latency the
/// threshold below is derived from.
/// </para>
/// <para>
/// The payload is one ISO-8601 round-trip timestamp and nothing else — no JSON — so it needs
/// no <c>ImrdyJsonContext</c> registration and a torn read simply fails to parse.
/// </para>
/// </summary>
public static class PublisherHeartbeat
{
    /// <summary>The directory that holds beats, a sibling of <c>sessions</c>.</summary>
    public const string DirectoryName = "heartbeats";

    /// <summary>Extension for one publisher's beat file. Deliberately not <c>.json</c>.</summary>
    public const string FileExtension = ".hb";

    /// <summary>
    /// Longest filename token derived from a machine name. Matches
    /// <c>WireListener.MaxMachineNameLength</c>, so the two bounds on the same string agree.
    /// </summary>
    private const int MaxTokenLength = 64;

    /// <summary>Token used when a machine name sanitizes to nothing at all.</summary>
    private const string FallbackToken = "unnamed";

    /// <summary>
    /// How often a publisher writes its beat. Counted off the daemon's existing 200 ms drain
    /// tick rather than by a timer of its own, so this is a period, not a timer interval.
    /// Five seconds costs one small write per publisher per five seconds across drvfs, which
    /// is two orders of magnitude below the per-event session writes already crossing it.
    /// </summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// How old a beat may get before its publisher reads as disconnected. <b>Derived, not
    /// chosen by feel</b> — re-derive it rather than nudging it:
    /// <list type="number">
    /// <item>One beat period is <see cref="Interval"/>, 5 s.</item>
    /// <item>A beat is not visible to the receiver the instant it is written. The live
    /// two-machine pass measured the WSL-write-to-Windows-observation band on this same mount
    /// at 144–257 ms, so round the upper end to 1 s.</item>
    /// <item>The write rides a loop tick that also drains publishing, so one beat can slip a
    /// tick without the publisher being gone. Tolerating two consecutive misses gives three
    /// periods, 15 s.</item>
    /// </list>
    /// 15 s + 1 s, rounded up to <b>20 s</b> for margin. The floor this must clear is
    /// <see cref="Interval"/> plus that latency band, about 5.3 s; anything below that reports
    /// a live publisher as gone. Raising <see cref="Interval"/> means re-deriving this.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromSeconds(20);

    /// <summary>
    /// The heartbeat directory beside a sessions directory. Takes the sessions directory
    /// rather than a root because that is what both ends already hold: the publisher has its
    /// file-sink endpoint, the receiver has <c>ImrdyPaths.Sessions</c>.
    /// </summary>
    public static string DirectoryFor(string sessionsDirectory)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sessionsDirectory));
        var parent = Path.GetDirectoryName(full);
        return Path.Combine(string.IsNullOrEmpty(parent) ? full : parent, DirectoryName);
    }

    /// <summary>Full path of one publisher's beat file, given the sessions directory it publishes into.</summary>
    public static string PathFor(string sessionsDirectory, string machine) =>
        Path.Combine(DirectoryFor(sessionsDirectory), TokenFor(machine) + FileExtension);

    /// <summary>
    /// Maps a machine name to a filename token. A machine name is a free-form operator-set
    /// string that reaches this from config or from another machine's <c>origin_machine</c>,
    /// so it never becomes a path component as written: every character outside
    /// <c>[A-Za-z0-9_-]</c> becomes <c>_</c>, which leaves no separator, no drive letter and
    /// no <c>..</c> for a path to escape on. Lower-cased because links are matched
    /// case-insensitively everywhere else and a case-sensitive filesystem would otherwise
    /// split one publisher into two.
    /// </summary>
    public static string TokenFor(string? machine)
    {
        if (string.IsNullOrWhiteSpace(machine))
        {
            return FallbackToken;
        }

        var trimmed = machine.Trim();
        var length = Math.Min(trimmed.Length, MaxTokenLength);
        var token = new char[length];

        for (var i = 0; i < length; i++)
        {
            var c = trimmed[i];
            token[i] = char.IsAsciiLetterOrDigit(c) || c is '_' or '-'
                ? char.ToLowerInvariant(c)
                : '_';
        }

        return new string(token);
    }

    /// <summary>Renders a beat. Round-trip format so it parses back exactly, offset included.</summary>
    public static string Format(DateTimeOffset beat) => beat.ToString("O");

    /// <summary>
    /// Reads a beat back. Exact rather than lenient on purpose: the format is ours, and a beat
    /// torn mid-write is often still a <em>parseable</em> timestamp under lenient rules — just
    /// the wrong one, which would be read as a live publisher's age. Exact parsing turns that
    /// into a clean miss instead.
    /// </summary>
    public static bool TryParse(string? text, out DateTimeOffset beat)
    {
        beat = default;
        return !string.IsNullOrWhiteSpace(text)
               && DateTimeOffset.TryParseExact(
                   text.Trim(),
                   "O",
                   System.Globalization.CultureInfo.InvariantCulture,
                   System.Globalization.DateTimeStyles.RoundtripKind,
                   out beat);
    }

    /// <summary>
    /// Whether a beat is old enough to call its publisher gone. A beat in the future — a
    /// publisher whose clock runs ahead — yields a negative age and reads as fresh, which is
    /// the direction to fail in: D20 would rather show a departed publisher for a few extra
    /// seconds than mark a live one disconnected.
    /// </summary>
    public static bool IsStale(DateTimeOffset beat, DateTimeOffset now) => now - beat > StaleAfter;
}
