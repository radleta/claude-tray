using System.Runtime.ExceptionServices;
using Imrdy.Core;
using Imrdy.Core.Hooks;
using Imrdy.Windows.Commands;
using Imrdy.Windows.DI;
using Imrdy.Windows.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Serilog;

namespace Imrdy.Windows;

internal static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        // Last-resort handler — catches AccessViolationException, SEHException, and other
        // corrupted-state exceptions that bypass managed try/catch and Application.ThreadException.
        // These commonly originate from undocumented COM vtable calls.
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
            {
                Log.Fatal(ex, "AppDomain unhandled exception (IsTerminating={IsTerminating})",
                    e.IsTerminating);
            }
            else
            {
                Log.Fatal("AppDomain unhandled exception (non-Exception): {Object}",
                    e.ExceptionObject);
            }

            Log.CloseAndFlush();
        };

        try
        {
            // Fast path — hook runs hundreds of times, minimal DI, bypass WinForms
            if (args.Length > 0 && args[0] == "hook")
            {
                using var services = HookServiceBuilder.Build();
                var hookLogger = services.GetRequiredService<ILoggerFactory>().CreateLogger("HookCommand");
                return HookCommand.Run(services, Console.In, new WindowsHookEnvironment(hookLogger));
            }

            // Management commands — Spectre.Console for rich output
            if (args.Length > 0 && args[0] is "status" or "packs" or "config" or "workspace"
                    or "links" or "stop" or "inspect-live" or "render-live" or "--help" or "-h" or "--version")
            {
                using var services = CliServiceBuilder.Build();
                return CommandRouter.Route(services, args);
            }

            // Developer preview harness — opens a DashboardForm from a fixture JSON.
            // Placed BETWEEN the Spectre CLI branch and the tray fallback: Spectre skips
            // WinForms init; preview needs it. Bypasses Global\ImrdyMonitor mutex intentionally
            // (preview is a standalone dev tool that must run while the real tray is running).
            if (args.Length > 0 && args[0] == "preview-dashboard")
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return PreviewDashboardCommand.Run(args);
            }

            // Render verb — produces artifacts (PNGs, JSON trees) of UI surfaces in-process.
            // Same placement rationale as preview-dashboard: Spectre branch skipped WinForms init,
            // but render needs it (Form.DrawToBitmap requires CreateControl/PerformLayout on an STA
            // thread with visual-styles enabled). Bypasses Global\ImrdyMonitor — render is a dev
            // tool that must run while the real tray is running.
            if (args.Length > 0 && args[0] == "render")
            {
                Application.SetHighDpiMode(HighDpiMode.SystemAware);
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                return RenderCommand.Run(args);
            }

            // Default: start the system tray monitor
            Application.SetHighDpiMode(HighDpiMode.SystemAware);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // Stress-test harness bypass: when IMRDY_STRESS_TEST=1, skip the single-instance
            // mutex so the test child can run alongside the developer's real tray. Never set in
            // production; the isolated IMRDY_HOME keeps config/session state separate.
            bool stressTest = Environment.GetEnvironmentVariable("IMRDY_STRESS_TEST") == "1";
            Mutex? mutex = null;
            if (!stressTest)
            {
                mutex = new Mutex(true, ImrdyPaths.MutexName, out bool created);
                if (!created)
                {
                    mutex.Dispose();
                    return 0; // Already running
                }
            }

            // Catch WinForms UI thread exceptions and log instead of crashing
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += (_, e) =>
            {
                Log.Error(e.Exception, "WinForms thread exception (swallowed)");
            };

            var monitorOptions = MonitorOptions.Parse(args);
            using var monitorServices = MonitorServiceBuilder.Build(monitorOptions);
            var trayApp = monitorServices.GetRequiredService<TrayApp>();
            Application.Run(trayApp);
            mutex?.Dispose();
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Fatal error: {ex.Message}");
            Log.Fatal(ex, "Unhandled exception");
            return 1;
        }
        finally
        {
            Log.CloseAndFlush();
        }
    }
}
