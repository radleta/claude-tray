using System.Reflection;
using System.Runtime.InteropServices;
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
            var publishers = new PublisherStore(ImrdyPaths.Publishers).Load();

            var vm = LinksReport.Build(
                publishers,
                ConfigReader.Read().Network,
                Environment.MachineName,
                Environment.GetEnvironmentVariable("WSL_DISTRO_NAME"),
                HeartbeatMachines.Read(
                    ImrdyPaths.Sessions,
                    publishers.Publishers.Select(entry => (string?)entry.Name)),
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
    /// Runs the publisher daemon until SIGINT or SIGTERM. Both are intercepted with
    /// <see cref="PosixSignalRegistration"/>, whose handler sets
    /// <c>PosixSignalContext.Cancel = true</c> so the runtime does not carry out its default
    /// action: the process stays alive, this method unwinds normally, and the lock and PID
    /// file are released through <c>Dispose</c>.
    /// <para>
    /// <c>Console.CancelKeyPress</c> used to serve SIGINT and was measured not to dispatch on
    /// Linux at all — under a real controlling terminal the daemon sat in <c>futex_do_wait</c>
    /// through Ctrl-C and never logged its stop line. SIGTERM was never a <c>CancelKeyPress</c>
    /// signal in the first place; it reached the token only through <c>ProcessExit</c>, which
    /// fires too late to unwind and left the runtime terminating at exit 143. So before this
    /// registration existed there was no path that unwound <c>Main</c>, and <c>daemon.lock</c>
    /// was released by process death on every ordinary stop.
    /// </para>
    /// <para>
    /// <c>ProcessExit</c> stays subscribed, and its remaining purpose is narrow enough to state
    /// exactly: on an exit these two registrations do not intercept — SIGHUP, SIGQUIT — it gives
    /// the unwind a chance to *start* inside the ProcessExit window. That race has been measured
    /// to lose: under the old build <c>ProcessExit</c> cancelled on SIGTERM and <c>Dispose</c>
    /// was still observed not to run. It is kept because it is two correctly-unsubscribed lines
    /// that cost nothing, not because it guarantees a clean shutdown anywhere — do not read it
    /// as covering those signals. Losing that race is benign for the same reason it always was:
    /// liveness is read from the lock on both sides —
    /// <c>DaemonLock.IsRunning</c> here and a non-blocking <c>flock</c> probe in
    /// <c>build-dev.sh</c> — and the kernel drops the <c>flock</c> when the process dies.
    /// Neither side reads the PID file to decide whether a daemon is up, and <c>kill -0</c> on
    /// that pid is specifically not the test: after a <c>wsl --terminate</c> the pid namespace
    /// restarts while the rootfs keeps the file, so a stale pid can be reused by an unrelated
    /// same-user process and would confirm. <c>daemon.pid</c> is only how the signal is
    /// addressed once the lock has already said something is alive.
    /// </para>
    /// </summary>
    private static int RunDaemon()
    {
        using var cts = new CancellationTokenSource();

        // Declared after `cts` so `using var` disposes them BEFORE it: a signal delivered
        // during teardown must not reach a handler holding a disposed source. That ordering is
        // what the `finally` below does by hand for ProcessExit, which is not an IDisposable.
        using var sigInt = PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        using var sigTerm = PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);

        void OnSignal(PosixSignalContext ctx)
        {
            ctx.Cancel = true;
            cts.Cancel();
        }

        EventHandler onProcessExit = (_, _) => cts.Cancel();
        AppDomain.CurrentDomain.ProcessExit += onProcessExit;

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
        finally
        {
            // The handler captures the CancellationTokenSource the `using` above disposes on
            // the way out, and the runtime raises ProcessExit on *every* exit — including the
            // ordinary return this method has just reached. Left subscribed, the handler fired
            // against the disposed source and every normal daemon exit ended in an
            // ObjectDisposedException trace and `Aborted (core dumped)`; the second-instance
            // path merely returns fast enough to make it obvious. Unsubscribing here is safe
            // because a cancellation that still matters has already been delivered: the daemon
            // loop is the only thing the token drives and it has returned. The two signal
            // registrations need no line here — `using var` disposes them on the way out of
            // this method, and because they are declared after `cts` they go first.
            AppDomain.CurrentDomain.ProcessExit -= onProcessExit;
        }
    }
}
