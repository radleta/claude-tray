using System.Drawing;

namespace Imrdy.Windows.Icons;

/// <summary>
/// Renders tray icons for a given status and aging tier.
/// Implementations cache the returned Icon instances and own their lifetime;
/// callers must NOT dispose the returned Icon.
/// </summary>
internal interface ITrayIconRenderer : IDisposable
{
    /// <summary>
    /// Returns an Icon for the given status and aging tier.
    /// Icon is cached — subsequent calls with the same key return the same instance.
    /// </summary>
    /// <param name="status">Status name (e.g., "busy", "idle", "attention"). Unknown statuses should return a fallback icon, not throw.</param>
    /// <param name="ageTier">Aging tier 0-4 from StatusMap.GetAgingTier. 0 = fresh, 4 = oldest.</param>
    /// <param name="disconnected">
    /// D20: the session's publisher has no live link. Rendered as a separate visual
    /// dimension (see <see cref="DisconnectedGlyph"/>), never as more aging — the two
    /// must stay tellable apart, so this is a third cache key and not a sixth tier.
    /// </param>
    Icon GetIcon(string status, int ageTier, bool disconnected);
}
