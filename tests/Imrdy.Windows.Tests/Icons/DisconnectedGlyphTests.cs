using System.Drawing;
using System.Drawing.Imaging;
using FluentAssertions;
using Imrdy.Windows.Icons;
using Xunit;

namespace Imrdy.Windows.Tests.Icons;

/// <summary>
/// D20's treatment has one binding property: it must be distinguishable from the aging dim.
/// These tests assert that structurally — the treatment adds ink where the source had none
/// (the ring) and removes ink where the source had some (the shrink) — because any effect
/// that only scaled alpha or colour would be indistinguishable from an aging tier.
/// </summary>
public class DisconnectedGlyphTests
{
    private static Bitmap SolidSquare(int size, Color color)
    {
        var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.Transparent);
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, 0, 0, size, size);
        return bmp;
    }

    [Fact]
    public void Apply_PreservesFrameSizeAndFormat()
    {
        using var source = SolidSquare(16, Color.Lime);

        using var ghosted = DisconnectedGlyph.Apply(source);

        ghosted.Width.Should().Be(16);
        ghosted.Height.Should().Be(16);
        ghosted.PixelFormat.Should().Be(source.PixelFormat,
            "the tray converts the result straight to an HICON and the overlay caches it beside "
            + "premultiplied glyphs — a format change would break one of the two");
    }

    [Fact]
    public void Apply_LeavesTheSourceUntouched()
    {
        using var source = SolidSquare(16, Color.Lime);
        var before = source.GetPixel(8, 8);

        using var ghosted = DisconnectedGlyph.Apply(source);

        source.GetPixel(8, 8).Should().Be(before,
            "the renderers cache the connected glyph and pass the same instance in — mutating it "
            + "would make every connected icon disconnected too");
        ghosted.Should().NotBeSameAs(source);
    }

    [Fact]
    public void Apply_ClearsTheGlyphCentreOffTheFrameCorner()
    {
        // A full-bleed source covers the corners. After the shrink the corners must be
        // glyph-free, which is what proves the effect is a geometry change and not a fade.
        using var source = SolidSquare(32, Color.Lime);
        source.GetPixel(1, 1).A.Should().BeGreaterThan(200, "precondition: the source is full-bleed");

        using var ghosted = DisconnectedGlyph.Apply(source);

        ghosted.GetPixel(1, 1).A.Should().BeLessThan(16,
            "the frame corner sits outside both the shrunk glyph and the inscribed ring");
    }

    [Fact]
    public void Apply_DrawsARingWhereASmallGlyphHadNothing()
    {
        // A glyph occupying only the middle third leaves the frame edge empty. The ring is
        // drawn at the frame edge, so ink appearing there can only have come from the ring.
        var size = 32;
        using var source = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(source))
        {
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(Color.Lime);
            g.FillEllipse(brush, size / 3f, size / 3f, size / 3f, size / 3f);
        }

        using var ghosted = DisconnectedGlyph.Apply(source);

        OutsideGlyphInk(source).Should().Be(0,
            "precondition: the source glyph does not reach the outer band");
        OutsideGlyphInk(ghosted).Should().BeGreaterThan(0,
            "the ring is the added element that makes D20 distinguishable from a dim");
    }

    /// <summary>
    /// Total alpha in the outer band the shrunk glyph cannot reach, so every pixel counted
    /// here was painted by the ring. Summed over the whole band rather than sampled at four
    /// points, because the ring is dashed and no single pixel is guaranteed to carry a dash.
    /// </summary>
    private static long OutsideGlyphInk(Bitmap bmp)
    {
        // The glyph is redrawn into the centred 60% box; anything at or beyond 15% from an
        // edge is outside it with room to spare.
        var margin = (int)(bmp.Width * 0.15);
        long total = 0;

        for (var y = 0; y < bmp.Height; y++)
        {
            for (var x = 0; x < bmp.Width; x++)
            {
                var outside = x < margin || y < margin
                    || x >= bmp.Width - margin || y >= bmp.Height - margin;
                if (outside) total += bmp.GetPixel(x, y).A;
            }
        }

        return total;
    }
}
