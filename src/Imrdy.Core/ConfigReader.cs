using System.Text.Json;

namespace Imrdy.Core;

/// <summary>
/// Reads and updates ~/.imrdy/config.json with atomic writes and safe defaults.
/// </summary>
public static class ConfigReader
{
    /// <summary>
    /// Reads config from ~/.imrdy/config.json.
    /// Returns defaults if file is missing or malformed.
    /// </summary>
    public static ImrdyConfig Read()
    {
        var path = ImrdyPaths.Config;
        if (!File.Exists(path))
            return new ImrdyConfig();

        try
        {
            var bytes = File.ReadAllBytes(path);
            var config = JsonSerializer.Deserialize(bytes, ImrdyJsonContext.Default.ImrdyConfig);
            return EnsureDefaults(config);
        }
        catch (JsonException)
        {
            return new ImrdyConfig();
        }
        catch (IOException)
        {
            return new ImrdyConfig();
        }
    }

    /// <summary>
    /// Read-modify-write: deserialize, apply mutation, serialize, atomic write.
    /// Creates file with defaults if missing (cold start).
    /// Last-writer-wins on concurrent calls.
    /// </summary>
    public static void Update(Func<ImrdyConfig, ImrdyConfig> mutate)
    {
        var current = Read();
        var updated = mutate(current);
        var json = JsonSerializer.SerializeToUtf8Bytes(updated, ImrdyJsonContext.Default.ImrdyConfig);
        AtomicFileWriter.Write(ImrdyPaths.Config, json);
    }

    /// <summary>
    /// Defense-in-depth: ensure all nested objects are non-null even if
    /// JsonObjectCreationHandling.Populate fails for some edge case.
    /// </summary>
    private static ImrdyConfig EnsureDefaults(ImrdyConfig? config)
    {
        if (config is null) return new ImrdyConfig();
        var tray = config.Tray ?? new TrayConfig();
        return config with
        {
            Tray = tray with
            {
                IconStyle = string.IsNullOrWhiteSpace(tray.IconStyle) ? "dots" : tray.IconStyle
            },
            Sound = (config.Sound ?? new SoundConfig()) with
            {
                DisabledPacks = config.Sound?.DisabledPacks ?? [],
                Projects = config.Sound?.Projects ?? new Dictionary<string, string>()
            },
            Overlay = (config.Overlay ?? new OverlayConfig()) with
            {
                Position = string.IsNullOrWhiteSpace(config.Overlay?.Position) ? "bottom-right" : config.Overlay.Position,
                Size = Math.Clamp(config.Overlay?.Size ?? 64, 32, 256),
                Spacing = Math.Clamp(config.Overlay?.Spacing ?? 8, 0, 32),
                Monitor = config.Overlay?.Monitor ?? 0,
            },
            // IpcEnabled is intentionally left nullable — null means "use dev-build marker at runtime".
            // Do not collapse it to a concrete bool here.
            Diagnostics = config.Diagnostics ?? new DiagnosticsConfig(),
            Network = (config.Network ?? new NetworkConfig()) with
            {
                ListenPort = Math.Clamp(
                    config.Network?.ListenPort ?? NetworkConfig.DefaultListenPort,
                    NetworkConfig.MinListenPort,
                    NetworkConfig.MaxListenPort),
            },
        };
    }
}
