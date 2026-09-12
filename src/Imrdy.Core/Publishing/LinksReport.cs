using System.Text;

namespace Imrdy.Core.Publishing;

/// <summary>
/// What <c>imrdy links</c> is, minus its presentation: the records-only view model a CLI
/// process can build for itself, the exit code it reports, and the plain-stdout rendering the
/// Linux binary uses.
/// <para>
/// A CLI process holds no sinks and no listener of its own, so what <see cref="Build"/>
/// produces is records-only: no link state, every registered row read from
/// <c>publishers.json</c>. The one thing it does see without a tray is a file-sink publisher's
/// heartbeat, which is a file on disk rather than a socket somebody else is holding — without
/// it a publisher actively delivering here would print nothing at all. Under the
/// user's ruling r-2 that is the <em>fallback</em>, not the whole command: the Windows binary
/// first asks the running tray for its live <see cref="ConnectionsViewModel"/> over the
/// <c>Local\ImrdyInspect</c> pipe, and falls back here when no tray answers. Either way both
/// surfaces render the same <see cref="ConnectionRow"/> through the same
/// <see cref="ConnectionRowFormatter"/>, so the CLI is a view of one report, not a second one.
/// </para>
/// </summary>
public static class LinksReport
{
    /// <summary>
    /// Exit code when at least one link is <see cref="SinkState.Failed"/>.
    /// <para>
    /// Reachable only from live health (r-2): a view model the running tray supplied carries
    /// real <see cref="SinkHealth"/> records and so can have a failed row, while one
    /// <see cref="Build"/> produced cannot. That is the whole of the guard's conditionality —
    /// with a tray to ask, the command guards a shell; without one it reports records and exits
    /// 0. Both binaries' output says which of the two happened, because a guard the operator
    /// cannot tell apart from a no-op is not a guard.
    /// </para>
    /// </summary>
    public const int ExitFailedLink = 1;

    private static readonly string[] Headers =
        ["MACHINE", "ENDPOINT", "OUTBOUND", "INBOUND", "DESKTOP", "NOTIFY", "LAST DELIVERY", "LAST ERROR"];

    /// <param name="heartbeats">
    /// File-sink publishers known from their beats. This process has no listener to enumerate
    /// publishers from, and a file sink would not appear in one anyway (<c>f-filesink-no-socket</c>),
    /// so without this a publisher actively delivering into this machine prints nothing at all.
    /// <see cref="HeartbeatMachines.Read"/> is what a CLI process fills it from.
    /// </param>
    public static ConnectionsViewModel Build(
        PublisherConfig publishers,
        NetworkConfig network,
        string hostName,
        string? wslDistro,
        IReadOnlyList<MachineBeat> heartbeats,
        DateTimeOffset now) =>
        ConnectionsViewModelBuilder.Build(
            publishers,
            outbound: [],
            inbound: [],
            heartbeats,
            MachineNameResolver.Resolve(network.MachineName, hostName, wslDistro),
            network.ListenEnabled,
            network.ListenPort,
            !string.IsNullOrEmpty(network.AuthKey),
            now);

    public static int ExitCode(ConnectionsViewModel vm) =>
        vm.Rows.Any(row => row.IsFailed) ? ExitFailedLink : 0;

    /// <summary>The eight cells of one row, in header order.</summary>
    public static string[] Cells(ConnectionRow row) =>
    [
        row.Name,
        ConnectionRowFormatter.Endpoint(row),
        ConnectionRowFormatter.Outbound(row),
        ConnectionRowFormatter.Inbound(row),
        ConnectionRowFormatter.Desktop(row),
        ConnectionRowFormatter.Notify(row),
        row.LastDelivery,
        ConnectionRowFormatter.LastError(row),
    ];

    public static string[] ColumnHeaders() => (string[])Headers.Clone();

    /// <summary>
    /// The one line that tells the operator which of r-2's two cases they are in, decided once
    /// here so the Spectre table and the plain Linux stdout cannot disagree about it. Without
    /// it a records-only run looks exactly like a live run in which nothing is wrong.
    /// </summary>
    /// <param name="live">True when these rows came from a running tray's live health.</param>
    public static string HealthSource(bool live) =>
        live ? LiveHealth : RecordsOnly(null);

    /// <summary>The live case: the tray answered and these rows carry its own <see cref="SinkHealth"/>.</summary>
    public const string LiveHealth = "health: live, from the running tray — a failed link exits 1";

    /// <summary>
    /// The records-only case, naming its cause. The operator was told to read this line before
    /// trusting the exit code, so it has to name the <em>right</em> cause: a tray that answered
    /// with an error is not an absent tray, and someone sent looking for a stopped process that
    /// is in fact running has been misdirected by the one line meant to orient them.
    /// </summary>
    /// <param name="reason">
    /// What the tray said, when one answered and refused; null when nothing answered at all.
    /// </param>
    public static string RecordsOnly(string? reason) =>
        Compose(reason is null
            ? "no tray answered"
            : $"the tray answered with an error ({reason})");

    /// <summary>
    /// The third cause, and the one neither of the other two describes: a tray that accepted the
    /// connection and then did not finish the exchange within the client's deadline. It is not
    /// absent — it holds the pipe — and it did not answer with an error, so saying either sends
    /// the operator somewhere wrong. A wedged tray is the case worth naming precisely, because
    /// the action it calls for (restart the tray) is not the action either other cause implies.
    /// </summary>
    /// <param name="detail">How long the client waited, for an operator sizing the problem.</param>
    public static string RecordsOnlyUnresponsive(string detail) =>
        Compose($"the tray accepted the connection but did not answer within {detail}");

    private static string Compose(string cause) =>
        $"health: records only, {cause} — link state is unknown and this run always exits 0";

    /// <summary>
    /// Column-aligned plain text for <c>Imrdy.Linux</c>, which adds no CLI framework (D26).
    /// Lives here rather than in <c>Program.cs</c> so it has a test: the Core test project
    /// does not reference <c>Imrdy.Linux</c>.
    /// </summary>
    /// <param name="live">True when <paramref name="vm"/> came from a running tray's live health (r-2).</param>
    public static IReadOnlyList<string> RenderLines(ConnectionsViewModel vm, bool live)
    {
        var lines = new List<string>
        {
            $"machine: {vm.MachineName}",
            vm.ListenEnabled
                ? $"listening: yes (port {vm.ListenPort}){(vm.AuthKeyConfigured ? "" : " — NO AUTH KEY: any peer that reaches this port is accepted")}"
                : $"listening: no ({ConnectionRowFormatter.NotListening})",
            HealthSource(live),
            string.Empty,
        };

        if (vm.Rows.Count == 0)
        {
            lines.Add(ConnectionRowFormatter.NoLinks);
            return lines;
        }

        var rows = vm.Rows.Select(Cells).ToList();
        var widths = Headers
            .Select((header, i) => rows.Aggregate(header.Length, (max, cells) => Math.Max(max, cells[i].Length)))
            .ToArray();

        lines.Add(Pad(Headers, widths));
        foreach (var cells in rows)
        {
            lines.Add(Pad(cells, widths));
        }

        return lines;
    }

    private static string Pad(IReadOnlyList<string> cells, IReadOnlyList<int> widths)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0) sb.Append("  ");
            // The last column is not padded: trailing spaces on every line are noise in a
            // terminal and worse in a diff.
            sb.Append(i == cells.Count - 1 ? cells[i] : cells[i].PadRight(widths[i]));
        }

        return sb.ToString().TrimEnd();
    }
}
