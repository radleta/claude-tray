using System.Text.Json;
using System.Text.Json.Nodes;
using Imrdy.Core;
using Imrdy.Core.Publishing;
using Imrdy.Core.Sound;
using Imrdy.Core.Validation;
using Microsoft.Extensions.DependencyInjection;
using Spectre.Console;
using Color = Spectre.Console.Color;
using Panel = Spectre.Console.Panel;

namespace Imrdy.Windows.Commands;

/// <summary>
/// Manages configuration: show, set, path, validate.
/// </summary>
internal static class ConfigCommand
{
    public static int Run(ServiceProvider services, string[] args, bool json)
    {
        if (args.Length == 0)
        {
            return ShowPath(services, json);
        }

        return args[0] switch
        {
            "show" => Show(services, json),
            "set" => Set(services, args[1..]),
            "path" => ShowPath(services, json),
            "validate" => Validate(services, json),
            _ => UnknownSubcommand(services, args[0]),
        };
    }

    private static int Show(ServiceProvider services, bool json)
    {
        var console = services.GetRequiredService<IAnsiConsole>();

        try
        {
            if (!File.Exists(ImrdyPaths.Config))
            {
                if (json)
                {
                    Console.WriteLine("{}");
                }
                else
                {
                    console.MarkupLine("[dim]No config file found.[/]");
                    console.MarkupLine($"[dim]Expected at: {Markup.Escape(ImrdyPaths.Config)}[/]");
                }

                return 0;
            }

            var content = File.ReadAllText(ImrdyPaths.Config);

            if (json)
            {
                Console.WriteLine(content);
                return 0;
            }

            // Pretty-print with Spectre JSON panel
            try
            {
                var doc = JsonDocument.Parse(content);
                var formatted = JsonSerializer.Serialize(doc, ImrdyJsonContext.Indented);
                console.MarkupLine("[bold]Configuration[/]");
                console.MarkupLine($"[dim]{Markup.Escape(ImrdyPaths.Config)}[/]");
                console.WriteLine();
                console.Write(new Panel(Markup.Escape(formatted))
                    .Header("config.json")
                    .BorderColor(Color.Grey));
            }
            catch (JsonException)
            {
                console.MarkupLine("[yellow]Warning: config.json contains invalid JSON[/]");
                console.WriteLine(content);
            }

            return 0;
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }
    }

    private static int Set(ServiceProvider services, string[] args)
    {
        var console = services.GetRequiredService<IAnsiConsole>();

        if (args.Length < 2)
        {
            console.MarkupLine("[red]Usage:[/] imrdy config set <key> <value>");
            console.MarkupLine("[dim]Keys: tray.enabled, sound.enabled, sound.defaultPack, sound.projects.<project>, overlay.enabled, overlay.position, overlay.size, overlay.spacing[/]");
            return 1;
        }

        var key = args[0];
        var value = args[1];

        try
        {
            if (string.Equals(key, "tray.enabled", StringComparison.OrdinalIgnoreCase))
            {
                if (!bool.TryParse(value, out var enabled))
                {
                    console.MarkupLine($"[red]Invalid value for '{Markup.Escape(key)}':[/] expected true/false, got '{Markup.Escape(value)}'");
                    return 1;
                }

                ConfigReader.Update(c => c with { Tray = c.Tray with { Enabled = enabled } });
            }
            else if (string.Equals(key, "sound.enabled", StringComparison.OrdinalIgnoreCase))
            {
                if (!bool.TryParse(value, out var enabled))
                {
                    console.MarkupLine($"[red]Invalid value for '{Markup.Escape(key)}':[/] expected true/false, got '{Markup.Escape(value)}'");
                    return 1;
                }

                ConfigReader.Update(c => c with { Sound = c.Sound with { Enabled = enabled } });
            }
            else if (string.Equals(key, "sound.defaultPack", StringComparison.OrdinalIgnoreCase))
            {
                ConfigReader.Update(c => c with { Sound = c.Sound with { DefaultPack = value } });
            }
            else if (key.StartsWith("sound.projects.", StringComparison.OrdinalIgnoreCase) && key.Length > 15)
            {
                var project = key[15..]; // After "sound.projects."
                ConfigReader.Update(c => c with
                {
                    Sound = c.Sound with
                    {
                        Projects = new Dictionary<string, string>(c.Sound.Projects)
                        {
                            [project] = value,
                        }
                    }
                });
            }
            else if (string.Equals(key, "overlay.enabled", StringComparison.OrdinalIgnoreCase))
            {
                if (!bool.TryParse(value, out var enabled))
                {
                    console.MarkupLine($"[red]Invalid value for '{Markup.Escape(key)}':[/] expected true/false, got '{Markup.Escape(value)}'");
                    return 1;
                }

                ConfigReader.Update(c => c with { Overlay = c.Overlay with { Enabled = enabled } });
            }
            else if (string.Equals(key, "overlay.position", StringComparison.OrdinalIgnoreCase))
            {
                ConfigReader.Update(c => c with { Overlay = c.Overlay with { Position = value } });
            }
            else if (string.Equals(key, "overlay.size", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, out var size))
                {
                    console.MarkupLine($"[red]Invalid value for '{Markup.Escape(key)}':[/] expected integer, got '{Markup.Escape(value)}'");
                    return 1;
                }

                ConfigReader.Update(c => c with { Overlay = c.Overlay with { Size = size } });
            }
            else if (string.Equals(key, "overlay.spacing", StringComparison.OrdinalIgnoreCase))
            {
                if (!int.TryParse(value, out var spacing))
                {
                    console.MarkupLine($"[red]Invalid value for '{Markup.Escape(key)}':[/] expected integer, got '{Markup.Escape(value)}'");
                    return 1;
                }

                ConfigReader.Update(c => c with { Overlay = c.Overlay with { Spacing = spacing } });
            }
            else
            {
                console.MarkupLine($"[red]Unknown key: '{Markup.Escape(key)}'.[/] Valid keys: tray.enabled, sound.enabled, sound.defaultPack, sound.projects.<path>, overlay.enabled, overlay.position, overlay.size, overlay.spacing");
                return 1;
            }

            console.MarkupLine($"Set [green]{Markup.Escape(key)}[/] = [bold]{Markup.Escape(value)}[/]");
            return 0;
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }
    }

    private static int ShowPath(ServiceProvider services, bool json)
    {
        var console = services.GetRequiredService<IAnsiConsole>();

        var paths = new Dictionary<string, string>
        {
            ["home"] = ImrdyPaths.Home,
            ["config"] = ImrdyPaths.Config,
            ["sessions"] = ImrdyPaths.Sessions,
            ["workspaces"] = ImrdyPaths.Workspaces,
            ["sounds"] = ImrdyPaths.SoundsDir,
            ["packs"] = ImrdyPaths.PacksDir,
            ["logs"] = ImrdyPaths.LogsDir,
            ["publishers"] = ImrdyPaths.Publishers,
            ["daemon-lock"] = ImrdyPaths.DaemonLock,
            ["daemon-pid"] = DaemonLock.PidFilePathFor(ImrdyPaths.DaemonLock),
        };

        if (json)
        {
            Console.WriteLine(JsonSerializer.Serialize(paths, ImrdyJsonContext.Indented));
            return 0;
        }

        var table = new Table();
        table.AddColumn("Name");
        table.AddColumn("Path");
        table.AddColumn("Exists");

        foreach (var (name, path) in paths.OrderBy(p => p.Key))
        {
            // Asking both is the only test that holds for every row. Deciding file-versus-
            // directory from a ".json" suffix worked while every file here was config JSON;
            // daemon.lock and daemon.pid are files with other extensions, and probing those
            // with Directory.Exists would report the lock missing while the daemon holds it.
            var exists = File.Exists(path) || Directory.Exists(path);

            table.AddRow(
                Markup.Escape(name),
                Markup.Escape(path),
                exists ? "[green]\u2713[/]" : "[dim]\u2717[/]");
        }

        console.MarkupLine("[bold]File Paths:[/]");
        console.Write(table);
        return 0;
    }

    private static int Validate(ServiceProvider services, bool json)
    {
        var configValidator = services.GetRequiredService<ConfigValidator>();
        var workspaceValidator = services.GetRequiredService<WorkspaceValidator>();
        var packLoader = services.GetRequiredService<PackLoader>();
        var console = services.GetRequiredService<IAnsiConsole>();

        try
        {
            var packs = packLoader.LoadPacks(ImrdyPaths.PacksDir);
            var packNames = packs.Select(p => p.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

            var configResult = configValidator.Validate(ImrdyPaths.Config, packNames);
            var workspaceResult = workspaceValidator.Validate(ImrdyPaths.Workspaces);

            if (json)
            {
                var output = new JsonObject
                {
                    ["config"] = new JsonObject
                    {
                        ["valid"] = configResult.IsValid,
                        ["errors"] = new JsonArray(configResult.Errors.Select(e => (JsonNode)new JsonObject
                        {
                            ["path"] = e.Path,
                            ["message"] = e.Message,
                            ["severity"] = e.Severity.ToString().ToLowerInvariant(),
                        }).ToArray()),
                    },
                    ["workspaces"] = new JsonObject
                    {
                        ["valid"] = workspaceResult.IsValid,
                        ["errors"] = new JsonArray(workspaceResult.Errors.Select(e => (JsonNode)new JsonObject
                        {
                            ["path"] = e.Path,
                            ["message"] = e.Message,
                            ["severity"] = e.Severity.ToString().ToLowerInvariant(),
                        }).ToArray()),
                    },
                };
                Console.WriteLine(output.ToJsonString(ImrdyJsonContext.Indented));
                return (configResult.IsValid && workspaceResult.IsValid) ? 0 : 1;
            }

            // Config validation tree
            var configTree = new Tree(configResult.IsValid
                ? "[green]\u2713[/] config.json"
                : "[red]\u2717[/] config.json");

            foreach (var error in configResult.Errors)
            {
                var color = error.Severity == ValidationSeverity.Error ? "red" : "yellow";
                configTree.AddNode($"[{color}]{Markup.Escape(error.Message)}[/]");
            }

            if (configResult.Errors.Count == 0)
            {
                configTree.AddNode("[green]All checks passed[/]");
            }

            console.Write(configTree);

            // Workspace validation tree
            var wsTree = new Tree(workspaceResult.IsValid
                ? "[green]\u2713[/] workspaces.json"
                : "[red]\u2717[/] workspaces.json");

            foreach (var error in workspaceResult.Errors)
            {
                var color = error.Severity == ValidationSeverity.Error ? "red" : "yellow";
                wsTree.AddNode($"[{color}]{Markup.Escape(error.Message)}[/]");
            }

            if (workspaceResult.Errors.Count == 0)
            {
                wsTree.AddNode("[green]All checks passed[/]");
            }

            console.Write(wsTree);

            return (configResult.IsValid && workspaceResult.IsValid) ? 0 : 1;
        }
        catch (Exception ex)
        {
            console.MarkupLine($"[red]Error:[/] {Markup.Escape(ex.Message)}");
            return 2;
        }
    }

    private static int UnknownSubcommand(ServiceProvider services, string sub)
    {
        var console = services.GetRequiredService<IAnsiConsole>();
        console.MarkupLine($"[red]Unknown subcommand:[/] {Markup.Escape(sub)}");
        console.MarkupLine("Run [dim]imrdy config --help[/] for usage.");
        return 1;
    }
}
