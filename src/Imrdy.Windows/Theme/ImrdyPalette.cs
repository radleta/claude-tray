using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace Imrdy.Windows.Theme;

/// <summary>
/// Shared visual shell constants and helpers consumed by all dashboard forms and the overlay panel.
/// Centralises the 5 palette colors, DWM mica backdrop application, and rounded Region clip.
/// Lives in Imrdy.Windows (not Core) because ApplyMica/ApplyRoundedRegion reference
/// System.Windows.Forms.Form and Win32 — Core forbids WinForms (layer rule).
/// </summary>
internal static class ImrdyPalette
{
    // DWM backdrop constants
    private const int DWMWA_SYSTEMBACKDROP_TYPE      = 38;
    private const int DWMSBT_MAINWINDOW              = 2; // Mica
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND                   = 2;
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE  = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
    private static extern int SetWindowTheme(IntPtr hwnd, string subAppName, string? subIdList);

    // Colors matching dashboard-ultra.html design.
    // Shared by HoverDashboardFormBase, SessionDashboardForm, WorkspaceDashboardForm, and OverlayPanel.
    // Session-only colors (BgFleet, Border) remain private in SessionDashboardForm.
    internal static readonly Color BgForm      = Color.FromArgb(28, 30, 38);
    internal static readonly Color FgPrimary   = Color.FromArgb(232, 234, 240);
    internal static readonly Color FgSecondary = Color.FromArgb(155, 161, 173);
    internal static readonly Color FgMuted     = Color.FromArgb(107, 111, 122);
    internal static readonly Color BgFooter    = Color.FromArgb(18, 18, 24);

    /// <summary>
    /// Applies the DWM mica backdrop to the given form handle.
    /// Silently swallows HRESULT failures and exceptions on Windows 10 19045 and earlier,
    /// where DWMWA_SYSTEMBACKDROP_TYPE is not supported. The form degrades to its solid
    /// <see cref="BgForm"/> fill on those builds.
    /// </summary>
    internal static bool ApplyMica(Form form)
    {
        try
        {
            var backdropType = DWMSBT_MAINWINDOW;
            var hr = DwmSetWindowAttribute(form.Handle, DWMWA_SYSTEMBACKDROP_TYPE, ref backdropType, sizeof(int));
            return hr == 0;
        }
        catch (Exception)
        {
            // Expected on Win10 19045 and earlier — swallow silently.
            return false;
        }
    }

    /// <summary>
    /// Rounds the window's corners via DWM (Win11 build 22000+). DWM rounds the window
    /// frame and its mica backdrop together, so the corners stay transparent — unlike a
    /// GDI Region, which leaves the DWM backdrop compositing opaque white wedges in the
    /// carved-out corners. Returns true when DWM applied the rounding (S_OK); returns
    /// false on Win10 (≤19045) where the attribute is unsupported, so callers fall back
    /// to ApplyRoundedRegion there (Win10 has no system backdrop, so the GDI region
    /// rounds cleanly without the white-wedge artifact).
    /// </summary>
    internal static bool ApplyRoundedCorners(Form form)
    {
        try
        {
            var pref = DWMWCP_ROUND;
            var hr   = DwmSetWindowAttribute(form.Handle, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
            return hr == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Forces the window's caption into dark mode, independent of the OS theme. Only a form
    /// that keeps its native title bar needs this — every other imrdy surface is borderless,
    /// which is why this arrived with the connections window and not before. Returns true
    /// when DWM accepted it; false on builds predating the attribute, where the caption
    /// follows the OS theme as it always did.
    /// </summary>
    internal static bool ApplyDarkTitleBar(Form form)
    {
        try
        {
            var dark = 1;
            var hr = DwmSetWindowAttribute(form.Handle, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
            return hr == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Puts a control's non-client parts — the scrollbars above all — into the shell's dark
    /// theme. imrdy's surfaces are dark whatever the OS theme is, and a control that owns a
    /// scrollbar paints that scrollbar from the OS theme regardless of its own colours: on a
    /// light-theme machine the connections list showed a white bar across a dark window.
    /// Silently ignored where uxtheme declines; the control keeps the OS scrollbar it has now.
    /// </summary>
    internal static void ApplyDarkScrollbars(Control control)
    {
        try
        {
            SetWindowTheme(control.Handle, "DarkMode_Explorer", null);
        }
        catch (Exception)
        {
            // Nothing to recover: this is cosmetic, and the caller has no better option.
        }
    }

    /// <summary>
    /// Applies a rounded-rectangle Region clip to the form using the given corner radius.
    /// No-op when the form's Width or Height is zero or negative (called before first layout).
    /// The previous Region is disposed before replacing.
    /// Uses <c>using var path</c> so the GraphicsPath is always disposed, even if
    /// <c>new Region(path)</c> throws (disposal-leak fix over the original explicit Dispose).
    /// </summary>
    internal static void ApplyRoundedRegion(Form form, int radius = 14)
    {
        if (form.Width <= 0 || form.Height <= 0) return;
        using var path = new GraphicsPath();
        var diameter = radius * 2;
        path.AddArc(0,                     0,                      diameter, diameter, 180, 90);
        path.AddArc(form.Width - diameter, 0,                      diameter, diameter, 270, 90);
        path.AddArc(form.Width - diameter, form.Height - diameter, diameter, diameter,   0, 90);
        path.AddArc(0,                     form.Height - diameter, diameter, diameter,  90, 90);
        path.CloseFigure();
        var oldRegion = form.Region;
        form.Region   = new Region(path);
        oldRegion?.Dispose();
    }
}
