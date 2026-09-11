using FluentAssertions;
using Imrdy.Core.Display;
using Imrdy.Core.State;

namespace Imrdy.Core.Tests.Display;

public class DashboardViewModelBuilderTests
{
    private static readonly DateTimeOffset BaseTime = new(2026, 4, 24, 12, 0, 0, TimeSpan.Zero);

    private static StateFileModel CreateState(
        string sessionId = "s1",
        string sessionName = "feature-x",
        string project = "imrdy",
        string cwd = @"D:\dev\imrdy",
        string status = "busy",
        string lastMessage = "do the thing") => new()
    {
        SessionId = sessionId,
        Status = status,
        Project = project,
        Cwd = cwd,
        HookEvent = "PreToolUse",
        Timestamp = BaseTime,
        SessionName = sessionName,
        LastMessage = lastMessage,
    };

    private static HookAccumulation CreateAccumulation(
        int turnCount = 3,
        int failureCount = 1,
        string? currentTool = "Bash",
        string? permissionTool = null,
        IReadOnlyList<RecentToolEntry>? recentTools = null,
        IReadOnlyList<DateTimeOffset>? activity = null,
        IReadOnlySet<string>? agents = null) => new(
            TurnCount: turnCount,
            FailureCount: failureCount,
            RecentTools: recentTools ?? Array.Empty<RecentToolEntry>(),
            ActivityTimestamps: activity ?? Array.Empty<DateTimeOffset>(),
            ActiveAgentIds: agents ?? new HashSet<string>(),
            CurrentTool: currentTool,
            PermissionTool: permissionTool);

    [Fact]
    public void Build_PopulatesIdentityFromState()
    {
        var vm = DashboardViewModelBuilder.Build(
            CreateState(),
            startedAt: BaseTime.AddMinutes(-5),
            soundPack: "jazz",
            desktopIndex: 2,
            accumulation: CreateAccumulation(),
            git: null,
            fleet: Array.Empty<FleetItem>(),
            now: BaseTime);

        vm.SessionId.Should().Be("s1");
        vm.SessionName.Should().Be("feature-x");
        vm.Project.Should().Be("imrdy");
        vm.CwdPath.Should().Be(@"D:\dev\imrdy");
        vm.DesktopIndex.Should().Be(2);
        vm.SoundPack.Should().Be("jazz");
    }

    [Fact]
    public void Build_CarriesOriginMachineForRendering_AndLeavesWslDistroUnrendered()
    {
        // D23: the dashboard renders the machine name; wsl_distro stays on the view model and
        // unrendered, so a WSL publisher gets one label, not two.
        var vm = DashboardViewModelBuilder.Build(
            CreateState(),
            startedAt: BaseTime,
            soundPack: null,
            desktopIndex: 0,
            accumulation: CreateAccumulation(),
            git: null,
            fleet: Array.Empty<FleetItem>(),
            now: BaseTime,
            wslDistro: "Ubuntu",
            originMachine: "desk2-Ubuntu");

        vm.OriginMachine.Should().Be("desk2-Ubuntu");
        vm.WslDistro.Should().Be("Ubuntu");
    }

    [Fact]
    public void Build_LocalSession_HasNoOriginMachine()
    {
        var vm = DashboardViewModelBuilder.Build(
            CreateState(), BaseTime, null, 0, CreateAccumulation(), null, Array.Empty<FleetItem>(), BaseTime);

        vm.OriginMachine.Should().BeNull();
    }

    [Fact]
    public void Build_NullSessionName_BecomesEmptyString()
    {
        var state = CreateState() with { SessionName = null };

        var vm = DashboardViewModelBuilder.Build(
            state, BaseTime, null, 0, CreateAccumulation(), null, Array.Empty<FleetItem>(), BaseTime);

        vm.SessionName.Should().Be("");
    }

    [Fact]
    public void Build_LastHookAt_SourcedFromStateTimestamp()
    {
        var ts = new DateTimeOffset(2026, 4, 24, 11, 59, 30, TimeSpan.Zero);
        var state = CreateState() with { Timestamp = ts };

        var vm = DashboardViewModelBuilder.Build(
            state, BaseTime, null, 0, CreateAccumulation(), null, Array.Empty<FleetItem>(), BaseTime);

        vm.LastHookAt.Should().Be(ts);
    }

    [Fact]
    public void Build_StartedAt_IsPassedThrough()
    {
        var startedAt = BaseTime.AddMinutes(-15);

        var vm = DashboardViewModelBuilder.Build(
            CreateState(), startedAt, null, 0, CreateAccumulation(), null, Array.Empty<FleetItem>(), BaseTime);

        vm.StartedAt.Should().Be(startedAt);
    }

    [Fact]
    public void Build_AccumulatorFields_ProjectFromAccumulation()
    {
        var acc = CreateAccumulation(
            turnCount: 7,
            failureCount: 2,
            currentTool: "Edit",
            agents: new HashSet<string> { "a1", "a2", "a3" },
            recentTools: new[] { new RecentToolEntry("Bash", BaseTime), new RecentToolEntry("Read", BaseTime) });

        var vm = DashboardViewModelBuilder.Build(
            CreateState(), BaseTime, null, 0, acc, null, Array.Empty<FleetItem>(), BaseTime);

        vm.TurnCount.Should().Be(7);
        vm.FailureCount.Should().Be(2);
        vm.SubagentCount.Should().Be(3);
        vm.CurrentTool.Should().Be("Edit");
        vm.RecentTools.Should().HaveCount(2);
    }

    [Fact]
    public void Build_LastPrompt_NullWhenStateLastMessageEmpty()
    {
        var state = CreateState() with { LastMessage = "" };

        var vm = DashboardViewModelBuilder.Build(
            state, BaseTime, null, 0, CreateAccumulation(), null, Array.Empty<FleetItem>(), BaseTime);

        vm.LastPrompt.Should().BeNull();
    }

    [Fact]
    public void Build_LastPrompt_CarriesAlreadyTruncatedValue()
    {
        // state.LastMessage is truncated upstream by HookCommand using TruncateMessage.
        var truncated = StateFileModel.TruncateMessage(new string('x', 500));
        var state = CreateState() with { LastMessage = truncated };

        var vm = DashboardViewModelBuilder.Build(
            state, BaseTime, null, 0, CreateAccumulation(), null, Array.Empty<FleetItem>(), BaseTime);

        vm.LastPrompt.Should().NotBeNull();
        vm.LastPrompt!.Length.Should().Be(StateFileModel.MaxMessageLength);
    }

    [Fact]
    public void Build_GitNullable_PassedThrough()
    {
        var git = new GitInfo("develop", 3);

        var vm = DashboardViewModelBuilder.Build(
            CreateState(), BaseTime, null, 0, CreateAccumulation(), git, Array.Empty<FleetItem>(), BaseTime);

        vm.Git.Should().Be(git);
    }

    [Fact]
    public void Build_NullGit_StaysNull()
    {
        var vm = DashboardViewModelBuilder.Build(
            CreateState(), BaseTime, null, 0, CreateAccumulation(), null, Array.Empty<FleetItem>(), BaseTime);

        vm.Git.Should().BeNull();
    }

    [Fact]
    public void Build_FleetItems_PassedThrough()
    {
        var fleet = new[]
        {
            new FleetItem("s1", "feature-x", "busy", IsHovered: true),
            new FleetItem("s2", "bugfix-y", "idle", IsHovered: false),
        };

        var vm = DashboardViewModelBuilder.Build(
            CreateState(), BaseTime, null, 0, CreateAccumulation(), null, fleet, BaseTime);

        vm.FleetItems.Should().HaveCount(2);
        vm.FleetItems[0].SessionId.Should().Be("s1");
        vm.FleetItems[0].IsHovered.Should().BeTrue();
        vm.FleetItems[1].IsHovered.Should().BeFalse();
    }

    [Fact]
    public void Build_Phase2Slots_AllNullInPhase1()
    {
        var vm = DashboardViewModelBuilder.Build(
            CreateState(), BaseTime, null, 0, CreateAccumulation(), null, Array.Empty<FleetItem>(), BaseTime);

        vm.ContextTokens.Should().BeNull();
        vm.ContextWindowSize.Should().BeNull();
        vm.CostUsd.Should().BeNull();
        vm.ModelDisplayName.Should().BeNull();
        vm.RateLimits.Should().BeNull();
    }

    [Fact]
    public void Build_PermissionTool_NullByDefault()
    {
        var vm = DashboardViewModelBuilder.Build(
            CreateState(), BaseTime, null, 0, CreateAccumulation(), null, Array.Empty<FleetItem>(), BaseTime);

        vm.PermissionTool.Should().BeNull();
    }

    [Fact]
    public void Build_PermissionTool_PopulatedFromAccumulation()
    {
        var acc = CreateAccumulation(currentTool: null, permissionTool: "Bash");

        var vm = DashboardViewModelBuilder.Build(
            CreateState(status: "permission"), BaseTime, null, 0, acc, null, Array.Empty<FleetItem>(), BaseTime);

        vm.PermissionTool.Should().Be("Bash");
    }
}
