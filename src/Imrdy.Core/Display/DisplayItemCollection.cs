namespace Imrdy.Core.Display;

/// <summary>Builds sorted display-item lists for the tray and overlay from pre-computed inputs.</summary>
public static class DisplayItemCollection
{
    /// <summary>
    /// Pure hit-test: maps a client X coordinate to the <see cref="DisplayItem"/> occupying that pixel.
    /// </summary>
    /// <param name="items">Ordered display items (e.g. from <see cref="Build"/>).</param>
    /// <param name="clientX">Client X coordinate to test.</param>
    /// <param name="iconSize">Width (and height) of each icon in pixels.</param>
    /// <param name="spacing">Gap between icons in pixels.</param>
    /// <param name="hit">The matched item; <c>null</c> on miss.</param>
    /// <param name="index">Slot index of the matched item; <c>-1</c> on miss.</param>
    /// <returns><c>true</c> when <paramref name="clientX"/> lands inside an icon; <c>false</c> for gaps, negative coords, or out-of-range.</returns>
    public static bool TryGetItemAtClientPoint(
        IReadOnlyList<DisplayItem> items,
        int clientX,
        int iconSize,
        int spacing,
        out DisplayItem? hit,
        out int index)
    {
        hit = null;
        index = -1;

        if (clientX < 0) return false;
        var slot = iconSize + spacing;
        if (slot <= 0) return false;
        var i = clientX / slot;
        var inSlot = clientX % slot;
        if (inSlot >= iconSize) return false;
        if (i >= items.Count) return false;

        hit = items[i];
        index = i;
        return true;
    }

    /// <summary>
    /// Filters, maps, and sorts <paramref name="items"/> into tray and overlay lists.
    /// </summary>
    /// <param name="items">Caller-supplied inputs with pre-computed <see cref="DisplayItemInput.IsVisible"/> flags.</param>
    /// <param name="trayEnabled">When <c>false</c>, <see cref="BuiltDisplayItems.ForTray"/> is empty.</param>
    public static BuiltDisplayItems Build(
        IReadOnlyList<DisplayItemInput> items,
        bool trayEnabled)
    {
        // Filter to visible items, map to output record, then sort.
        // Sort: Session-before-Workspace via explicit map; do not depend on enum ordinal.
        var sorted = items
            .Where(x => x.IsVisible)
            .Select(x => new DisplayItem(
                x.Id,
                x.ItemType,
                x.Status,
                x.DesktopIndex,
                x.IconStyle,
                x.AgingTier,
                x.IsVisible,
                x.Label,
                x.IsDisconnected))
            .OrderBy(x => x.DesktopIndex.HasValue ? 0 : 1) // null DesktopIndex last
            .ThenBy(x => x.DesktopIndex ?? 0)
            .ThenBy(x => x.ItemType == DisplayItemType.Session ? 0 : 1) // Session-before-Workspace via explicit map; do not depend on enum ordinal.
            .ToList();

        IReadOnlyList<DisplayItem> forTray = trayEnabled
            ? sorted
            : Array.Empty<DisplayItem>();

        return new BuiltDisplayItems(forTray, sorted);
    }
}
