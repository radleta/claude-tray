namespace Imrdy.Core;

/// <summary>
/// Root configuration record for ~/.imrdy/config.json.
/// Null sections are handled by ConfigReader.EnsureDefaults().
/// </summary>
public record ImrdyConfig
{
    public TrayConfig Tray { get; init; } = new();
    public SoundConfig Sound { get; init; } = new();
    public OverlayConfig Overlay { get; init; } = new();
    public DiagnosticsConfig Diagnostics { get; init; } = new();
    public NetworkConfig Network { get; init; } = new();
}

public record TrayConfig
{
    public bool Enabled { get; init; } = true;
    public string IconStyle { get; init; } = "dots";
}

public record SoundConfig
{
    public bool Enabled { get; init; } = true;
    public string DefaultPack { get; init; } = "random";
    public List<string> DisabledPacks { get; init; } = [];
    public Dictionary<string, string> Projects { get; init; } = new();
}

public record OverlayConfig
{
    public bool Enabled { get; init; } = false;
    public string Position { get; init; } = "bottom-right";
    public int Size { get; init; } = 64;
    public int Spacing { get; init; } = 8;
    /// <summary>
    /// Zero-based index of the monitor the overlay docks to. 0 = primary monitor (default).
    /// </summary>
    public int Monitor { get; init; } = 0;

    /// <summary>
    /// When true the overlay position is locked and cannot be changed by dragging.
    /// </summary>
    public bool Locked { get; init; } = false;

    /// <summary>
    /// Persisted free-float position: monitor-relative offset in logical px from the
    /// target monitor's working-area origin. Null means "no free-float position set" —
    /// resolution falls back to the legacy <see cref="Position"/> anchor.
    /// </summary>
    public int? OffsetX { get; init; } = null;

    /// <summary>
    /// Persisted free-float position: monitor-relative offset in logical px from the
    /// target monitor's working-area origin. Null means "no free-float position set" —
    /// resolution falls back to the legacy <see cref="Position"/> anchor.
    /// </summary>
    public int? OffsetY { get; init; } = null;
}

public record DiagnosticsConfig
{
    /// <summary>
    /// Nullable so ConfigReader.EnsureDefaults can distinguish "missing from JSON" (null) from "explicitly false".
    /// Resolution rule at runtime: <c>IpcEnabled ?? File.Exists(ImrdyPaths.DevBuildMarker)</c> — null defaults to on-in-dev, off-in-prod.
    /// Callers MUST NOT collapse null to a concrete bool in EnsureDefaults; the three-state semantics are intentional.
    /// </summary>
    public bool? IpcEnabled { get; init; } = null;
}

/// <summary>
/// Cross-machine publish scalars. Per-publisher records live in publishers.json, not here.
/// Only the two string fields are nullable, and each states what null means; the rest take
/// concrete defaults the way <see cref="TrayConfig"/> and <see cref="OverlayConfig"/> do.
/// <see cref="DiagnosticsConfig.IpcEnabled"/> is this codebase's one deliberate three-state
/// field and this section does not add a second.
/// </summary>
public record NetworkConfig
{
    /// <summary>Name this machine publishes under. Null resolves to the hostname at runtime.</summary>
    public string? MachineName { get; init; } = null;

    /// <summary>Shared key sent in the connect frame. Null means no key is configured.</summary>
    public string? AuthKey { get; init; } = null;

    /// <summary>TCP port the receiver listens on when <see cref="ListenEnabled"/> is true.</summary>
    public int ListenPort { get; init; } = DefaultListenPort;

    /// <summary>Whether this machine accepts inbound publisher connections.</summary>
    public bool ListenEnabled { get; init; } = false;

    public const int DefaultListenPort = 47600;
    public const int MinListenPort = 1;
    public const int MaxListenPort = 65535;
}
