using Imrdy.Core.Display;
using Imrdy.Core.Hooks;
using Imrdy.Windows.Models;

namespace Imrdy.Windows.Dashboard;

/// <summary>
/// Shared helper that builds a <see cref="DashboardViewModel"/> from a live <see cref="SessionEntry"/>.
/// Extracted from <see cref="HoverDashboardController"/> to serve as the single source of truth
/// used by the hover controller (two call sites) and the <c>inspect-live</c> IPC handler (D5).
/// </summary>
internal static class LiveDashboardVmBuilder
{
    /// <summary>
    /// Builds a <see cref="DashboardViewModel"/> for the given session entry.
    /// </summary>
    /// <param name="cachedGit">
    /// Caller-resolved git info (may be <c>null</c> if not yet cached). The caller owns the
    /// single <c>TryGetCached</c> call so the cache is not read twice (DRY — D5).
    /// </param>
    public static DashboardViewModel BuildForSession(
        SessionEntry entry,
        HookAccumulationStore store,
        GitInfo? cachedGit,
        IReadOnlyList<SessionEntry> allSessions,
        DateTimeOffset now)
    {
        var snap = store.GetSnapshot(entry.SessionId);
        var fleet = ProjectFleetItems(allSessions, entry.SessionId);

        return DashboardViewModelBuilder.Build(
            state: entry.State,
            startedAt: entry.StartedAt,
            soundPack: entry.SoundPack,
            desktopIndex: entry.DesktopIndex ?? 0,
            accumulation: snap,
            git: cachedGit,
            fleet: fleet,
            now: now,
            wslDistro: entry.State.WslDistro,
            originMachine: entry.State.OriginMachine);
    }

    /// <summary>
    /// Projects session entries into fleet items for the dashboard fleet strip.
    /// Sets <c>IsHovered</c> only for the targeted session.
    /// </summary>
    internal static IReadOnlyList<FleetItem> ProjectFleetItems(IReadOnlyList<SessionEntry> sessions, string hoveredSessionId)
    {
        var fleet = new List<FleetItem>(sessions.Count);
        foreach (var s in sessions)
        {
            fleet.Add(new FleetItem(
                SessionId: s.SessionId,
                SessionName: s.State.SessionName ?? "",
                Status: s.State.Status,
                IsHovered: s.SessionId == hoveredSessionId));
        }
        return fleet;
    }
}
