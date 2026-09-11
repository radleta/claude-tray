using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;
using Imrdy.Core;
using Imrdy.Core.Desktop;
using Imrdy.Core.Display;
using Imrdy.Core.Graphics;
using Imrdy.Core.Overlay;
using Imrdy.Core.Status;
using Imrdy.Windows.Desktop;
using Imrdy.Windows.Icons;
using Imrdy.Windows.Interaction;
using Imrdy.Windows.Theme;
using Microsoft.Extensions.Logging;
using Svg;

namespace Imrdy.Windows.Overlay;

/// <summary>
/// Single non-layered, DWM-composited, always-interactive overlay panel.
/// Replaces OverlayWindowBase, InteractiveOverlayWindow, and PassiveOverlayWindow
/// (all three deleted by the redesign — this is the one surviving surface).
/// Edge-docked, top-most WinForms Form that renders status chips via OnPaint.
/// Pinned to all virtual desktops; re-pins on TaskbarCreated (Explorer restart)
/// via the registered window message (Decision 9).
/// The panel has no layered or click-through extended styles, but WndProc's WM_MOUSEACTIVATE
/// handler unconditionally declines activation (MA_NOACTIVATE) for every interaction,
/// right-click included — the overlay never becomes the OS foreground window on its own. The
/// context menu's hover hot-track pipeline (Decision 3, 11) still works because
/// TrayApp.ShowContextMenuAt grants the overlay foreground explicitly (SetForegroundWindow +
/// AttachThreadInput) immediately before showing the menu, then restores the prior foreground
/// window once it closes — see CreateParams and MenuAnchor.AtControl for the full mechanism.
/// </summary>
internal sealed class OverlayPanel : Form
{
    // ── Layout constants (Decision 10) ────────────────────────────────────────────
    // Hardcoded, not config fields — mirrors the dashboards' fixed 14 px radius precedent.
    internal const int ChipPadding      = 6;
    internal const int ChipCornerRadius = 6;
    internal const int PanelPadding     = 4;

    // ── Grip layout constants (Decision D2/D5) ─────────────────────────────────────
    // Left grip handle — the sole drag-arming zone (see HitIconIndex/IsGripHit).
    // GripWidthLogical is the LOGICAL-px seed; the GripWidth property below DPI-scales
    // it via the same DeviceDpi/96f convention OnMouseMove applies to the drag delta.
    // Paint (OnPaint) and hit-test (HitIconIndex) both consume this one GripWidth
    // value — the sync gate that keeps chip slot math from desyncing (Risk 2).
    internal const int GripWidthLogical = 14;

    // Depends on DeviceDpi (runtime, only meaningful once the handle is created) — cannot be const.
    private int GripWidth => (int)(GripWidthLogical * this.DeviceDpi / 96f);

    // Depends on _config.Size + GripWidth (runtime values) — cannot be const.
    private int MinimumPanelWidth => 2 * PanelPadding + GripWidth + _config.Size;

    // ── TaskbarCreated registration ───────────────────────────────────────────────
    // RegisterWindowMessage returns 0 on failure (atom-table exhausted — rare).
    // The WndProc guard checks for 0 first to prevent matching WM_NULL (0x0000).
    private static readonly uint _wmTaskbarCreated =
        PInvokeOverlay.RegisterWindowMessage("TaskbarCreated");

    // ── WndProc message constants ─────────────────────────────────────────────────
    // Used by the focus-preservation + drag-cancel guards in WndProc.
    private const int WM_MOUSEACTIVATE = 0x0021;
    private const int MA_NOACTIVATE    = 3;
    private const int WM_CANCELMODE    = 0x001F;

    // ── Fields ────────────────────────────────────────────────────────────────────
    private readonly OverlayConfig _config;
    private readonly ISessionInteractionRouter _router;
    private readonly IDesktopManager? _desktopManager;
    private readonly ILogger _logger;
    private readonly GraphicsPackLoader _graphicsPackLoader;

    // ── Mutable position state ─────────────────────────────────────────────────────
    // Initialized from config in ctor; mutated by ApplyPositionConfig (UI-thread only).
    // _config is retained for Size/Spacing reads (structural; applied via recreate).
    private string _position;
    private int    _monitor;
    private bool   _locked;
    private int?   _offsetX;
    private int?   _offsetY;

    // ── Drag FSM state ─────────────────────────────────────────────────────────────
    // States: Idle (no button) → Armed (_dragArmed, !_isDragging) → Dragging (_isDragging).
    // Capture is held in Armed + Dragging; released by ResetDragState on every exit path.
    private bool  _isDragging;
    private bool  _dragArmed;         // Left button pressed; threshold not yet crossed.
    private Point _dragStartScreen;   // Cursor.Position at mouse-down (physical screen px).
    private Point _formStartLocation; // this.Location at mouse-down (logical px, Per-Monitor V2).
    private int   _downHitIndex;      // Chip index at mouse-down; -1 = gutter click.

    // Both declared non-nullable and MUST be ctor-initialized to satisfy CS8618
    // under Nullable=enable + TreatWarningsAsErrors.
    // _items: OnPaint fires (on first Show) before any UpdateItems/LoadFixtureItems call.
    // _cache: non-nullable field; disposal contract established here; populated via GetOrCreateBitmap.
    private IReadOnlyList<DisplayItem> _items;
    private Dictionary<(string style, string status, bool disconnected), Bitmap> _cache;

    // Chip container base color (slightly lighter than BgForm to provide a visible
    // chip boundary against the dark panel background).
    // Seed value — tuned visually in step 10.
    private static readonly Color _chipBgBase = Color.FromArgb(50, 52, 66);

    // Grip glyph dot colors: fixed dimmed/hover alpha over white (Decision D5).
    // PROCESS-LIFETIME shared brushes (the standard WinForms SystemBrushes-style shared-
    // brush pattern) — declared once, reused across every OnPaint call and every
    // OverlayPanel instance for the life of the process. Do NOT dispose these in
    // Dispose(bool)/InvalidateStyleCache: unlike the per-instance _cache bitmap
    // dictionary (which IS correctly disposed per-instance there), disposing a
    // process-lifetime brush would leave it disposed for the NEXT recreated
    // OverlayPanel (structural config reload), which would then throw
    // ObjectDisposedException on every OnPaint (Risk 6).
    private static readonly SolidBrush _gripDimBrush   = new(Color.FromArgb(90,  255, 255, 255));
    private static readonly SolidBrush _gripHoverBrush = new(Color.FromArgb(200, 255, 255, 255));

    // Nullable — no initial hover target; set by controller via SetHoveredChipId.
    private string? _hoveredChipId;

    // True when the cursor is within the grip band (Decision D5) — updated in
    // OnMouseMove; toggles the grip glyph between dimmed (false) and brightened (true).
    private bool _gripHovered;

    // True when DWM owns corner rounding (Win11 22000+); false on Win10 where the GDI Region fallback is used.
    private bool _usesDwmCorners;

    // ── Events ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Raised on every left-click activation dispatch — AFTER a successful router call
    /// (inside the try, so a failed dispatch does not trigger a spurious dismissal). Hover
    /// dashboard controllers subscribe to dismiss their forms immediately, bypassing the
    /// grace-corridor timer.
    ///
    /// Right-click does NOT raise this event. This is deliberate, not an oversight: the
    /// dismissal path (<c>HandleSurfaceInteraction</c> → <c>ForceHideForm</c> →
    /// <c>DisposeForm</c>) destroys a window synchronously, but the activation/z-order
    /// fallout from destroying a window is delivered via POSTED messages that only arrive
    /// after the current message handler returns. Firing this event from the right-click
    /// branch — before or after the router's menu-open call, either ordering — destroys the
    /// dashboard form inside the same synchronous `OnMouseUp` call that opens the
    /// ContextMenuStrip; the posted activation fallout from that destruction then arrives
    /// one message-pump turn later and is seen by `ToolStripManager.ModalMenuFilter` as an
    /// activation change, which force-closes the menu that just opened. The menu appears to
    /// "never open" because it opens and dies within the same frame. Do not "fix" this by
    /// re-adding the invoke to the right-click branch — the two-part rationale above is a
    /// load-bearing constraint, confirmed against a live regression, not a gap to close.
    /// </summary>
    public event Action? SurfaceInteracted;

    /// <summary>
    /// Raised after a grip-drag completes (drop) — regardless of whether the release point
    /// lands over a chip. Distinct from <see cref="SurfaceInteracted"/>: a drag-drop never
    /// dispatches a session/workspace activation, so that event does not fire. TrayApp's
    /// hover-dashboard controllers subscribe to set their post-interaction cooldown here too
    /// — otherwise a chip left under the cursor after the drop would ghost-reshow its
    /// dashboard via the normal dwell timer (Risk 10).
    /// </summary>
    public event Action? DragCompleted;

    // ── Constructor ───────────────────────────────────────────────────────────────

    public OverlayPanel(
        OverlayConfig config,
        ISessionInteractionRouter router,
        IDesktopManager? desktopManager,
        ILoggerFactory loggerFactory,
        GraphicsPackLoader graphicsPackLoader)
    {
        _config            = config;
        _router            = router;
        _desktopManager    = desktopManager;
        _logger            = loggerFactory.CreateLogger<OverlayPanel>();
        _graphicsPackLoader = graphicsPackLoader;

        // Mutable position state — initialized from config; updated in-place by ApplyPositionConfig.
        _position   = config.Position;
        _monitor    = config.Monitor;
        _locked     = config.Locked;
        _offsetX    = config.OffsetX;
        _offsetY    = config.OffsetY;
        _isDragging       = false;
        _dragArmed        = false;
        _dragStartScreen  = default;
        _formStartLocation = default;
        _downHitIndex     = -1;

        // Ctor-init both non-nullable fields (CS8618).
        _items = Array.Empty<DisplayItem>();
        _cache = new Dictionary<(string style, string status, bool disconnected), Bitmap>();

        // Form shell
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar   = false;
        TopMost         = true;
        StartPosition   = FormStartPosition.Manual;
        Cursor          = Cursors.Hand;
        DoubleBuffered  = true;

        // Flicker-free triple (spec §Constraints): DoubleBuffered alone does not suppress
        // the WM_ERASEBKGND flash — the full triple is required.
        SetStyle(
            ControlStyles.AllPaintingInWmPaint |
            ControlStyles.UserPaint            |
            ControlStyles.OptimizedDoubleBuffer,
            true);

        // Solid fallback when DWM mica is unavailable (Win10 ≤19045 or DWM off).
        BackColor = ImrdyPalette.BgForm;
    }

    // ── CreateParams ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Adds WS_EX_TOOLWINDOW only — no layered, transparent, or WS_EX_NOACTIVATE style. The
    /// window is natively activatable, but WndProc's WM_MOUSEACTIVATE handler unconditionally
    /// declines every activation attempt (MA_NOACTIVATE) — left-click, hover, drag, and
    /// right-click alike — so the overlay never steals foreground through ordinary user
    /// interaction and the terminal keeps focus. The one surface that legitimately needs the
    /// overlay's owner window to briefly hold real foreground — the AtControl-anchored
    /// ContextMenuStrip opened by a right-click (see
    /// <see cref="Imrdy.Windows.Interaction.MenuAnchor.AtControl"/>) — gets it explicitly via
    /// TrayApp.ShowContextMenuAt's SetForegroundWindow/AttachThreadInput dance, not through
    /// this window's own activation policy. See WndProc for the full rationale.
    /// MUST start from base.CreateParams — omitting it discards ShowInTaskbar=false
    /// and other styles WinForms bakes into the base ExStyle.
    /// </summary>
    protected override CreateParams CreateParams
    {
        get
        {
            var cp = base.CreateParams;
            cp.ExStyle |= PInvokeOverlay.WS_EX_TOOLWINDOW;
            return cp;
        }
    }

    // ── Form overrides ────────────────────────────────────────────────────────────

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ImrdyPalette.ApplyMica(this);
        _usesDwmCorners = ImrdyPalette.ApplyRoundedCorners(this);
        if (!_usesDwmCorners)
            ImrdyPalette.ApplyRoundedRegion(this);
        PinAcrossVirtualDesktops();
    }

    protected override void OnResize(EventArgs e)
    {
        base.OnResize(e);
        // Region clip must track every size change or the rounded-corner shape becomes stale.
        // Skipped when DWM owns the rounding — DWM tracks size automatically.
        if (!_usesDwmCorners)
            ImrdyPalette.ApplyRoundedRegion(this);
    }

    /// <summary>
    /// No-op override — part of the flicker-free triple. Suppresses the WM_ERASEBKGND
    /// flash. OnPaint handles all painting; no base call.
    /// </summary>
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        // Intentional no-op: base call would flash the background erase before OnPaint.
    }

    /// <summary>
    /// Rendering Contract (spec §Rendering Contract / State × Tier Matrix).
    /// Pure function of (_items, _hoveredChipId, _gripHovered, config).
    /// The grip (Decision D2/D5) is drawn first, unconditionally, in the left
    /// GripWidth-wide band — always visible, dimmed until hover — so it renders even for
    /// the empty-overlay placeholder state. Per chip (L→R, Spacing between,
    /// PanelPadding + GripWidth inset):
    ///   1. Chip background — rounded rect at tier-driven alpha (aging visual).
    ///   2. Status glyph — from (style,status) cache, full alpha tiers 0–3, ~85% tier4.
    ///   3. Alert cue — non-color outline for error/permission (Decision 2d).
    ///   4. Hover highlight — controller-pushed via SetHoveredChipId.
    /// Empty state: single dimmed placeholder; panel never zero-width/invisible (Decision 6).
    /// HitIconIndex subtracts the same PanelPadding + GripWidth inset, so paint and
    /// hit-test share one origin (Risk 2's sync gate).
    /// </summary>
    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(ImrdyPalette.BgForm);

        // Drawn independent of the chip loop so the grip is always present, even for an
        // empty overlay (Decision D5).
        PaintGrip(g);

        var items = _items;

        // Decision 6: zero sessions → single dimmed placeholder, panel stays visible.
        if (items.Count == 0)
        {
            PaintPlaceholderChip(g);
            return;
        }

        var size      = _config.Size;
        var spacing   = _config.Spacing;
        var gripWidth = GripWidth;

        for (var i = 0; i < items.Count; i++)
        {
            var item  = items[i];
            var chipX = PanelPadding + gripWidth + i * (size + spacing);
            PaintChip(g, chipX, PanelPadding, size, item);
        }
    }

    /// <summary>
    /// Draws the left grip handle (Decision D2/D5): a 6-dot (2 columns × 3 rows)
    /// drag-handle glyph, vertically centered in the GripWidth-wide band at the panel's
    /// left edge. Dimmed by default; brightened when <see cref="_gripHovered"/> is true.
    /// Uses the process-lifetime static readonly grip brushes only — never allocates a
    /// Brush/Pen here (Risk 6; OnPaint runs on every drain tick and hover transition).
    /// </summary>
    private void PaintGrip(Graphics g)
    {
        var gripWidth = GripWidth;
        var brush     = _gripHovered ? _gripHoverBrush : _gripDimBrush;

        const int dotSize    = 3;
        const int rowSpacing = 4;
        const int colSpacing = 5;

        var gridHeight = 3 * dotSize + 2 * rowSpacing;
        var startY     = (this.ClientSize.Height - gridHeight) / 2;

        var gridWidth = 2 * dotSize + colSpacing;
        var col1X     = PanelPadding + Math.Max(0, (gripWidth - gridWidth) / 2);
        var col2X     = col1X + dotSize + colSpacing;

        for (var row = 0; row < 3; row++)
        {
            var y = startY + row * (dotSize + rowSpacing);
            g.FillEllipse(brush, col1X, y, dotSize, dotSize);
            g.FillEllipse(brush, col2X, y, dotSize, dotSize);
        }
    }

    private void PaintChip(Graphics g, int chipX, int chipY, int size, DisplayItem item)
    {
        var status   = item.Status;
        var tier     = item.AgingTier;
        var isAlert  = IsAlertStatus(status);
        var chipRect = new Rectangle(chipX, chipY, size, size);

        // ── 1. Chip background (tier-driven opacity fade = aging visual) ──────────
        var chipAlpha = ChipBgAlpha(tier, isAlert);
        using var bgPath  = BuildRoundedRect(chipRect, ChipCornerRadius);
        using var bgBrush = new SolidBrush(
            Color.FromArgb(chipAlpha, _chipBgBase.R, _chipBgBase.G, _chipBgBase.B));
        g.FillPath(bgBrush, bgPath);

        // ── 2. Status glyph (from (style,status) cache, inset by ChipPadding) ────
        var glyphSize = size - 2 * ChipPadding;
        if (glyphSize > 0)
        {
            var glyph     = GetOrCreateBitmap(item.IconStyle, status, item.IsDisconnected);
            var glyphRect = new Rectangle(chipX + ChipPadding, chipY + ChipPadding, glyphSize, glyphSize);

            if (tier <= 3)
            {
                // Aging expressed via chip-bg opacity only — glyph at full alpha.
                g.DrawImage(glyph, glyphRect);
            }
            else
            {
                // tier4: slight composite dim (≈85% alpha) per spec.
                using var ia = new ImageAttributes();
                var cm       = new ColorMatrix();
                cm.Matrix33  = 0.85f;
                ia.SetColorMatrix(cm);
                g.DrawImage(glyph, glyphRect, 0, 0, glyph.Width, glyph.Height, GraphicsUnit.Pixel, ia);
            }
        }

        // ── 3. Redundant non-color cue for error/permission (Decision 2d) ────────
        if (isAlert)
            PaintAlertCue(g, chipRect);

        // ── 4. Hover highlight ───────────────────────────────────────────────────
        if (item.Id == _hoveredChipId)
            PaintHoverHighlight(g, chipRect);
    }

    private void PaintPlaceholderChip(Graphics g)
    {
        // Single dimmed imrdy-glyph placeholder for zero-session empty state (Decision 6).
        // Panel stays visible at MinimumPanelWidth. Seed values tuned in step 10.
        // Origin shifted right by GripWidth (same as the per-chip loop) so it never sits
        // underneath the grip glyph.
        const int   placeholderChipAlpha  = 50;
        const float placeholderGlyphAlpha = 0.30f;

        var size     = _config.Size;
        var chipX    = PanelPadding + GripWidth;
        var chipRect = new Rectangle(chipX, PanelPadding, size, size);

        using var bgPath  = BuildRoundedRect(chipRect, ChipCornerRadius);
        using var bgBrush = new SolidBrush(
            Color.FromArgb(placeholderChipAlpha, _chipBgBase.R, _chipBgBase.G, _chipBgBase.B));
        g.FillPath(bgBrush, bgPath);

        var glyphSize = size - 2 * ChipPadding;
        if (glyphSize > 0)
        {
            // "circles"/"idle" (green circle) at very low opacity → calm "ready, no sessions" feel.
            // The glyph style can be updated to an imrdy brand icon when one is available.
            var glyph     = GetOrCreateBitmap("circles", "idle", disconnected: false);
            var glyphRect = new Rectangle(chipX + ChipPadding, PanelPadding + ChipPadding, glyphSize, glyphSize);
            using var ia  = new ImageAttributes();
            var cm        = new ColorMatrix();
            cm.Matrix33   = placeholderGlyphAlpha;
            ia.SetColorMatrix(cm);
            g.DrawImage(glyph, glyphRect, 0, 0, glyph.Width, glyph.Height, GraphicsUnit.Pixel, ia);
        }
    }

    private static void PaintAlertCue(Graphics g, Rectangle chipRect)
    {
        // Non-color redundant cue for error/permission: thin white border outline (Decision 2d).
        // One cue only — no theme system. Appearance tuned via imrdy render (step 10).
        using var path = BuildRoundedRect(chipRect, ChipCornerRadius);
        using var pen  = new Pen(Color.FromArgb(180, 255, 255, 255), 1.5f);
        g.DrawPath(pen, path);
    }

    private static void PaintHoverHighlight(Graphics g, Rectangle chipRect)
    {
        // Subtle white tint fill + ring on hover. Appearance tuned via imrdy render (step 10).
        using var path     = BuildRoundedRect(chipRect, ChipCornerRadius);
        using var fillBrush = new SolidBrush(Color.FromArgb(30, 255, 255, 255));
        using var ringPen   = new Pen(Color.FromArgb(80, 255, 255, 255), 1.0f);
        g.FillPath(fillBrush, path);
        g.DrawPath(ringPen, path);
    }

    private static GraphicsPath BuildRoundedRect(Rectangle rect, int radius)
    {
        // Clamp diameter so arcs don't degenerate on very small chips.
        var d    = Math.Min(2 * radius, Math.Min(rect.Width, rect.Height));
        var path = new GraphicsPath();
        path.AddArc(rect.X,         rect.Y,          d, d, 180, 90);
        path.AddArc(rect.Right - d, rect.Y,          d, d, 270, 90);
        path.AddArc(rect.Right - d, rect.Bottom - d, d, d,   0, 90);
        path.AddArc(rect.X,         rect.Bottom - d, d, d,  90, 90);
        path.CloseFigure();
        return path;
    }

    // Seed alpha ladder (tuned by eye in step 10 via imrdy render).
    private static int ChipBgAlpha(int tier, bool isAlert)
    {
        var alpha = tier switch
        {
            0 => 255,
            1 => 200,
            2 => 160,
            3 => 120,
            _ => 80,   // tier4
        };
        // Raised opacity floor: permission/error remain clearly visible at every tier
        // (Decision 2c — never the faint alpha-30 of the old layered path).
        const int AlertFloor = 160;
        return isAlert ? Math.Max(alpha, AlertFloor) : alpha;
    }

    private static bool IsAlertStatus(string status)
        => string.Equals(status, "permission", StringComparison.OrdinalIgnoreCase)
           || string.Equals(status, "error",      StringComparison.OrdinalIgnoreCase);

    // ── WndProc ───────────────────────────────────────────────────────────────────

    protected override void WndProc(ref Message m)
    {
        // Focus preservation: always return MA_NOACTIVATE — no base call (Raymond Chen).
        // The overlay never steals foreground; the terminal keeps focus through every drag,
        // click, and right-click. Unlike HoverDashboardFormBase there is no pin concept —
        // always NoActivate.
        //
        // An earlier attempt returned MA_ACTIVATE for a right-button-down here instead, to
        // give the AtControl-anchored ContextMenuStrip (see MenuAnchor.AtControl) a real
        // foreground owner — ToolStripManager's modal menu filter force-closes a menu whose
        // owner never took foreground the instant any unrelated activation change happens
        // elsewhere on the desktop. That did not work: WM_MOUSEACTIVATE activation is not the
        // same thing as owning foreground *input*, which is what SetForegroundWindow and
        // ContextMenuStrip.Show actually need — right-clicks still silently no-op'd, and
        // SetForegroundWindow-based foreground restore still failed most of the time. The fix
        // now lives entirely in TrayApp.ShowContextMenuAt, which grants the overlay real
        // foreground via PInvokeWindow.SetForegroundWindow wrapped in
        // PInvokeWindow.InvokeWithForegroundAttached (the AttachThreadInput dance) immediately
        // before menu.Show, and restores whatever held foreground before the menu opened once
        // it closes — none of which requires this window to ever actually activate.
        if (m.Msg == WM_MOUSEACTIVATE) { m.Result = (IntPtr)MA_NOACTIVATE; return; }

        // Drag cancel: foreground stolen (app-switch, ALT+TAB, etc.) → release capture.
        if (m.Msg == WM_CANCELMODE) { ResetDragState(); }   // fall through to base

        // Guard FIRST: RegisterWindowMessage returns 0 on failure (atom-table exhausted).
        // Without this guard, a 0 value would match WM_NULL (0x0000) and fire
        // PinAcrossVirtualDesktops on every no-op message — catastrophic throughput hit.
        if (_wmTaskbarCreated == 0)
        {
            base.WndProc(ref m);
            return;
        }

        // Cast to uint avoids sign-extension; registered message IDs live in 0xC000–0xFFFF.
        if ((uint)m.Msg == _wmTaskbarCreated)
        {
            // Explorer restarted — shell discarded all-desktop pin state; re-pin (Decision 9).
            PinAcrossVirtualDesktops();
        }

        base.WndProc(ref m);
    }

    // ── Public API consumed by controllers + TrayApp ──────────────────────────────

    /// <summary>
    /// Live tray path. Sizes the panel to fit the items, computes placement, and
    /// repaints. Never hides the panel on empty — the empty state renders the idle
    /// placeholder (Decision 6).
    /// </summary>
    public void UpdateItems(IReadOnlyList<DisplayItem> items)
    {
        ApplyItemsAndSize(items);
        this.Location = CalculatePosition();
        Invalidate();
    }

    /// <summary>
    /// Render path only (imrdy render / OverlayRenderer). Sizes the panel and repaints
    /// but skips CalculatePosition — the form stays at the offscreen position set by
    /// the Show pattern and never flashes onto the visible desktop.
    /// </summary>
    internal void LoadFixtureItems(IReadOnlyList<DisplayItem> items)
    {
        ApplyItemsAndSize(items);
        Invalidate();
    }

    /// <summary>
    /// Pushes the controller/drain-owned hover-highlight target into the form.
    /// Invalidates only when the id changes. The form is purely presentational —
    /// it does not poll Cursor.Position or read the clock.
    /// </summary>
    public void SetHoveredChipId(string? id)
    {
        if (_hoveredChipId == id) return;
        _hoveredChipId = id;
        Invalidate();
    }

    /// <summary>
    /// Maps a screen-coordinate point to the session id of the icon under it.
    /// Returns false for gaps, workspace items, or points outside the panel.
    /// </summary>
    public bool TryGetSessionIdAtScreenPoint(Point screenPt, out string sessionId)
    {
        sessionId = string.Empty;

        int cx = screenPt.X, cy = screenPt.Y;
        if (!PInvokeOverlay.ScreenToClientPoint(Handle, ref cx, ref cy))
            return false;

        if (!HitIconIndex(cx, out var index))
            return false;

        if (index < 0 || index >= _items.Count)
            return false;

        var item = _items[index];
        if (item.ItemType != DisplayItemType.Session)
            return false;

        sessionId = item.Id;
        return true;
    }

    /// <summary>
    /// Maps an already-converted client-X coordinate to the DisplayItem at that slot.
    /// Used by HoverDashboardControllerBase-derived controllers whose
    /// TryHitTestForOurDomain has already done the screen→client conversion.
    /// </summary>
    public bool TryHitTestAtClient(int clientX, out DisplayItem? item, out int hitIndex)
    {
        item     = null;
        hitIndex = -1;

        if (!HitIconIndex(clientX, out var index))
            return false;

        if (index < 0 || index >= _items.Count)
            return false;

        item     = _items[index];
        hitIndex = index;
        return true;
    }

    /// <summary>
    /// Disposes every Bitmap in the (style, status, disconnected) cache, clears it, and repaints.
    /// Called from TrayApp on icon-style change so the next OnPaint rebuilds glyphs
    /// with the new style.
    /// </summary>
    public void InvalidateStyleCache()
    {
        foreach (var bmp in _cache.Values) bmp.Dispose();
        _cache.Clear();
        Invalidate();
    }

    /// <summary>
    /// Updates the five mutable position fields and re-docks the panel in place —
    /// no dispose/recreate, no Show, no flash.
    /// </summary>
    /// <remarks>
    /// Contract:
    ///   requires — caller runs on the UI/message-pump thread; handle created.
    ///   ensures  — _position/_monitor/_locked/_offsetX/_offsetY == arguments; this.Location
    ///              recomputed in place via the offset→anchor→default resolution chain.
    ///   invariants — never calls this.Activate(); never changes this.Size.
    ///   throws   — never (out-of-range monitor absorbed by ResolveTargetScreen primary fallback).
    ///
    /// THREAD-AFFINITY: the Debug.Assert guard is stripped in Release builds.
    /// Valid callers: OnMouseUp (drag drop) and TrayApp.OnConfigChanged (drain tick) only.
    /// </remarks>
    public void ApplyPositionConfig(string position, int monitor, bool locked, int? offsetX, int? offsetY)
    {
        Debug.Assert(!InvokeRequired, "ApplyPositionConfig must be called on the UI thread");
        _position = position;
        _monitor  = monitor;
        _locked   = locked;
        _offsetX  = offsetX;
        _offsetY  = offsetY;
        this.Location = CalculatePosition();
    }

    /// <summary>
    /// True while the user is actively dragging the panel (threshold crossed).
    /// Read by TrayApp.OnConfigChanged to defer overlay reconfiguration mid-drag.
    /// Set true in OnMouseMove once the drag threshold is crossed; reset to false by
    /// ResetDragState on every FSM exit path (drop, Escape, WM_CANCELMODE).
    /// </summary>
    public bool IsDragging => _isDragging;

    // ── Mouse handling ────────────────────────────────────────────────────────────

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            // Record start points for the drag FSM regardless of lock state
            // (needed for the click branch — _downHitIndex dispatches activation on LeftUp).
            _dragStartScreen   = Cursor.Position;
            _formStartLocation = this.Location;
            _downHitIndex      = HitIconIndex(e.X, out var idx) ? idx : -1;

            // Grip is the ONLY drag-arming zone (D10) — chip and gutter mouse-downs still
            // record _downHitIndex above (for the click branch) but never arm the drag.
            if (IsGripHit(e.X) && !_locked)
            {
                // Arm the drag: capture keeps WM_MOUSEMOVE/WM_LBUTTONUP flowing
                // even when the cursor leaves the panel during a fast drag.
                _dragArmed   = true;
                this.Capture = true;
            }
            // MUST NOT call this.Activate() — focus-preservation invariant.
            // Activation relocated to the OnMouseUp click branch.
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        // Grip dim↔hover state (Decision D5) — independent of the drag FSM branches
        // below; e.X is already client-relative (WM_MOUSEMOVE lParam), matching the
        // existing HitIconIndex(e.X, ...) call sites elsewhere in this method.
        UpdateGripHover(e.X);

        if (_isDragging)
        {
            // Active drag: convert physical-pixel Cursor.Position delta to logical pixels
            // before applying to this.Location (Per-Monitor V2 coordinate space).
            // At 150% DPI the raw physical delta is 1.5× the logical delta — without
            // this conversion the panel trails the cursor (Risk 2).
            var dx    = Cursor.Position.X - _dragStartScreen.X;
            var dy    = Cursor.Position.Y - _dragStartScreen.Y;
            float scale = this.DeviceDpi / 96f;
            this.Location = new Point(
                _formStartLocation.X + (int)(dx / scale),
                _formStartLocation.Y + (int)(dy / scale));
            // Re-assert static cursor — OS suppresses WM_SETCURSOR during capture,
            // making the instance this.Cursor invisible while dragging.
            Cursor.Current = Cursors.SizeAll;
        }
        else if (_dragArmed)
        {
            // Armed but below threshold: check whether the cursor has left the drag-threshold
            // rect. GetSystemMetricsForDpi (D6) is Per-Monitor-V2-aware, unlike
            // SystemInformation.DragSize, which wraps the non-Per-Monitor-V2-aware
            // GetSystemMetrics and yields the wrong threshold on a monitor whose DPI
            // differs from the system DPI.
            var dragSize = new Size(
                PInvokeOverlay.GetSystemMetricForDpi(PInvokeOverlay.SM_CXDRAG, this.DeviceDpi),
                PInvokeOverlay.GetSystemMetricForDpi(PInvokeOverlay.SM_CYDRAG, this.DeviceDpi));
            var dragRect = new Rectangle(
                _dragStartScreen.X - dragSize.Width  / 2,
                _dragStartScreen.Y - dragSize.Height / 2,
                dragSize.Width,
                dragSize.Height);
            if (!dragRect.Contains(Cursor.Position))
            {
                _isDragging    = true;
                Cursor.Current = Cursors.SizeAll;
            }
        }
        else
        {
            // Idle hover-hint via instance cursor (WM_SETCURSOR path — not suppressed when not capturing).
            // Three zones (D10 — grip is the ONLY drag-arming zone, gutter is no longer draggable):
            //   chip           → Hand    (click affordance, locked or not)
            //   grip + !locked → SizeAll (drag affordance)
            //   grip + locked  → Default (grip is inert when locked)
            //   gutter (else)  → Default (never draggable, click is a no-op)
            if (HitIconIndex(e.X, out _))
                this.Cursor = Cursors.Hand;
            else if (IsGripHit(e.X) && !_locked)
                this.Cursor = Cursors.SizeAll;
            else
                this.Cursor = Cursors.Default;
        }

        base.OnMouseMove(e);
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        // Reset idle hover-hint cursor only — never cancel an active captured drag.
        // Capture keeps WM_MOUSEMOVE/WM_LBUTTONUP flowing while the cursor is outside the panel;
        // OnMouseMove re-asserts Cursor.Current = SizeAll on the next move event.
        if (!_isDragging)
            this.Cursor = Cursors.Default;
        // The cursor left the panel entirely, so it is no longer over the grip band.
        UpdateGripHover(-1);
        base.OnMouseLeave(e);
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        if (e.KeyCode == Keys.Escape && (_dragArmed || _isDragging))
        {
            var wasDragging = _isDragging;
            ResetDragState();
            if (wasDragging)
                this.Location = CalculatePosition(); // Revert panel to persisted anchor; no persist write.
            // Pure-Armed branch (threshold not yet crossed) skips CalculatePosition() —
            // the panel has not moved from its persisted position.
            e.Handled = true;
        }
        base.OnKeyDown(e);
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            if (_isDragging)
            {
                // Drag completion (D1 — free-float + edge snap): compute the free-float
                // origin from the dragged Location, magnetically snap to a working-area
                // edge/corner within 24px, clamp fully on-screen (D9), re-place flash-free,
                // persist async. Uses the monitor under the CURSOR (not the panel) — same
                // multi-monitor convention the retired ComputeSnap() used.
                var screen      = Screen.FromPoint(Cursor.Position);
                var workingArea = screen.WorkingArea;
                var snapped     = OverlayPlacement.ComputeEdgeSnap(this.Location, this.Size, workingArea);
                var clamped     = OverlayPlacement.ClampToWorkingArea(snapped, this.Size, workingArea);
                var monitor     = IndexOfScreen(screen);
                var offsetX     = clamped.X - workingArea.Left;
                var offsetY     = clamped.Y - workingArea.Top;

                // Apply in-memory FIRST so the panel lands correctly even if the persist
                // below fails (step contract — snapped offset applied before the write).
                ApplyPositionConfig(_position, monitor, _locked, offsetX, offsetY);

                // Fire-and-forget persist — no CancellationToken (ConfigReader.Update wraps
                // synchronous AtomicFileWriter; no cancellation point inside).
                // ContinueWith runs only on fault; unwraps AggregateException for structured logs.
                _ = Task.Run(() => ConfigReader.Update(c => c with
                    {
                        Overlay = c.Overlay with { OffsetX = offsetX, OffsetY = offsetY, Monitor = monitor }
                    }))
                    .ContinueWith(
                        t => _logger.LogError(t.Exception?.InnerException ?? t.Exception,
                            "overlay offset persist failed"),
                        TaskContinuationOptions.OnlyOnFaulted);

                // Post-drop cooldown (Risk 10): the drag-drop path never dispatches an
                // activation, so SurfaceInteracted does not fire — raise DragCompleted so
                // TrayApp's hover controllers set their own post-interaction cooldown.
                DragCompleted?.Invoke();

                ResetDragState();
                // No activation on drag completion (drag ≠ click — SurfaceInteracted not fired).
            }
            else
            {
                // Click branch: pointer moved less than DragSize — treat as a click.
                ResetDragState();
                if (_downHitIndex >= 0 && _downHitIndex < _items.Count)
                {
                    var item = _items[_downHitIndex];
                    try
                    {
                        if (item.ItemType == DisplayItemType.Session)
                            _router.ActivateSession(item.Id);
                        else
                            _router.ActivateWorkspace(item.Id);
                        SurfaceInteracted?.Invoke();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "OverlayPanel: left-click dispatch failed for {ItemType} {Id}",
                            item.ItemType, item.Id);
                    }
                }
                // Gutter click (_downHitIndex < 0): no-op.
            }
        }
        else if (e.Button == MouseButtons.Right)
        {
            if (HitIconIndex(e.X, out var idx) && idx < _items.Count)
            {
                // Chip right-click: open the session/workspace context menu.
                var item = _items[idx];
                try
                {
                    var anchor = MenuAnchor.AtControl(this, e.Location);
                    if (item.ItemType == DisplayItemType.Session)
                        _router.OpenSessionMenu(item.Id, anchor);
                    else
                        _router.OpenWorkspaceMenu(item.Id, anchor);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "OverlayPanel: right-click menu failed for {ItemType} {Id}",
                        item.ItemType, item.Id);
                }
            }
            else
            {
                // Gutter/padding right-click: open the overlay settings menu.
                // Invariant: OverlayPanel never builds a menu directly — all routing goes through
                // ISessionInteractionRouter so call sites are uniform and auditable.
                // try/catch added here to match the chip-hit branch above — this branch never had
                // exception handling before, which was a pre-existing gap independent of the
                // SurfaceInteracted ordering bug this file's other comments discuss.
                try
                {
                    _router.OpenOverlayMenu(MenuAnchor.AtControl(this, e.Location));
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "OverlayPanel: gutter right-click menu failed");
                }
            }
        }

        base.OnMouseUp(e);
    }

    // ── Disposal ──────────────────────────────────────────────────────────────────

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            // Each cached Bitmap holds a native HBITMAP; dispose all to prevent GDI handle
            // leaks toward the 10,000/process limit (especially critical for render-path
            // multi-run scenarios where one panel is constructed per fixture).
            foreach (var bmp in _cache.Values) bmp.Dispose();
            _cache.Clear();
        }
        base.Dispose(disposing);
    }

    // ── Private helpers ───────────────────────────────────────────────────────────

    /// <summary>
    /// Stores items, computes chip geometry, and resizes the panel.
    /// Does NOT set Location — callers are responsible for positioning.
    /// </summary>
    private void ApplyItemsAndSize(IReadOnlyList<DisplayItem> items)
    {
        _items = items;
        // Minimum 1 chip slot for the idle placeholder when items is empty (Decision 6).
        var count      = Math.Max(1, items.Count);
        var panelWidth = Math.Max(
            MinimumPanelWidth,
            GripWidth + count * _config.Size + (count - 1) * _config.Spacing + 2 * PanelPadding);
        var panelHeight = _config.Size + 2 * PanelPadding;
        this.Size = new Size(panelWidth, panelHeight);
    }

    /// <summary>
    /// Computes the dock position via the Core offset→anchor→default resolution chain
    /// (<see cref="OverlayPlacement.ResolveOrigin"/>). Reads the mutable
    /// <c>_offsetX</c>/<c>_offsetY</c>/<c>_position</c> fields — NOT <c>_config.*</c> — so
    /// in-place re-docking via <see cref="ApplyPositionConfig"/> takes effect without
    /// recreate. The raw nullable offsets are passed straight through; Core owns the null
    /// resolution and the 16px margin / bottom taskbar-reserve constants — do not
    /// duplicate them here. Garbage/unknown position strings fall back to bottom-right
    /// (spec §Error Handling), handled inside <see cref="OverlayAnchor.Parse"/>.
    /// </summary>
    private Point CalculatePosition()
    {
        var screen = ResolveTargetScreen();
        return OverlayPlacement.ResolveOrigin(_offsetX, _offsetY, _position, screen.WorkingArea, this.Size);
    }

    /// <summary>
    /// Returns the Screen the panel should dock to. Falls back to primary (or first)
    /// screen when <c>_monitor</c> is out of range.
    /// Reads <c>_monitor</c> (mutable) — NOT <c>_config.Monitor</c> — so
    /// <see cref="ApplyPositionConfig"/> re-targets the correct monitor in-place.
    /// Null-coalescing on Screen.PrimaryScreen prevents CS8602 under TreatWarningsAsErrors.
    /// </summary>
    private Screen ResolveTargetScreen()
    {
        var screens = Screen.AllScreens;
        if (_monitor >= 0 && _monitor < screens.Length)
            return screens[_monitor];
        return Screen.PrimaryScreen ?? screens[0];
    }

    /// <summary>
    /// Maps a client X coordinate to a chip slot index. Subtracts PanelPadding + GripWidth
    /// before delegating to TryGetItemAtClientPoint so the slot math matches OnPaint
    /// geometry: both use i * (size + spacing) as the chip origin, offset by
    /// PanelPadding + GripWidth from the panel left edge (the grip band shifts every chip
    /// right by GripWidth — Risk 2's paint/hit-test sync gate). A click in the left
    /// PanelPadding gutter or the grip band returns false.
    /// </summary>
    private bool HitIconIndex(int clientX, out int index)
    {
        return DisplayItemCollection.TryGetItemAtClientPoint(
            _items, clientX - PanelPadding - GripWidth, _config.Size, _config.Spacing,
            out _, out index);
    }

    /// <summary>
    /// True when <paramref name="clientX"/> falls inside the left grip band — the SOLE
    /// drag-arming zone (D10). The single source of truth consumed by both the grip-hit
    /// test in <see cref="OnMouseDown"/> and the hover-dim state in
    /// <see cref="UpdateGripHover"/>, so the two never desync (Risk 2's paint/hit-test sync
    /// gate extends to hover). A negative <paramref name="clientX"/> (e.g. from
    /// <see cref="OnMouseLeave"/>) always resolves to false.
    /// </summary>
    private bool IsGripHit(int clientX) => clientX >= 0 && clientX < PanelPadding + GripWidth;

    /// <summary>
    /// Updates <see cref="_gripHovered"/> from a client-X coordinate and invalidates only
    /// the grip band on a dim↔hover transition (Decision D5). No-op when the state does not
    /// change — avoids redundant repaints on every mouse-move tick.
    /// </summary>
    private void UpdateGripHover(int clientX)
    {
        var hovered = IsGripHit(clientX);
        if (hovered == _gripHovered) return;
        _gripHovered = hovered;
        Invalidate(new Rectangle(0, 0, PanelPadding + GripWidth, this.ClientSize.Height));
    }

    private void PinAcrossVirtualDesktops()
    {
        // Null in the render path — pinning skipped via null-conditional.
        // Idempotent; COM-exception absorption lives inside the IDesktopManager impl.
        _desktopManager?.PinWindowToAllDesktops(this.Handle);
    }

    // ── Drag helpers ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Idempotent drag-state cleanup. Called from every FSM exit path — drop, Escape,
    /// and WM_CANCELMODE. Releases capture and resets the static cursor.
    /// </summary>
    private void ResetDragState()
    {
        _dragArmed  = false;
        _isDragging = false;
        if (this.Capture) this.Capture = false;
        Cursor.Current = Cursors.Default;
    }

    /// <summary>
    /// Returns the index of <paramref name="screen"/> in <see cref="Screen.AllScreens"/>
    /// matched by <see cref="Screen.DeviceName"/>. Falls back to 0 when not found.
    /// Used by the <see cref="OnMouseUp"/> drag-drop free-float branch to resolve the
    /// persisted <c>Monitor</c> index for the screen under the cursor at release.
    /// </summary>
    private static int IndexOfScreen(Screen screen)
    {
        var screens = Screen.AllScreens;
        for (var i = 0; i < screens.Length; i++)
        {
            if (string.Equals(screens[i].DeviceName, screen.DeviceName, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 0;
    }

    // ── Glyph bitmap cache (ported from OverlayWindowBase) ───────────────────────
    // Key is (style, status, disconnected) — TIER-INDEPENDENT. Aging is NOT baked into the
    // glyph; it is expressed purely as chip-background opacity in OnPaint (Decision 7).
    // D20's disconnected treatment IS baked in, and deliberately so: it is a shape change,
    // not an opacity change, so it cannot ride the chip-background ladder that carries aging.
    // ApplyAgingColorMatrix, layered-window composite, SetBounds defense, and
    // Visible=false-on-empty are all deleted — they belong to the layered-window path.

    private Bitmap GetOrCreateBitmap(string style, string status, bool disconnected)
    {
        var key = (style, status, disconnected);
        if (_cache.TryGetValue(key, out var cached)) return cached;

        Bitmap? bitmap = null;
        try
        {
            bitmap      = RenderBitmap(style, status, disconnected);
            _cache[key] = bitmap;
            return bitmap;
        }
        catch (Exception ex)
        {
            bitmap?.Dispose();
            _logger.LogWarning(ex,
                "OverlayPanel: failed to render bitmap for style='{Style}' status='{Status}', falling back to circle.",
                style, status);
            var fallback = RenderCircleFallback(status, disconnected);
            _cache[key]  = fallback;
            return fallback;
        }
    }

    private Bitmap RenderBitmap(string style, string status, bool disconnected)
    {
        var size          = _config.Size;
        var shapeDelegate = GetShapeDelegate(style);

        var glyph = shapeDelegate is not null
            ? RenderBuiltInShape(shapeDelegate, status, size)
            : style.StartsWith("pack:", StringComparison.OrdinalIgnoreCase)
                ? RenderFromPack(style, status, size)
                : throw new InvalidOperationException($"Unknown icon style '{style}'.");

        if (!disconnected) return glyph;

        try
        {
            return DisconnectedGlyph.Apply(glyph);
        }
        finally
        {
            glyph.Dispose();
        }
    }

    // Aging bake-in DROPPED: GetAgingFactorFromTier call and aged* local vars removed.
    // OnPaint expresses aging via chip-background opacity — the glyph always carries the
    // full status color, which makes the cache tier-independent.
    private static Bitmap RenderBuiltInShape(Action<Graphics, RectangleF, Brush> drawDelegate, string status, int size)
    {
        var (r, gv, b) = StatusMap.ResolveColor(status);

        Bitmap? bitmap = null;
        try
        {
            bitmap = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
            using var graphics = Graphics.FromImage(bitmap);
            graphics.SmoothingMode = SmoothingMode.AntiAlias;
            graphics.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.FromArgb(r, gv, b));
            var rect = new RectangleF(1, 1, size - 2, size - 2);
            drawDelegate(graphics, rect, brush);
            return bitmap;
        }
        catch
        {
            bitmap?.Dispose();
            throw;
        }
    }

    // Tier-branch DROPPED: no ApplyAgingColorMatrix path; ClonePArgb used unconditionally.
    // (OverlayWindowBase:236-239 tier == 0 ? ClonePArgb : ApplyAgingColorMatrix deleted.)
    private Bitmap RenderFromPack(string style, string status, int size)
    {
        var packName  = style["pack:".Length..];
        var packsRoot = Path.GetFullPath(ImrdyPaths.GraphicsPacksDir);
        var packDir   = Path.GetFullPath(Path.Combine(packsRoot, packName));
        if (!packDir.StartsWith(packsRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Invalid pack name '{packName}' — path traversal detected.");

        var packJsonPath = Path.Combine(packDir, "pack.json");
        var pack = _graphicsPackLoader.LoadPack(packDir, packJsonPath)
            ?? throw new InvalidOperationException($"Pack '{packName}' could not be loaded.");

        if (!pack.StateFilePaths.TryGetValue(status, out var filePath))
        {
            if (!pack.StateFilePaths.TryGetValue("unknown", out filePath) &&
                !pack.StateFilePaths.TryGetValue("idle",    out filePath))
            {
                throw new InvalidOperationException($"Pack '{packName}' has no entry for status '{status}'.");
            }
        }

        // SvgDocument does not implement IDisposable in Svg.NET 3.4.7
        var doc = SvgDocument.Open(filePath);
        using var baseBitmap = EnsurePArgb(doc.Draw(size, size), size);

        Bitmap? result = null;
        try
        {
            result = ClonePArgb(baseBitmap, size);
            return result;
        }
        catch
        {
            result?.Dispose();
            throw;
        }
    }

    private Bitmap RenderCircleFallback(string status, bool disconnected)
    {
        try
        {
            var circle = RenderBuiltInShape(ShapeDefinitions.Circle, status, _config.Size);
            if (!disconnected) return circle;

            try
            {
                return DisconnectedGlyph.Apply(circle);
            }
            finally
            {
                circle.Dispose();
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex,
                "OverlayPanel: even circle fallback failed for status='{Status}'.", status);
            return new Bitmap(_config.Size, _config.Size, PixelFormat.Format32bppPArgb);
        }
    }

    private static Action<Graphics, RectangleF, Brush>? GetShapeDelegate(string style) => style switch
    {
        "circles"   => ShapeDefinitions.Circle,
        "squares"   => ShapeDefinitions.Square,
        "triangles" => ShapeDefinitions.Triangle,
        "diamonds"  => ShapeDefinitions.Diamond,
        "hexagons"  => ShapeDefinitions.Hexagon,
        "plus"      => ShapeDefinitions.Plus,
        _           => null
    };

    private static Bitmap EnsurePArgb(Bitmap source, int size)
    {
        if (source.PixelFormat == PixelFormat.Format32bppPArgb) return source;
        var converted = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(converted);
        g.DrawImage(source, 0, 0, size, size);
        source.Dispose();
        return converted;
    }

    private static Bitmap ClonePArgb(Bitmap source, int size)
    {
        var clone = new Bitmap(size, size, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(clone);
        g.DrawImage(source, 0, 0, size, size);
        return clone;
    }
}
