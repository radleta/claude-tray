using Microsoft.Extensions.DependencyInjection;

namespace Imrdy.Core.Publishing;

/// <summary>
/// DI composition for <c>imrdy daemon</c>, the Linux publisher host.
/// <para>
/// Deliberately separate from <see cref="Imrdy.Core.Hooks.HookServiceBuilder"/>. The sinks
/// and <see cref="PublisherStore"/> register here and in the tray, never in the hook
/// builder: D6 says the hook does not publish, and a sink registered in the hook's builder
/// would put a socket in the hook's dependency graph.
/// </para>
/// </summary>
public static class DaemonServiceBuilder
{
    public static ServiceProvider Build(bool verbose = false, bool quiet = false)
    {
        var services = new ServiceCollection();
        services.AddCoreServices();
        services.AddSerilog(verbose: verbose, quiet: quiet, fileSink: true, logPath: ImrdyPaths.DaemonLog);

        // Explicitly constructed, following the WorkspaceStore precedent in AddCoreServices,
        // because it takes a file path.
        services.AddSingleton(new PublisherStore(ImrdyPaths.Publishers));

        return services.BuildServiceProvider();
    }
}
