using System.Text.Json;

namespace Imrdy.Core.Validation;

/// <summary>
/// Validates ~/.imrdy/config.json: valid JSON, known keys, pack references resolve.
/// </summary>
public sealed class ConfigValidator
{
    private static readonly HashSet<string> KnownRootKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "tray",
        "sound",
        "overlay",
        "diagnostics",
        "network",
    };

    private static readonly HashSet<string> KnownTrayKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "enabled",
        "iconStyle",
    };

    private static readonly HashSet<string> KnownSoundKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "enabled",
        "defaultPack",
        "disabledPacks",
        "projects",
    };

    private static readonly HashSet<string> KnownOverlayKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "enabled",
        "position",
        "size",
        "spacing",
        "monitor",
        "locked",
        "offsetX",
        "offsetY",
    };

    private static readonly HashSet<string> KnownDiagnosticsKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "ipcEnabled",
    };

    private static readonly HashSet<string> KnownNetworkKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "machineName",
        "authKey",
        "listenPort",
        "listenEnabled",
    };

    /// <summary>
    /// Validates a config file.
    /// </summary>
    /// <param name="configPath">Path to config.json.</param>
    /// <param name="availablePackNames">Names of installed packs (for reference checking).</param>
    public ValidationResult Validate(string configPath, IReadOnlyCollection<string> availablePackNames)
    {
        var errors = new List<ValidationError>();

        if (!File.Exists(configPath))
        {
            errors.Add(new ValidationError(configPath, "Config file not found.", ValidationSeverity.Warning));
            return new ValidationResult { Errors = errors };
        }

        JsonDocument doc;
        try
        {
            var bytes = File.ReadAllBytes(configPath);
            doc = JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            errors.Add(new ValidationError(configPath, $"Invalid JSON: {ex.Message}", ValidationSeverity.Error));
            return new ValidationResult { Errors = errors };
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                errors.Add(new ValidationError(configPath, "Root element must be a JSON object.", ValidationSeverity.Error));
                return new ValidationResult { Errors = errors };
            }

            var packNameSet = new HashSet<string>(availablePackNames, StringComparer.OrdinalIgnoreCase);

            // Check for unknown top-level keys
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (!KnownRootKeys.Contains(property.Name))
                {
                    errors.Add(new ValidationError(
                        $"{configPath} → {property.Name}",
                        $"Unknown key: '{property.Name}' (possible typo).",
                        ValidationSeverity.Warning));
                }
            }

            TryValidateSection(doc.RootElement, "tray", KnownTrayKeys, configPath, errors, out _);
            TryValidateSection(doc.RootElement, "overlay", KnownOverlayKeys, configPath, errors, out _);
            TryValidateSection(doc.RootElement, "diagnostics", KnownDiagnosticsKeys, configPath, errors, out _);
            TryValidateSection(doc.RootElement, "network", KnownNetworkKeys, configPath, errors, out _);

            // "sound" carries pack-reference checks on top of the shared key check
            if (TryValidateSection(doc.RootElement, "sound", KnownSoundKeys, configPath, errors, out var soundProp))
            {
                // Validate defaultPack reference
                if (soundProp.TryGetProperty("defaultPack", out var defaultProp)
                    && defaultProp.ValueKind == JsonValueKind.String)
                {
                    var defaultPack = defaultProp.GetString();
                    if (!string.IsNullOrEmpty(defaultPack)
                        && !string.Equals(defaultPack, "random", StringComparison.OrdinalIgnoreCase)
                        && !packNameSet.Contains(defaultPack))
                    {
                        errors.Add(new ValidationError(
                            $"{configPath} → sound.defaultPack",
                            $"Default pack '{defaultPack}' is not installed.",
                            ValidationSeverity.Error));
                    }
                }

                // Validate projects pack references
                if (soundProp.TryGetProperty("projects", out var projectsProp)
                    && projectsProp.ValueKind == JsonValueKind.Object)
                {
                    foreach (var mapping in projectsProp.EnumerateObject())
                    {
                        if (mapping.Value.ValueKind == JsonValueKind.String)
                        {
                            var packName = mapping.Value.GetString();
                            if (!string.IsNullOrEmpty(packName) && !packNameSet.Contains(packName))
                            {
                                errors.Add(new ValidationError(
                                    $"{configPath} → sound.projects.{mapping.Name}",
                                    $"Pack '{packName}' referenced by project '{mapping.Name}' is not installed.",
                                    ValidationSeverity.Error));
                            }
                        }
                    }
                }
            }
        }

        return new ValidationResult { Errors = errors };
    }

    /// <summary>
    /// Reports a non-object section as an error and any key outside <paramref name="knownKeys"/>
    /// as a typo warning. Returns true only when the section is present and is an object, so a
    /// caller with further checks can run them against <paramref name="section"/>.
    /// </summary>
    private static bool TryValidateSection(
        JsonElement root,
        string name,
        HashSet<string> knownKeys,
        string configPath,
        List<ValidationError> errors,
        out JsonElement section)
    {
        if (!root.TryGetProperty(name, out section))
        {
            return false;
        }

        if (section.ValueKind != JsonValueKind.Object)
        {
            errors.Add(new ValidationError(
                $"{configPath} → {name}",
                $"'{name}' must be a JSON object.",
                ValidationSeverity.Error));
            return false;
        }

        foreach (var prop in section.EnumerateObject())
        {
            if (!knownKeys.Contains(prop.Name))
            {
                errors.Add(new ValidationError(
                    $"{configPath} → {name}.{prop.Name}",
                    $"Unknown key: '{name}.{prop.Name}' (possible typo).",
                    ValidationSeverity.Warning));
            }
        }

        return true;
    }
}
