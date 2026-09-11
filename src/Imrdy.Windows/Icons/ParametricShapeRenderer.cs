using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Imrdy.Core.Status;

namespace Imrdy.Windows.Icons;

/// <summary>
/// Renders DPI-aware tray icons for any built-in GDI+ shape.
/// The shape is supplied as a draw delegate at construction time, making all 6 built-in
/// styles (circles, squares, triangles, diamonds, hexagons, plus) share identical GDI
/// discipline and aging logic.
/// Safe icon creation pattern: GetHicon → FromHandle.Clone → DestroyIcon.
/// </summary>
internal sealed class ParametricShapeRenderer : ITrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly Action<Graphics, RectangleF, Brush> _drawShape;
    private readonly AgingCache _cache;

    /// <param name="drawShape">
    /// Delegate that fills one icon frame. Receives the Graphics context, the padded
    /// draw rect, and a pre-constructed SolidBrush. Must not dispose the brush.
    /// </param>
    /// <param name="styleName">
    /// Canonical style name (e.g. "circles", "squares"). Used only for diagnostics.
    /// </param>
    public ParametricShapeRenderer(Action<Graphics, RectangleF, Brush> drawShape, string styleName)
    {
        _drawShape = drawShape;
        _ = styleName; // retained for future diagnostics/logging
        _cache = new AgingCache(CreateIcon);
    }

    /// <inheritdoc/>
    public Icon GetIcon(string status, int ageTier, bool disconnected)
    {
        var (r, g, b) = StatusMap.ResolveColor(status);
        var factor = StatusMap.GetAgingFactorFromTier(ageTier);
        return _cache.GetOrCreate(r, g, b, factor, disconnected);
    }

    /// <inheritdoc/>
    public void Dispose() => _cache.Dispose();

    private Icon CreateIcon(byte r, byte g, byte b, double agingFactor, bool disconnected)
    {
        var size = SystemInformation.SmallIconSize;
        var agedR = (byte)(r * agingFactor);
        var agedG = (byte)(g * agingFactor);
        var agedB = (byte)(b * agingFactor);

        using var bitmap = new Bitmap(size.Width, size.Height);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        using var brush = new SolidBrush(Color.FromArgb(agedR, agedG, agedB));
        // 1px padding to avoid clipping at bitmap edges
        var rect = new RectangleF(1, 1, size.Width - 2, size.Height - 2);
        _drawShape(graphics, rect, brush);

        if (!disconnected)
        {
            return ToIcon(bitmap);
        }

        using var ghosted = DisconnectedGlyph.Apply(bitmap);
        return ToIcon(ghosted);
    }

    private static Icon ToIcon(Bitmap bitmap)
    {
        var hIcon = bitmap.GetHicon();
        try
        {
            return (Icon)Icon.FromHandle(hIcon).Clone();
        }
        finally
        {
            DestroyIcon(hIcon);
        }
    }
}
