using System.Drawing;
using System.Drawing.Drawing2D;

namespace Imrdy.Windows.Icons;

/// <summary>
/// D20's disconnected treatment: the glyph shrinks and a dashed ring is drawn around it
/// in the glyph's own colour.
/// <para>
/// The treatment is deliberately NOT more dimming. The aging ladder already owns opacity
/// (<see cref="AgingCache"/> in the tray, chip-background alpha in the overlay), so a
/// disconnected publisher's session has to differ by <em>shape</em> — a ring that is not
/// there otherwise — or it reads as "just older", which is the one thing D20 forbids.
/// </para>
/// <para>
/// Shape-independent on purpose: an erosion-to-outline treatment is nearly a no-op on a
/// thin glyph such as <c>plus</c>, whereas the ring is equally visible for every built-in
/// style and for any pack SVG.
/// </para>
/// </summary>
internal static class DisconnectedGlyph
{
    /// <summary>Fraction of the frame the original glyph is redrawn into, centred.</summary>
    private const float GlyphScale = 0.60f;

    /// <summary>Ring opacity. Below full alpha so the ring reads as a hint, not a border.</summary>
    private const byte RingAlpha = 210;

    /// <summary>Alpha above which a source pixel contributes to the ring colour.</summary>
    private const byte ColorSampleThreshold = 96;

    private static readonly Color NeutralRing = Color.FromArgb(150, 150, 150);

    /// <summary>
    /// Returns a new bitmap carrying the disconnected treatment. The caller owns the
    /// result; <paramref name="source"/> is left untouched.
    /// </summary>
    public static Bitmap Apply(Bitmap source)
    {
        var width = source.Width;
        var height = source.Height;
        var ringColor = SampleGlyphColor(source);

        var result = new Bitmap(width, height, source.PixelFormat);
        try
        {
            using var g = Graphics.FromImage(result);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.Clear(Color.Transparent);

            var inset = new RectangleF(
                width * (1f - GlyphScale) / 2f,
                height * (1f - GlyphScale) / 2f,
                width * GlyphScale,
                height * GlyphScale);
            g.DrawImage(source, inset);

            var thickness = Math.Max(1f, width / 16f);
            using var pen = new Pen(Color.FromArgb(RingAlpha, ringColor), thickness)
            {
                DashStyle = DashStyle.Dash,
            };
            var half = thickness / 2f;
            g.DrawEllipse(pen, half, half, width - thickness, height - thickness);

            return result;
        }
        catch
        {
            result.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Averages the opaque pixels so the ring carries the status colour without the caller
    /// having to thread one through — the pack renderer and the overlay both have a bitmap
    /// and no colour in hand at this point.
    /// </summary>
    private static Color SampleGlyphColor(Bitmap source)
    {
        long r = 0, g = 0, b = 0, n = 0;

        for (var y = 0; y < source.Height; y++)
        {
            for (var x = 0; x < source.Width; x++)
            {
                var c = source.GetPixel(x, y);
                if (c.A < ColorSampleThreshold) continue;
                r += c.R;
                g += c.G;
                b += c.B;
                n++;
            }
        }

        return n == 0
            ? NeutralRing
            : Color.FromArgb((int)(r / n), (int)(g / n), (int)(b / n));
    }
}
