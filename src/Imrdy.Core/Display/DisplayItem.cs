namespace Imrdy.Core.Display;

/// <summary>Pure-data view of one tray/overlay item, produced by <see cref="DisplayItemCollection.Build"/>.</summary>
/// <param name="IsDisconnected">
/// D20: this item's publisher has no live link. Rendered as a treatment distinct from the
/// aging dim — <paramref name="AgingTier"/> already owns opacity, so this is a separate
/// dimension and never a sixth tier. Always false for a local session and for a workspace.
/// </param>
public sealed record DisplayItem(
    string Id,
    DisplayItemType ItemType,
    string Status,
    int? DesktopIndex,
    string IconStyle,
    int AgingTier,
    bool IsVisible,
    string Label,
    bool IsDisconnected = false);
