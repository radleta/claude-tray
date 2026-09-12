using System.Text.Json;
using Imrdy.Core;
using Imrdy.Core.Diagnostics;
using Imrdy.Core.Publishing;
using Imrdy.Windows.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Imrdy.Windows.Commands;

/// <summary>
/// Reports every registered link in both directions (D26), rendering the same
/// <see cref="ConnectionRow"/> the connections window does through the same
/// <see cref="ConnectionRowFormatter"/>. Human output: a Spectre table. JSON output: the
/// <see cref="ConnectionsViewModel"/> itself.
/// <para>
/// This process holds no sinks and no listener of its own, so live health has to be asked
/// for: it queries the running tray over the <c>Local\ImrdyInspect</c> pipe — the seam
/// <c>inspect-live</c> and <c>render-live</c> already use — and falls back to the records in
/// <c>publishers.json</c>, with exit 0, when no tray answers (the user's ruling r-2). That
/// fallback is the <em>normal</em> case in production, not an error: the pipe is gated on
/// <c>diagnostics.ipcEnabled ?? File.Exists(.dev-build)</c>, and a shipped install has no
/// marker. Which of the two happened is stated in the output, because a guard the operator
/// cannot tell apart from a no-op is not a guard.
/// </para>
/// <para>
/// A file-sink publisher is reported as <c>FileSink</c> and never as <c>Connected</c> (D27).
/// </para>
/// </summary>
internal static class LinksCommand
{
    /// <summary>Width assumed when stdout is redirected — enough that no cell wraps.</summary>
    private const int RedirectedWidth = 200;

    /// <summary>
    /// Connect budget for the tray query. Short on purpose: the common production answer is
    /// "no pipe", and an operator running <c>imrdy links</c> should not wait on it.
    /// </summary>
    private static readonly TimeSpan LiveTimeout = TimeSpan.FromMilliseconds(500);

    public static int Run(ServiceProvider services, bool json)
    {
        var store = services.GetRequiredService<PublisherStore>();
        var console = services.GetRequiredService<IAnsiConsole>();

        try
        {
            var (vm, live, healthLine) = Resolve(store);

            if (json)
            {
                // The payload stays exactly the ConnectionsViewModel a script asked for, so
                // the source line goes to stderr where a pipe into jq never sees it.
                Console.Error.WriteLine(healthLine);
                Console.WriteLine(JsonSerializer.Serialize(vm, ImrdyJsonContext.Indented));
            }
            else
            {
                Render(console, vm, live, healthLine);
            }

            return LinksReport.ExitCode(vm);
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }
    }

    /// <summary>
    /// The tray's live view if one answers usefully, the records otherwise.
    /// <para>
    /// Five paths fall back rather than throwing, and they are not the same failure, so each
    /// composes its own health line rather than leaving the caller to guess a cause from a null.
    /// An <c>IMRDY_HOME</c> override and two exception arms mean no tray is on the other end. A
    /// tray that answered and <em>refused</em> — a handler exception, the server's 2-second
    /// budget expiring, or <c>unknown verb</c>, which is what an older tray beside a newer CLI
    /// returns during an upgrade — is the case that makes falling back worth having at all, and
    /// its refusal is echoed back. A tray that accepted the connection and then went silent past
    /// the client's exchange deadline is the fifth, and it is neither of the others: the process
    /// is running and holding the pipe, so both "no tray answered" and "answered with an error"
    /// would send the operator somewhere wrong.
    /// </para>
    /// <para>
    /// Everything else is a real defect — a malformed response or a serialization fault — and
    /// reaches <see cref="Run"/>'s exit 2 rather than being disguised as an absent tray.
    /// </para>
    /// </summary>
    /// <returns>
    /// The rows, whether they are live, and the one line telling the operator which of those
    /// cases produced them — which they are told to read before trusting the exit code.
    /// </returns>
    private static (ConnectionsViewModel Vm, bool Live, string HealthLine) Resolve(PublisherStore store)
    {
        // IMRDY_HOME says "report this state". A running tray is serving whatever home it was
        // started with, which under an override is a different one — so its live health would
        // answer a question nobody asked. The records are the only honest answer here.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IMRDY_HOME")))
        {
            return (BuildFromRecords(store), false, LinksReport.RecordsOnly(null));
        }

        try
        {
            var response = InspectIpcClient.Send(
                new InspectRequest("links-live", string.Empty, null), LiveTimeout);

            if (response.Error is null && response.Links is { } live)
            {
                return (live, true, LinksReport.LiveHealth);
            }

            return (BuildFromRecords(store), false,
                LinksReport.RecordsOnly(DescribeRefusal(response)));
        }
        catch (InvalidOperationException)
        {
            // No server listening within the budget: the tray is down, or diagnostics IPC is
            // off, which is the shipped default.
        }
        catch (IOException)
        {
            // The pipe went away mid-exchange — a tray shutting down while we asked.
        }
        catch (TimeoutException)
        {
            // A tray accepted the connection and then did not finish the exchange within the
            // client's deadline: wedged, or shutting down mid-answer. Records-only is the right
            // answer, but "no tray answered" is not — the process is running and holding the
            // pipe, and saying otherwise sends the operator hunting something that is not the
            // problem. This is the one cause the exchange deadline exists to detect.
            return (BuildFromRecords(store), false, LinksReport.RecordsOnlyUnresponsive(
                $"{InspectIpcClient.ExchangeTimeout.TotalSeconds:0.#}s"));
        }

        return (BuildFromRecords(store), false, LinksReport.RecordsOnly(null));
    }

    /// <summary>Longest refusal reason echoed back; an exception message can be a stack-sized string.</summary>
    private const int MaxRefusalLength = 120;

    /// <summary>
    /// What to tell the operator about a response that arrived and was not usable. A response
    /// with no error and no payload is its own case: the verb ran and returned nothing, which
    /// no server-side path produces today and so says exactly that rather than inventing a
    /// cause.
    /// </summary>
    private static string DescribeRefusal(InspectResponse response)
    {
        var reason = response.Error ?? "no links payload in the response";
        reason = reason.ReplaceLineEndings(" ").Trim();

        return reason.Length > MaxRefusalLength
            ? reason[..MaxRefusalLength] + "…"
            : reason;
    }

    /// <summary>
    /// The records-only fallback, plus the one live signal a process with no tray can still
    /// read for itself: the file-sink heartbeats on disk. Without them this path stays blind to
    /// a publisher that is delivering right now, since it opens no socket and the operator may
    /// well never have registered it (f-filesink-no-socket).
    /// </summary>
    private static ConnectionsViewModel BuildFromRecords(PublisherStore store)
    {
        var publishers = store.Load();

        return LinksReport.Build(
            publishers,
            ConfigReader.Read().Network,
            Environment.MachineName,
            Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"),
            HeartbeatMachines.Read(
                ImrdyPaths.Sessions,
                publishers.Publishers.Select(entry => (string?)entry.Name)),
            DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Eight columns do not fit the 80 that Spectre assumes when stdout is redirected, and a
    /// wrapped table shows <c>FileSin/k</c> split across two lines — unreadable in a pipe and
    /// ungreppable in a script. A redirected stream has no width of its own, so widening the
    /// profile costs an interactive terminal nothing: that case keeps its real width.
    /// UTF-8 is forced for the same reason, or the em dash arrives as mojibake through a pipe.
    /// </summary>
    private static void WidenForRedirectedOutput(IAnsiConsole console)
    {
        if (!Console.IsOutputRedirected) return;

        Console.OutputEncoding = System.Text.Encoding.UTF8;
        console.Profile.Width = RedirectedWidth;
    }

    private static void Render(IAnsiConsole console, ConnectionsViewModel vm, bool live, string healthLine)
    {
        WidenForRedirectedOutput(console);

        console.MarkupLine($"[bold]{Markup.Escape(vm.MachineName)}[/]");
        console.MarkupLine(vm.ListenEnabled
            ? $"[dim]Listening on port {vm.ListenPort}[/]"
            : $"[yellow]Not listening[/] [dim]— {Markup.Escape(ConnectionRowFormatter.NotListening)}[/]");

        // D10's key is what makes a misconfigured machine fail loudly rather than inject
        // sessions into the wrong tray. Listening without one is a state, not a default worth
        // leaving unsaid.
        if (vm.ListenEnabled && !vm.AuthKeyConfigured)
        {
            console.MarkupLine("[yellow]No auth key[/] [dim]— every publisher that reaches this port is accepted; set network.authKey[/]");
        }

        // r-2: composed by LinksReport, so this and the Linux binary cannot drift on what a
        // records-only run means.
        console.MarkupLine(live
            ? $"[dim]{Markup.Escape(healthLine)}[/]"
            : $"[yellow]{Markup.Escape(healthLine)}[/]");
        console.WriteLine();

        if (vm.Rows.Count == 0)
        {
            console.MarkupLine($"[dim]{Markup.Escape(ConnectionRowFormatter.NoLinks)}[/]");
            return;
        }

        var table = new Table().Border(TableBorder.Rounded);
        foreach (var header in LinksReport.ColumnHeaders())
        {
            table.AddColumn(new TableColumn($"[bold]{header}[/]"));
        }

        foreach (var row in vm.Rows)
        {
            var cells = LinksReport.Cells(row);
            table.AddRow(
                Markup.Escape(cells[0]),
                $"[dim]{Markup.Escape(cells[1])}[/]",
                Colorize(cells[2]),
                Colorize(cells[3]),
                Markup.Escape(cells[4]),
                Markup.Escape(cells[5]),
                $"[dim]{Markup.Escape(cells[6])}[/]",
                cells[7].Length == 0 ? string.Empty : $"[red]{Markup.Escape(cells[7])}[/]");
        }

        console.Write(table);
    }

    /// <summary>
    /// The two state columns carry the only colour, so a bad link is findable without reading
    /// every row — the same rule the window's list follows.
    /// </summary>
    private static string Colorize(string state) => state switch
    {
        nameof(SinkState.Connected) => $"[green]{state}[/]",
        nameof(SinkState.Failed) => $"[red]{state}[/]",
        _ => $"[dim]{Markup.Escape(state)}[/]",
    };
}
