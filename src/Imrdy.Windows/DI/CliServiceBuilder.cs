using Imrdy.Core;
using Imrdy.Core.Publishing;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;

namespace Imrdy.Windows.DI;

/// <summary>
/// DI composition for CLI management commands.
/// Registers Core services + validators + Spectre IAnsiConsole. No WinForms.
/// </summary>
public static class CliServiceBuilder
{
    public static ServiceProvider Build(bool verbose = false, bool quiet = false)
    {
        var services = new ServiceCollection();
        services.AddCoreServices();
        services.AddSerilog(verbose: verbose, quiet: quiet);
        services.AddSingleton(AnsiConsole.Console);
        // Explicitly constructed, following the WorkspaceStore precedent in AddCoreServices,
        // because it takes a file path. No sink is registered here: a CLI process reports on
        // links, it does not open them (D6).
        services.AddSingleton(new PublisherStore(ImrdyPaths.Publishers));
        return services.BuildServiceProvider();
    }
}
