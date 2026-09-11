using System.Drawing;
using FluentAssertions;
using Imrdy.Windows.Icons;
using Xunit;

namespace Imrdy.Windows.Tests.Icons;

/// <summary>
/// D20 requires the disconnected treatment to be visually distinct from the aging dim.
/// The binding assertion is therefore not "disconnected differs from connected" — that a
/// deeper dim would also satisfy — but "disconnected differs from <em>every</em> aging tier",
/// which only a treatment outside the dim ladder can pass.
/// </summary>
public class ParametricShapeRendererDisconnectedTests
{
    private static ParametricShapeRenderer NewRenderer() =>
        new(ShapeDefinitions.Circle, "circles");

    [Fact]
    public void GetIcon_Disconnected_DiffersFromEveryConnectedAgingTier()
    {
        using var renderer = NewRenderer();

        var disconnected = Pixels(renderer.GetIcon("idle", 0, disconnected: true));

        for (var tier = 0; tier <= 4; tier++)
        {
            var connected = Pixels(renderer.GetIcon("idle", tier, disconnected: false));
            disconnected.Should().NotEqual(connected,
                $"D20's treatment must not be mistakable for aging tier {tier}");
        }
    }

    [Fact]
    public void GetIcon_DisconnectedFlag_IsPartOfTheCacheKey()
    {
        using var renderer = NewRenderer();

        var connected = renderer.GetIcon("busy", 2, disconnected: false);
        var disconnected = renderer.GetIcon("busy", 2, disconnected: true);

        disconnected.Should().NotBeSameAs(connected,
            "a shared cache entry would render a dropped link as a live one");
        renderer.GetIcon("busy", 2, disconnected: true).Should().BeSameAs(disconnected,
            "the flag widens the key; it must not defeat the cache");
    }

    [Fact]
    public void GetIcon_Connected_IsUnchangedByTheWiderSignature()
    {
        using var renderer = NewRenderer();

        var first = renderer.GetIcon("idle", 0, disconnected: false);

        renderer.GetIcon("idle", 0, disconnected: false).Should().BeSameAs(first,
            "the common local-session path must still hit the cache on every drain tick");
    }

    private static byte[] Pixels(Icon icon)
    {
        using var bitmap = icon.ToBitmap();
        var bytes = new byte[bitmap.Width * bitmap.Height * 4];
        var i = 0;

        for (var y = 0; y < bitmap.Height; y++)
        {
            for (var x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                bytes[i++] = c.A;
                bytes[i++] = c.R;
                bytes[i++] = c.G;
                bytes[i++] = c.B;
            }
        }

        return bytes;
    }
}
