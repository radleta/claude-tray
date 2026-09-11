using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Imrdy.Core.Graphics;
using Microsoft.Extensions.Logging;
using Svg;

namespace Imrdy.Windows.Icons;

/// <summary>
/// Renders tray icons from an SVG-based graphics pack.
/// Pre-renders all (state, tier, disconnected) combinations eagerly in the constructor.
/// Falls back gracefully on any render failure — caller should check IsHealthy
/// and substitute a ParametricShapeRenderer if false (handled by TrayIconRendererFactory).
/// </summary>
internal sealed class PackIconRenderer : ITrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    private readonly GraphicsPackLoader.LoadedGraphicsPack _pack;
    private readonly ILogger<PackIconRenderer> _logger;
    private readonly Dictionary<(string status, int tier, bool disconnected), Icon> _cache = new();

    /// <summary>
    /// True if all (state, tier, disconnected) combinations rendered successfully during construction.
    /// False means the cache is empty and GetIcon will return a safety-net transparent icon.
    /// TrayIconRendererFactory reads this to decide whether to swap to ParametricShapeRenderer.
    /// </summary>
    internal bool IsHealthy { get; private set; }

    public PackIconRenderer(GraphicsPackLoader.LoadedGraphicsPack pack, ILogger<PackIconRenderer> logger)
    {
        _pack = pack;
        _logger = logger;

        try
        {
            PreRenderAll();
            IsHealthy = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "PackIconRenderer: failed to pre-render pack '{Pack}'. Renderer is unhealthy.", pack.Name);
            IsHealthy = false;
        }
    }

    /// <inheritdoc/>
    public Icon GetIcon(string status, int ageTier, bool disconnected)
    {
        if (!IsHealthy)
        {
            return CreateTransparentIcon();
        }

        if (_cache.TryGetValue((status, ageTier, disconnected), out var icon))
        {
            return icon;
        }

        // Unknown status → try "unknown" fallback, then "idle", then any cached icon.
        // The disconnected flag is held across every fallback: a substituted glyph must
        // still carry D20's treatment, or a disconnected session with an unknown status
        // would silently render as connected.
        if (_cache.TryGetValue(("unknown", ageTier, disconnected), out var unknownIcon))
        {
            return unknownIcon;
        }

        if (_cache.TryGetValue(("idle", ageTier, disconnected), out var idleIcon))
        {
            return idleIcon;
        }

        // Last resort: return the first cached icon for any status at the requested tier,
        // or any icon in the cache at all
        foreach (var tier in new[] { ageTier, 0 })
        {
            foreach (var key in _cache.Keys)
            {
                if (key.tier == tier && key.disconnected == disconnected)
                {
                    return _cache[key];
                }
            }
        }

        return CreateTransparentIcon();
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        foreach (var icon in _cache.Values)
        {
            icon.Dispose();
        }
        _cache.Clear();
    }

    private void PreRenderAll()
    {
        var size = SystemInformation.SmallIconSize;

        foreach (var (state, filePath) in _pack.StateFilePaths)
        {
            using var baseBitmap = RenderSvgToBitmap(filePath, size.Width, size.Height);

            for (var tier = 0; tier <= 4; tier++)
            {
                Bitmap bitmapForTier;
                if (tier == 0)
                {
                    bitmapForTier = (Bitmap)baseBitmap.Clone();
                }
                else
                {
                    bitmapForTier = ApplyAgingColorMatrix(baseBitmap, size.Width, size.Height, tier);
                }

                try
                {
                    _cache[(state, tier, false)] = BitmapToIcon(bitmapForTier);

                    using var ghosted = DisconnectedGlyph.Apply(bitmapForTier);
                    _cache[(state, tier, true)] = BitmapToIcon(ghosted);
                }
                finally
                {
                    bitmapForTier.Dispose();
                }
            }
        }
    }

    private static Bitmap RenderSvgToBitmap(string svgPath, int width, int height)
    {
        var doc = SvgDocument.Open(svgPath);
        return doc.Draw(width, height);
    }

    /// <summary>
    /// Applies aging desaturation + dim toward gray via ColorMatrix.
    /// Tier 1-4: scales RGB toward (0.5, 0.5, 0.5) proportionally and dims alpha.
    /// </summary>
    private static Bitmap ApplyAgingColorMatrix(Bitmap source, int width, int height, int tier)
    {
        // agingScale: how much of the original color to keep (vs. blend to 0.5 gray)
        // tier 1 → 0.75, tier 2 → 0.5, tier 3 → 0.25, tier 4 → 0.0 (fully gray)
        var agingScale = 1.0f - (tier / 4.0f);
        var grayOffset = (1.0f - agingScale) * 0.5f;

        // Alpha dim: tier 1 → 0.9, tier 2 → 0.8, tier 3 → 0.7, tier 4 → 0.6
        var alphaMul = 1.0f - (tier * 0.1f);

        // ColorMatrix layout (5x5):
        //   [ sr  0   0   0   0  ]   scale R
        //   [ 0   sg  0   0   0  ]   scale G
        //   [ 0   0   sb  0   0  ]   scale B
        //   [ 0   0   0   sa  0  ]   scale A
        //   [ or  og  ob  0   1  ]   add offset (maps to 0..1 range)
        var matrix = new ColorMatrix(new float[][]
        {
            [agingScale, 0, 0, 0, 0],
            [0, agingScale, 0, 0, 0],
            [0, 0, agingScale, 0, 0],
            [0, 0, 0, alphaMul, 0],
            [grayOffset, grayOffset, grayOffset, 0, 1],
        });

        using var attrs = new ImageAttributes();
        attrs.SetColorMatrix(matrix);

        var aged = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(aged);
        g.DrawImage(source, new Rectangle(0, 0, width, height), 0, 0, width, height, GraphicsUnit.Pixel, attrs);
        return aged;
    }

    private static Icon BitmapToIcon(Bitmap bitmap)
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

    private static Icon CreateTransparentIcon()
    {
        using var bitmap = new Bitmap(16, 16, PixelFormat.Format32bppArgb);
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
