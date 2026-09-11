using Imrdy.Core;
using Imrdy.Core.Desktop;
using Imrdy.Core.Graphics;
using Imrdy.Core.Publishing;
using Imrdy.Core.Sound;
using Imrdy.Windows.Desktop;
using Imrdy.Windows.Icons;
using Imrdy.Windows.Models;
using Imrdy.Windows.Sound;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Imrdy.Windows.DI;

/// <summary>
/// DI composition for the system tray monitor.
/// Registers everything: Core services + WinForms UI + ISoundPlayer + IDesktopManager.
/// </summary>
public static class MonitorServiceBuilder
{
    public static ServiceProvider Build(MonitorOptions? options = null, bool verbose = false, bool quiet = false)
    {
        var services = new ServiceCollection();
        services.AddCoreServices();
        services.AddSerilog(verbose: verbose, quiet: quiet, fileSink: true);
        services.AddSingleton(options ?? new MonitorOptions());
        services.AddSingleton<ISoundPlayer, WinFormsSoundPlayer>();
        services.AddSingleton<IDesktopManager, ComVirtualDesktop>();
        services.AddSingleton<TrayIconRendererFactory>();
        services.AddSingleton<NotificationDwellState>();

        // Explicitly constructed, following the WorkspaceStore precedent in AddCoreServices,
        // because it takes a file path. Registered here and in DaemonServiceBuilder, never in
        // HookServiceBuilder: D6 keeps sockets out of the hook's dependency graph.
        services.AddSingleton(new PublisherStore(ImrdyPaths.Publishers));

        services.AddSingleton<TrayApp>();
        return services.BuildServiceProvider();
    }
}
