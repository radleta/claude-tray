---
tags: [imrdy-expert/overlay]
summary: "OverlayPanel OnPaint rendering; bitmap cache keyed by (style,status); aging via chip-background opacity ladder in OnPaint; Form.Bounds reliability on non-layered forms; monitor/position placement reads mutable _monitor/_position fields, not config directly"
code-cites:
  - src/Imrdy.Windows/Overlay/OverlayPanel.cs
  - src/Imrdy.Windows/Desktop/PInvokeOverlay.cs
  - src/Imrdy.Core/Status/StatusMap.cs
  - src/Imrdy.Core/Overlay/OverlayAnchor.cs
---

# Overlay Rendering Internals

## Overview

`OverlayPanel` (`src/Imrdy.Windows/Overlay/OverlayPanel.cs`) is a non-layered WinForms `Form` that renders via `OnPaint`. It replaces the former `OverlayWindowBase` / `PassiveOverlayWindow` / `InteractiveOverlayWindow` three-class hierarchy, which was layered (`WS_EX_LAYERED`) and rendered via `UpdateLayeredWindow`. All layered-window GDI plumbing has been removed.

DWM mica backdrop is applied in `OnHandleCreated` via `DwmSetWindowAttribute` (overlay only — dashboard forms do not use mica; see [Hover Dashboard Form Lifecycle](hover-dashboard-form-lifecycle.md)). `DrawToBitmap` captures only GDI+ content — rendered PNGs show the standard WinForms background color, not mica.

DWM native corner rounding is applied via `ImrdyPalette.ApplyRoundedCorners(this)` (sets `DWMWA_WINDOW_CORNER_PREFERENCE = DWMWCP_ROUND`). This returns true on Win11+ (DWM owns the rounding) and false on Win10 ≤19045 (falls back to GDI `Region` clip via `ApplyRoundedRegion`). `OverlayPanel._usesDwmCorners` tracks which path was taken. Root cause for the switch: a GDI `Region` clips only GDI painting, not the DWM mica backdrop, so DWM composited opaque white into Region-carved corners on Win11.

## Bitmap Cache

`OverlayPanel._cache` is a `Dictionary<(string style, string status, bool disconnected), Bitmap>` — one entry per unique (style, status, disconnected) combination. Aging tier is NOT baked into the cached glyph; the disconnected flag IS, because `DisconnectedGlyph` is a geometry change (shrink to 60% + dashed ring) rather than an opacity one. Cache is populated lazily; cleared by `InvalidateStyleCache()` (called when the user changes icon styles). Dispose loop runs over `.Values` in both `InvalidateStyleCache` and `Dispose`.

**Cache miss path**: built-in shape via `GetShapeDelegate` OR pack icon via `RenderFromPack`. Fallback on exception: circle via `RenderCircleFallback`.

## Aging Tier and OnPaint Opacity Ladder

AgingTier 0-4 is computed by `StatusMap.GetAgingTier` in `Imrdy.Core/Status/StatusMap.cs`:

| Tier | Time since last seen |
|------|---------------------|
| 0    | < 1 min             |
| 1    | < 3 min             |
| 2    | < 7 min             |
| 3    | < 15 min            |
| 4    | 15 min+             |

**Aging is NOT baked into the cached glyph.** `OverlayPanel._cache` stores glyphs at full brightness, keyed by `(style, status)` only. Aging is applied at paint time as a chip-background opacity ladder in `OnPaint` → `PaintChip` → `ChipBgAlpha(tier, isAlert)`: tier 0 = alpha 255 (most opaque), tier 1 = 200, tier 2 = 160, tier 3 = 120, tier 4 = 80 (faintest). Alert statuses (`permission`/`error`, matched by `IsAlertStatus`) are floored at alpha 160 regardless of tier (Decision 2c — never the faint alpha of the old layered path). Tier 4 also applies a slight additional glyph dim (`ColorMatrix.Matrix33 = 0.85f`, applied only when `tier > 3`). No `ColorMatrix` is used for tiers 0-3.

Note: the tray-icon renderers (`ParametricShapeRenderer`, `PackIconRenderer` in `src/Imrdy.Windows/Icons/`) still bake tier-based aging into their per-icon bitmaps (RGB multiplier for built-in shapes; `ApplyAgingColorMatrix` for SVG pack icons). That path is unchanged and separate from the overlay.

## OnPaint Rendering Flow

`OverlayPanel.OnPaint` (`OverlayPanel.cs:213-237`):
1. `g.Clear(ImrdyPalette.BgForm)`.
2. Empty-state short-circuit: `items.Count == 0` → `PaintPlaceholderChip(g)` and return (Decision 6 — panel never zero-width/invisible).
3. Otherwise, left-to-right loop: `chipX = PanelPadding + i * (size + spacing)`; each chip painted via `PaintChip(g, chipX, PanelPadding, size, item)`.

`PaintChip` (`OverlayPanel.cs:239-283`) paints in this fixed order: (1) rounded chip background at tier-driven alpha, (2) status glyph from the `(style,status)` cache inset by `ChipPadding`, (3) alert cue outline for error/permission (`PaintAlertCue`), (4) hover highlight when `item.Id == _hoveredChipId` (`PaintHoverHighlight`). This is the **same slot math** `HitIconIndex`/`DisplayItemCollection.TryGetItemAtClientPoint` uses — hit-test and paint geometry cannot diverge.

The grip band occupies that left edge, so both sides carry the identical `PanelPadding + GripWidth` inset: `OnPaint`'s `chipX = PanelPadding + gripWidth + i * (size + spacing)` (`OverlayPanel.cs:285`) and `HitIconIndex`'s `clientX - PanelPadding - GripWidth` (`OverlayPanel.cs:904`). `GripWidth` is a single DPI-scaling property over the `GripWidthLogical = 14` seed (`OverlayPanel.cs:46-49`) — paint, hit-test, `IsGripHit`, and `MinimumPanelWidth` all read that one value. Any further left-edge element must extend both offsets together, or hit-test and paint will disagree.

No GDI DC juggling, no `UpdateLayeredWindow`, no premultiplied alpha requirement. The non-layered form renders via the normal WinForms paint pipeline.

## Form.Bounds Reliability

On non-layered forms, `Form.Bounds` reflects the actual screen position reliably. WinForms intercepts `WM_WINDOWPOSCHANGED` and updates its internal cache. There is no need for `GetActualWindowRect` or a custom `ActualScreenBounds` property.

**Contrast with the former layered approach**: `OverlayWindowBase.ActualScreenBounds` existed solely because `UpdateLayeredWindow` positions the HWND via Win32 without reliably firing `WM_WINDOWPOSCHANGED` in a way WinForms intercepts — leaving `Form.Bounds` stale at `(0,0,300,300)` for the process lifetime. That staleness bug and its workaround (`GetActualWindowRect` P/Invoke) are gone with the switch to `OnPaint`.

**Implication**: all callers that previously had to use `ActualScreenBounds` (hover dashboard controller bounds checks, z-order gate in `WindowAtPoint`, grace corridor geometry) now use `_overlayPanel.Bounds` directly.

## PInvokeOverlay Surface (Post-Redesign)

Components retained in `src/Imrdy.Windows/Desktop/PInvokeOverlay.cs`:

| Component | Role |
|-----------|------|
| `WS_EX_TOOLWINDOW` | Applied to OverlayPanel's extended window style |
| `ScreenToClientPoint(hwnd, lParam)` | DPI-correct screen→client conversion for hover-highlight poll and hit-testing; 64-bit-safe lParam decode + LOWORD/HIWORD sign-extension built in |
| `WindowAtPoint(point)` | Z-order hit test for hover-dashboard z-order gating |
| `RegisterWindowMessage` | Used for `TaskbarCreated` message ID; OverlayPanel re-pins after Explorer restart |

Components stripped (no longer needed without `WS_EX_LAYERED`):

| Former component | Why removed |
|-----------------|-------------|
| `UpdateLayeredWindow` + GDI P/Invokes | OnPaint replaces the layered bitmap path |
| `SetBitmap` method | Wrapper around `UpdateLayeredWindow`; entire method was layered-only |
| `GetActualWindowRect` + `RECT` struct | Workaround for Bounds-cache staleness; non-layered forms don't have this problem |
| `DecodeLParamPoint` | Merged into `ScreenToClientPoint`; no longer a separate helper |

## Monitor and Position Placement (mutable fields, not config directly)

**Drift correction (2026-07-02):** placement does NOT read `_config.Monitor` / `_config.Position` directly — it reads two private mutable fields, `_monitor` (int) and `_position` (string), that are ctor-initialized from `config.Monitor`/`config.Position` and later overwritten in-place by `ApplyPositionConfig(position, monitor, locked)` without recreating the panel. This is what makes flash-free drag-drop and non-structural config live-reload possible (see [Config Live Reload](config-live-reload.md)).

- `CalculatePosition()` (`OverlayPanel.cs:740-763`) calls `OverlayAnchor.Parse(_position)` then `ResolveTargetScreen()`; computes `x`/`y` from `screen.WorkingArea` for the 6 anchors (Left/Center/Right × Top/Bottom), with a 16px margin and an 8px auto-hide-taskbar reserve on the bottom edge.
- `ResolveTargetScreen()` (`OverlayPanel.cs:772-778`) reads `_monitor` (not `config.Monitor`) against `Screen.AllScreens`, clamping to `Screen.PrimaryScreen ?? screens[0]` when out of range.
- `ApplyPositionConfig(string position, int monitor, bool locked)` (`OverlayPanel.cs:509-516`) is the single mutation point for `_position`/`_monitor`/`_locked`; it recomputes `this.Location = CalculatePosition()` in place. Valid callers per its doc comment: `OnMouseUp` (drag drop) and `TrayApp.OnConfigChanged` (drain tick) only — asserted via `Debug.Assert(!InvokeRequired, ...)` (stripped in Release).

Free-float placement follows that same pattern: `_offsetX` / `_offsetY` (`int?`, `OverlayPanel.cs:79-80`) sit alongside `_position`/`_monitor`/`_locked` and are mutated only inside `ApplyPositionConfig(position, monitor, locked, offsetX, offsetY)` (`OverlayPanel.cs:595`). `CalculatePosition` delegates the resolution chain (offset → `position` anchor → default) plus the snap/clamp math to pure `Imrdy.Core.Overlay.OverlayPlacement`. Any further placement input must extend the same field-plus-`ApplyPositionConfig` path and never read `_config.*` directly from `CalculatePosition`/`ResolveTargetScreen`.

## Related

- [Overlay Interactivity](overlay-interactivity.md) — OverlayPanel single-class design, drag FSM (OnMouseDown/OnMouseMove/OnMouseUp), ISessionInteractionRouter
- [Status Mapping](status-mapping.md) — StatusMap.GetAgingTier, StatusMap.ResolveColor
- [Render Verb Architecture](render-verb-architecture.md) — overlay component in `imrdy render --all`
- [Config Live Reload](config-live-reload.md) — structural-delta classification; Position/Monitor/Locked apply in-place via ApplyPositionConfig
