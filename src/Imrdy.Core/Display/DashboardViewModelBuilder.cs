using Imrdy.Core.State;

namespace Imrdy.Core.Display;

/// <summary>
/// Builds a <see cref="DashboardViewModel"/> from Core-only inputs.
/// Windows-layer callers (HoverDashboardController) decompose SessionEntry and pass the
/// individual fields here — Core does NOT project-reference Imrdy.Windows.
/// </summary>
public static class DashboardViewModelBuilder
{
    public static DashboardViewModel Build(
        StateFileModel state,
        DateTimeOffset startedAt,
        string? soundPack,
        int desktopIndex,
        HookAccumulation accumulation,
        GitInfo? git,
        IReadOnlyList<FleetItem> fleet,
        DateTimeOffset now,
        string? wslDistro = null,
        string? originMachine = null)
    {
        _ = now; // reserved for future age-based field derivations; injected for testability

        return new DashboardViewModel(
            SessionId: state.SessionId,
            SessionName: state.SessionName ?? "",
            Project: state.Project,
            CwdPath: state.Cwd,
            DesktopIndex: desktopIndex,
            SoundPack: soundPack,
            WslDistro: wslDistro,
            OriginMachine: originMachine,
            Status: state.Status,
            LastHookAt: state.Timestamp,
            StartedAt: startedAt,
            TurnCount: accumulation.TurnCount,
            FailureCount: accumulation.FailureCount,
            SubagentCount: accumulation.ActiveAgentIds.Count,
            CurrentTool: accumulation.CurrentTool,
            RecentTools: accumulation.RecentTools,
            ActivityTimestamps: accumulation.ActivityTimestamps,
            LastPrompt: string.IsNullOrEmpty(state.LastMessage) ? null : state.LastMessage,
            PermissionTool: accumulation.PermissionTool,
            Git: git,
            FleetItems: fleet,
            ContextTokens: null,
            ContextWindowSize: null,
            CostUsd: null,
            ModelDisplayName: null,
            RateLimits: null);
    }
}
