using System.Reflection;
using System.Text.Json;
using Imrdy.Core;
using Imrdy.Core.Hooks;
using Imrdy.Core.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Imrdy.Linux;

internal static class Program
{
    static int Main(string[] args)
    {
        if (args.Length > 0 && args[0] == "hook")
        {
            try
            {
                using var services = HookServiceBuilder.Build();
                var hookLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger("HookCommand");
                _ = HookCommand.Run(services, Console.In, new LinuxHookEnvironment(hookLogger));
            }
            catch (Exception ex)
            {
                // Never fail the Claude session — hook errors are logged inside HookCommand.Run.
                // Exceptions here are unexpected (e.g., DI build failure before the logger is
                // available); write to stderr so the operator can diagnose from hook process output.
                Console.Error.WriteLine($"imrdy hook: unexpected error: {ex}");
            }

            return 0;
        }

        if (args.Length > 0 && args[0] == "daemon")
        {
            return RunDaemon();
        }

        if (args.Length > 0 && args[0] == "links")
        {
            return RunLinks(args[1..].Any(a => a == "--json"));
        }

        if (args.Length > 0 && (args[0] == "--version" || args[0] == "-v"))
        {
            var version = Assembly.GetExecutingAssembly()
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString()
                ?? "dev";
            Console.WriteLine($"imrdy {version}");
            return 0;
        }

        if (args.Length == 0 || args[0] == "--help" || args[0] == "-h")
        {
            Console.WriteLine("imrdy - Claude Code session monitor hook");
            Console.WriteLine();
            Console.WriteLine("Usage:");
            Console.WriteLine("  imrdy hook          Process a Claude Code hook event from stdin");
            Console.WriteLine("  imrdy daemon        Publish this machine's sessions to registered receivers");
            Console.WriteLine("  imrdy links [--json]  Show this machine's registered links from publishers.json");
            Console.WriteLine("                        Records only on Linux, and it says so on its own 'health:' line:");
            Console.WriteLine("                        live health lives in the Windows tray, which this binary cannot reach,");
            Console.WriteLine("                        so no link can report as failed here and this always exits 0");
            Console.WriteLine("  imrdy --version     Show version");
            Console.WriteLine("  imrdy --help        Show this help");
            return 0;
        }

        Console.Error.WriteLine($"imrdy: unrecognized command '{args[0]}'");
        Console.Error.WriteLine("Run 'imrdy --help' for usage.");
        return 1;
    }

    /// <summary>
    /// Reports this machine's registered links. Plain stdout with an optional --json, adding
    /// no CLI framework to this binary (D26); the rendering itself lives in
    /// <see cref="LinksReport"/> so it is testable from Imrdy.Core.Tests, which is the only
    /// test project that can reach it.
    /// <para>
    /// Records only, and <c>live: false</c> is passed rather than assumed: r-2's live query is
    /// the Windows tray's <c>Local\ImrdyInspect</c> pipe, which is a Windows tray on the
    /// <em>receiver</em> and not something this binary has a path to. So the guard case never
    /// arises here, and the rendered <c>health:</c> line says exactly that rather than letting
    /// a clean run read as a healthy one.
    /// </para>
    /// </summary>
    private static int RunLinks(bool json)
    {
        try
        {
            var vm = LinksReport.Build(
                new PublisherStore(ImrdyPaths.Publishers).Load(),
                ConfigReader.Read().Network,
                Environment.MachineName,
                Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"),
                DateTimeOffset.UtcNow);

            if (json)
            {
                // Stderr, so a pipe into jq gets only the payload and the operator still sees
                // which of r-2's two cases produced it.
                Console.Error.WriteLine(LinksReport.HealthSource(live: false));
                Console.WriteLine(JsonSerializer.Serialize(vm, ImrdyJsonContext.Indented));
            }
            else
            {
                foreach (var line in LinksReport.RenderLines(vm, live: false))
                {
                    Console.WriteLine(line);
                }
            }

            return LinksReport.ExitCode(vm);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"imrdy links: {ex.Message}");
            return 2;
        }
    }

    /// <summary>
    /// Runs the publisher daemon until SIGINT or SIGTERM. Both are handled so a distro
    /// shutdown releases the lock through <c>Dispose</c> rather than leaving the kernel to
    /// drop it on process death — which works, but logs nothing and clears no PID file.
    /// </summary>
    private static int RunDaemon()
    {
        using var cts = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => cts.Cancel();

        try
        {
            using var services = DaemonServiceBuilder.Build();
            var daemonLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger("DaemonCommand");
            return DaemonCommand
                .RunAsync(services, new LinuxHookEnvironment(daemonLogger).GetWslDistro(), cts.Token)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            // The daemon has no caller to report to and its logger may not exist yet if DI
            // itself failed, so stderr is the only channel left.
            Console.Error.WriteLine($"imrdy daemon: fatal error: {ex}");
            return 1;
        }
    }
}
