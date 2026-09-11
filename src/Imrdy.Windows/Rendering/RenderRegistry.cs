using Imrdy.Core.Rendering;

namespace Imrdy.Windows.Rendering;

/// <summary>
/// Enumerates every <see cref="IRenderableSurface"/> available to the render verb.
/// </summary>
internal static class RenderRegistry
{
    // Phase 2+: add new TrayIconRenderer(), new MenuRenderer() here.
    public static IReadOnlyList<IRenderableSurface> Components { get; } = [new DashboardRenderer(), new WorkspaceDashboardRenderer(), new OverlayRenderer(), new ConnectionsRenderer()];
}
