namespace Imrdy.Core.Display;

/// <summary>Caller-supplied input to <see cref="DisplayItemCollection.Build"/>. Mirrors <see cref="DisplayItem"/> fields; visibility is pre-computed by the caller.</summary>
public sealed record DisplayItemInput(
    string Id,
    DisplayItemType ItemType,
    string Status,
    int? DesktopIndex,
    string IconStyle,
    int AgingTier,
    bool IsVisible,
    string Label,
    bool IsDisconnected = false);
