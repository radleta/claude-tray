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
            var (vm, live, refusal) = Resolve(store);
            var healthLine = live ? LinksReport.LiveHealth : LinksReport.RecordsOnly(refusal);

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
    /// Four failures fall back rather than throwing, and they are not the same failure. Three
    /// mean no tray is on the other end. The fourth is a tray that answered and refused — a
    /// handler exception, the server's 2-second budget expiring, or <c>unknown verb</c>, which
    /// is what an older tray beside a newer CLI returns during an upgrade. That last case is
    /// the reason falling back is worth having at all, and it is also why the refusal comes
    /// back to the caller: telling the operator "no tray answered" when one did sends them
    /// looking for a stopped process that is running.
    /// </para>
    /// <para>
    /// Everything else is a real defect — a malformed response or a serialization fault — and
    /// reaches <see cref="Run"/>'s exit 2 rather than being disguised as an absent tray.
    /// </para>
    /// </summary>
    /// <returns>
    /// The rows, whether they are live, and — when a tray answered and refused — what it said.
    /// A null refusal on a non-live result means nothing answered.
    /// </returns>
    private static (ConnectionsViewModel Vm, bool Live, string? Refusal) Resolve(PublisherStore store)
    {
        // IMRDY_HOME says "report this state". A running tray is serving whatever home it was
        // started with, which under an override is a different one — so its live health would
        // answer a question nobody asked. The records are the only honest answer here.
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("IMRDY_HOME")))
        {
            return (BuildFromRecords(store), false, null);
        }

        try
        {
            var response = InspectIpcClient.Send(
                new InspectRequest("links-live", string.Empty, null), LiveTimeout);

            if (response.Error is null && response.Links is { } live)
            {
                return (live, true, null);
            }

            return (BuildFromRecords(store), false, DescribeRefusal(response));
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
            // The read phase's remaining budget expired; Send only wraps the connect phase.
        }

        return (BuildFromRecords(store), false, null);
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

    private static ConnectionsViewModel BuildFromRecords(PublisherStore store) =>
        LinksReport.Build(
            store.Load(),
            ConfigReader.Read().Network,
            Environment.MachineName,
            Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"),
            DateTimeOffset.UtcNow);

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
            : "[yellow]Not listening[/] [dim]— inbound publishers cannot reach this machine[/]");

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
            console.MarkupLine("[dim]No links registered.[/]");
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
