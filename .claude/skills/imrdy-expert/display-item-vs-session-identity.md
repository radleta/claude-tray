---
tags: [imrdy-expert/display-model]
summary: "DisplayItem uses Id + ItemType; SessionEntry has SessionId. Choose based on context. Full DisplayItem field reference included."
code-cites:
  - src/Imrdy.Core/Display/DisplayItem.cs
  - src/Imrdy.Core/Display/DisplayItemInput.cs
  - src/Imrdy.Core/Display/DisplayItemCollection.cs
  - src/Imrdy.Core/Display/DisplayItemType.cs
---

## DisplayItem vs SessionEntry — Which Identity to Use

`DisplayItem` (in `Imrdy.Core.Display/`) and `SessionEntry` (in `Imrdy.Windows/Models/`) have different identity schemes.

| Field | DisplayItem | SessionEntry |
|-------|-------------|--------------|
| Identity | `Id` (string) | `SessionId` (string) |
| Type discriminator | `ItemType` (Session\|Workspace) | N/A (only sessions) |
| Session name | Not included | `State.SessionName` |
| Status | `Status` (string) | `State.Status` (string) |

**Decision tree for fleet/summary projections:**

| Goal | Source | Why |
|------|--------|-----|
| Build session list with names and hover state | `_sessionSource()` -> `IReadOnlyList<SessionEntry>` | SessionEntry has SessionId + SessionName directly; no mapping needed |
| Filter workspaces from display items | Use `DisplayItem` with `ItemType == DisplayItemType.Workspace` | DisplayItem is the only source that distinguishes sessions from workspaces |
| Build display-layer projection (tray/overlay) | `_displayItemSource()` -> `IReadOnlyList<DisplayItem>` | Represents tray/overlay filtered state (respects Dismissed, RemoveAfter, etc.) |

**Simpler fleet pattern — use SessionEntry:**

```csharp
private static IReadOnlyList<FleetItem> ProjectFleetItems(
    IReadOnlyList<SessionEntry> sessions, string hoveredSessionId)
{
    var fleet = new List<FleetItem>(sessions.Count);
    foreach (var s in sessions)
    {
        fleet.Add(new FleetItem(
            SessionId: s.SessionId,
            SessionName: s.State.SessionName ?? "",
            Status: s.State.Status,
            IsHovered: s.SessionId == hoveredSessionId));
    }
    return fleet;
}
```

Avoids mapping `DisplayItem.Id` -> `SessionId` and querying session names from elsewhere.

**When to use DisplayItem:** Tray render path, overlay render path, any code that respects workspace filtering or session visibility state (Dismissed, RemoveAfter aging).

**When to use SessionEntry:** Dashboard state building, controller logic that needs full session context, any aggregation that ignores visibility state.

---

**Discovered:** Step 8 backend wiring — attempted fleet projection from DisplayItem before consulting SessionEntry definition. Both record types are valid; context determines which is simpler to work with.

**Impact:** Dashboard and controller code touching session identity should prefer SessionEntry when full state is available, and DisplayItem only when filtering/visibility is required.

---

## DisplayItem Full Field Reference

`DisplayItem` is a `sealed record` at `src/Imrdy.Core/Display/DisplayItem.cs`:

```csharp
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
```

| Field | Type | Meaning |
|-------|------|---------|
| `Id` | `string` | Session ID for sessions; workspace path for workspaces |
| `ItemType` | `DisplayItemType` | `Session` or `Workspace` (enum, 2 values) |
| `Status` | `string` | Status string ("busy", "idle", "done", "error", "permission", "unknown", etc.) |
| `DesktopIndex` | `int?` | Virtual desktop index; null when unknown. Sort key in `Build()` |
| `IconStyle` | `string` | Resolved icon style for this item ("circles", "squares", "pack:mypack", etc.) — per-session/workspace override already applied |
| `AgingTier` | `int` | 0-4 (0=fresh <1m, 4=oldest 15m+). Drives ColorMatrix desaturation in SVG pack path; RGB multiplier in built-in shape path |
| `IsVisible` | `bool` | Always true for items returned by `Build()` — items failing visibility are filtered out before returning |
| `Label` | `string` | Short display label (session name or workspace name) |
| `IsDisconnected` | `bool` | D20: this item's publisher has no live link. A separate dimension from `AgingTier`, never a sixth tier — `AgingTier` owns opacity, this owns geometry (`DisconnectedGlyph`). Always false for a local session and for a workspace. Defaults to false, so existing positional constructions still compile |

`DisplayItemInput` (`DisplayItemInput.cs`) mirrors these fields exactly — it is the caller-supplied input to `Build()`; `DisplayItem` is the output.

## DisplayItemCollection.Build() Behavior

```csharp
// src/Imrdy.Core/Display/DisplayItemCollection.cs
public static BuiltDisplayItems Build(IReadOnlyList<DisplayItemInput> items, bool trayEnabled)
```

- Filters `items` to those with `IsVisible == true`.
- Maps each to a `DisplayItem` (no field transformation — direct copy).
- Sorts: null `DesktopIndex` last, then ascending `DesktopIndex`, then `Session` before `Workspace` within the same desktop.
- Returns `BuiltDisplayItems(ForTray, ForOverlay)` where `ForTray` is empty when `trayEnabled == false`.

**Sort order invariant**: sessions and workspaces on the same desktop are always adjacent, sessions first. The overlay renders in `ForOverlay` list order — left to right.

## Hit-Test Geometry

`DisplayItemCollection.TryGetItemAtClientPoint(items, clientX, iconSize, spacing, out hit, out index)` is the pure hit-test function used by both overlay and unit tests. Formula:

```
slot = iconSize + spacing
i = clientX / slot
inSlot = clientX % slot
hit if inSlot < iconSize  (i.e., in the icon portion, not the gap)
```

Returns false for gaps, negative coords, or out-of-range index.
