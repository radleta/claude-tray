using System.Drawing;

namespace Imrdy.Windows.Icons;

/// <summary>
/// Caches rendered tray icons by color + aging factor + disconnected flag to avoid GDI+ churn.
/// The icon factory delegate is supplied at construction time, decoupling the
/// cache from any specific shape renderer.
/// AgingCache owns all Icons produced by the factory — callers get borrowed references
/// and must not dispose them.
/// </summary>
internal sealed class AgingCache : IDisposable
{
    private readonly Dictionary<string, Icon> _cache = new();
    private readonly Func<byte, byte, byte, double, bool, Icon> _iconFactory;

    /// <param name="iconFactory">
    /// Called on cache miss to produce a new Icon for the given color, aging factor and
    /// disconnected flag. The returned Icon is owned by AgingCache and will be disposed
    /// on Clear/Dispose.
    /// </param>
    public AgingCache(Func<byte, byte, byte, double, bool, Icon> iconFactory)
    {
        _iconFactory = iconFactory;
    }

    /// <summary>
    /// Gets or creates a cached icon for the given status color, aging factor and
    /// disconnected flag.
    /// </summary>
    public Icon GetOrCreate(byte r, byte g, byte b, double agingFactor, bool disconnected)
    {
        var key = $"{r}-{g}-{b}-{agingFactor:F2}-{(disconnected ? 'd' : 'c')}";

        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var icon = _iconFactory(r, g, b, agingFactor, disconnected);
        _cache[key] = icon;
        return icon;
    }

    /// <summary>
    /// Clears all cached icons, disposing each one.
    /// </summary>
    public void Clear()
    {
        foreach (var icon in _cache.Values)
        {
            icon.Dispose();
        }

        _cache.Clear();
    }

    public void Dispose()
    {
        Clear();
    }
}
