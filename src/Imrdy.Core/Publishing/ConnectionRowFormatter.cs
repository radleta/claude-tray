using Imrdy.Core.Time;

namespace Imrdy.Core.Publishing;

/// <summary>
/// Turns one <see cref="ConnectionRow"/> into the cell strings both surfaces show — the
/// connections window's list and <c>imrdy links</c>. The two surfaces present those strings
/// differently (a <c>ListView</c>, a Spectre table, plain stdout on Linux), but what a cell
/// says is decided exactly once, here, so the window and the CLI cannot disagree about
/// whether a link is healthy.
/// </summary>
public static class ConnectionRowFormatter
{
    /// <summary>Placeholder for a cell that has no value, rather than an empty gap.</summary>
    public const string None = "—";

    /// <summary>
    /// Three different absences, kept apart, because they need three different actions from
    /// the operator: a machine with no record at all (legitimate — a receiver holds no
    /// allow-list, D24), a record that deliberately carries no endpoint because it is
    /// receive-only (legitimate — r-1), and a record whose endpoint is present but blank
    /// (malformed — <see cref="SinkFactory.TryCreate"/> logs it and builds nothing). Rendering
    /// the third as the second tells an operator who blanked a field by hand that the record
    /// is configured exactly as intended.
    /// </summary>
    public static string Endpoint(ConnectionRow row) => Kind(row) switch
    {
        EndpointKind.Present => row.Endpoint!.Trim(),
        EndpointKind.ReceiveOnly => ReceiveOnly,
        EndpointKind.Malformed => Malformed,
        _ => "(not registered)",
    };

    /// <summary>What a registered record with no endpoint says in both the endpoint and outbound cells.</summary>
    public const string ReceiveOnly = "receive-only";

    /// <summary>What a registered record with a blank endpoint says in both those cells.</summary>
    public const string Malformed = "(blank endpoint)";

    private enum EndpointKind { Present, ReceiveOnly, Malformed, Unregistered }

    /// <summary>
    /// The single emptiness test for this class. Both cells used to ask the question their own
    /// way — one on <c>Length &gt; 0</c>, one on <c>IsNullOrWhiteSpace</c> — and disagreed on a
    /// whitespace-only endpoint, which rendered blank in one column and receive-only in the
    /// next. Null-versus-blank matches <see cref="SinkFactory.TryCreate"/>'s own split.
    /// </summary>
    private static EndpointKind Kind(ConnectionRow row)
    {
        if (!string.IsNullOrWhiteSpace(row.Endpoint)) return EndpointKind.Present;
        if (!row.IsRegistered) return EndpointKind.Unregistered;
        return row.Endpoint is null ? EndpointKind.ReceiveOnly : EndpointKind.Malformed;
    }

    /// <summary>
    /// A row with no health record is not "Failed" — it is a link that was never built, and
    /// saying so is the difference between a diagnosis and a false alarm.
    /// </summary>
    public static string Outbound(ConnectionRow row)
    {
        if (row.Outbound is { } health) return health.State.ToString();
        if (!row.IsRegistered) return None;
        if (!row.Enabled) return "disabled";

        // r-1: a record with no endpoint is receive-only by the operator's choice, so there is
        // nothing to dial and nothing missing. "no sink" here would report a fault — the same
        // argument D27 makes for a file sink. A blank endpoint is the opposite: a fault the
        // operator has to act on, and the same word for both would hide it.
        switch (Kind(row))
        {
            case EndpointKind.ReceiveOnly: return ReceiveOnly;
            case EndpointKind.Malformed: return Malformed;
        }

        // A CLI process has no live tray and so no health table at all. A file-sink link has
        // no connection to report in either direction (D27) and its endpoint is the only
        // thing that ever says so, so calling it "no sink" would report a fault it cannot
        // have.
        return SinkFactory.IsFileEndpoint(row.Endpoint) ? nameof(SinkState.FileSink) : "no sink";
    }

    /// <summary>
    /// Inbound wording differs from outbound on purpose: outbound has a sink or does not,
    /// inbound has only ever been connected to or not.
    /// </summary>
    public static string Inbound(ConnectionRow row) =>
        row.Inbound is { } health ? health.State.ToString() : "never";

    /// <summary>
    /// The one clock read on this path, and it happens while the row is being built rather
    /// than while it is being painted — see <see cref="ConnectionRow.LastDelivery"/>. Surfaces
    /// read the string off the row.
    /// </summary>
    public static string LastDelivery(SinkHealth? outbound, SinkHealth? inbound, DateTimeOffset now)
    {
        var last = Newest(outbound?.LastSuccessAt, inbound?.LastSuccessAt);
        return last is null
            ? "never"
            : RelativeTimeFormatter.FormatDuration(now - last.Value) + " ago";
    }

    public static string Desktop(ConnectionRow row) => row.DesktopIndex?.ToString() ?? None;

    public static string Notify(ConnectionRow row) => row.Muted ? "muted" : None;

    public static string LastError(ConnectionRow row) =>
        row.Outbound?.LastError ?? row.Inbound?.LastError ?? string.Empty;

    private static DateTimeOffset? Newest(DateTimeOffset? a, DateTimeOffset? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a > b ? a : b;
    }
}
