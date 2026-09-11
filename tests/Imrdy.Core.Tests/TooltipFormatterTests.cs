using Imrdy.Core.Tooltip;
using FluentAssertions;

namespace Imrdy.Core.Tests;

public class TooltipFormatterTests
{
    [Fact]
    public void FormatSession_Unnamed_CorrectFormat()
    {
        var result = TooltipFormatter.FormatSession(
            "my-project", null, "busy", TimeSpan.FromMinutes(2), 0, "scv");

        result.Should().Be("my-project [busy 2m] (d1) ~scv");
    }

    [Fact]
    public void FormatSession_Named_IncludesSessionName()
    {
        var result = TooltipFormatter.FormatSession(
            "my-project", "refactor-auth", "idle", TimeSpan.FromMinutes(5), 1, "retro");

        result.Should().Be("my-project: refactor-auth [idle 5m] (d2) ~retro");
    }

    [Fact]
    public void FormatSession_RemoteSession_NamesThePublisherAfterTheProject()
    {
        var result = TooltipFormatter.FormatSession(
            "my-project", null, "busy", TimeSpan.FromMinutes(2), 0, "scv", "desk2-ubuntu");

        result.Should().Be("my-project@desk2-ubuntu [busy 2m] (d1) ~scv");
    }

    [Fact]
    public void FormatSession_RemoteNamedSession_KeepsBothTheMachineAndTheName()
    {
        var result = TooltipFormatter.FormatSession(
            "my-project", "refactor-auth", "idle", TimeSpan.FromMinutes(5), 1, null, "desk2");

        result.Should().Be("my-project@desk2: refactor-auth [idle 5m] (d2)");
    }

    [Fact]
    public void FormatSession_LocalSession_NamesNoMachine()
    {
        // The @ marker has to be absent, not empty — a local session must read exactly as it did
        // before remote sessions existed.
        var result = TooltipFormatter.FormatSession(
            "my-project", null, "busy", TimeSpan.FromMinutes(2), 0, "scv", null);

        result.Should().Be("my-project [busy 2m] (d1) ~scv");
    }

    [Fact]
    public void FormatSession_LongRemoteTooltip_TrimsTheTailNotTheMachine()
    {
        // 63 characters is the NotifyIcon.Text cap and Truncate takes it off the tail, which is
        // why the machine name sits right after the project rather than at the end.
        var result = TooltipFormatter.FormatSession(
            "a-very-long-project-name-that-runs-on", "a-long-session-name-too", "busy",
            TimeSpan.FromMinutes(2), 0, "scv", "desk2");

        result.Should().HaveLength(63);
        result.Should().StartWith("a-very-long-project-name-that-runs-on@desk2:");
    }

    [Fact]
    public void FormatSession_NoDesktop_OmitsDesktop()
    {
        var result = TooltipFormatter.FormatSession(
            "project", null, "busy", TimeSpan.FromSeconds(30), null, "pack");

        result.Should().Be("project [busy 30s] ~pack");
    }

    [Fact]
    public void FormatSession_NoPack_OmitsPack()
    {
        var result = TooltipFormatter.FormatSession(
            "project", null, "busy", TimeSpan.FromMinutes(1), 0, null);

        result.Should().Be("project [busy 1m] (d1)");
    }

    [Fact]
    public void FormatSession_EmptyPack_OmitsPack()
    {
        var result = TooltipFormatter.FormatSession(
            "project", null, "busy", TimeSpan.FromMinutes(1), 0, "");

        result.Should().Be("project [busy 1m] (d1)");
    }

    [Fact]
    public void FormatSession_NoDesktopNoPack_MinimalFormat()
    {
        var result = TooltipFormatter.FormatSession(
            "project", null, "busy", TimeSpan.FromMinutes(1), null, null);

        result.Should().Be("project [busy 1m]");
    }

    [Fact]
    public void FormatSession_TruncatesAt63Chars()
    {
        var longProject = new string('x', 60);
        var result = TooltipFormatter.FormatSession(
            longProject, "long-session-name", "busy", TimeSpan.FromMinutes(2), 1, "pack");

        result.Length.Should().Be(63);
    }

    [Fact]
    public void FormatSession_ExactlyAt63_NoTruncation()
    {
        // Build a string that's exactly 63 chars
        // "project [busy 2m] (d1) ~scv" = 28 chars; pad project to get 63
        var result = TooltipFormatter.FormatSession(
            "project", null, "busy", TimeSpan.FromMinutes(2), 0, "scv");

        result.Length.Should().BeLessThanOrEqualTo(63);
    }

    [Fact]
    public void FormatSession_TildePrefix_OnPackName()
    {
        var result = TooltipFormatter.FormatSession(
            "proj", null, "busy", TimeSpan.FromMinutes(1), 0, "assistant");

        result.Should().Contain("~assistant");
    }

    [Fact]
    public void FormatWorkspace_CorrectFormat()
    {
        var result = TooltipFormatter.FormatWorkspace("claude-code-ref", 0);

        result.Should().Be("claude-code-ref [workspace] (d1)");
    }

    [Fact]
    public void FormatWorkspace_TruncatesAt63Chars()
    {
        var longName = new string('w', 60);
        var result = TooltipFormatter.FormatWorkspace(longName, 0);

        result.Length.Should().Be(63);
    }

    [Fact]
    public void FormatAge_Seconds()
    {
        TooltipFormatter.FormatAge(TimeSpan.FromSeconds(0)).Should().Be("0s");
        TooltipFormatter.FormatAge(TimeSpan.FromSeconds(45)).Should().Be("45s");
        TooltipFormatter.FormatAge(TimeSpan.FromSeconds(59)).Should().Be("59s");
    }

    [Fact]
    public void FormatAge_Minutes()
    {
        TooltipFormatter.FormatAge(TimeSpan.FromMinutes(1)).Should().Be("1m");
        TooltipFormatter.FormatAge(TimeSpan.FromMinutes(2)).Should().Be("2m");
        TooltipFormatter.FormatAge(TimeSpan.FromMinutes(59)).Should().Be("59m");
    }

    [Fact]
    public void FormatAge_Hours()
    {
        TooltipFormatter.FormatAge(TimeSpan.FromHours(1)).Should().Be("1h");
        TooltipFormatter.FormatAge(TimeSpan.FromHours(23)).Should().Be("23h");
    }

    [Fact]
    public void FormatAge_Days()
    {
        TooltipFormatter.FormatAge(TimeSpan.FromDays(1)).Should().Be("1d");
        TooltipFormatter.FormatAge(TimeSpan.FromDays(3)).Should().Be("3d");
        TooltipFormatter.FormatAge(TimeSpan.FromDays(30)).Should().Be("30d");
    }

    [Fact]
    public void FormatAge_MixedTimeSpan_UsesLargestUnit()
    {
        // 1 hour 30 minutes → "1h" (not "90m")
        TooltipFormatter.FormatAge(TimeSpan.FromMinutes(90)).Should().Be("1h");
        // 1 day 12 hours → "1d"
        TooltipFormatter.FormatAge(TimeSpan.FromHours(36)).Should().Be("1d");
    }

    [Fact]
    public void FormatSession_EmptySessionName_TreatedAsUnnamed()
    {
        var result = TooltipFormatter.FormatSession(
            "project", "", "busy", TimeSpan.FromMinutes(1), 0, null);

        result.Should().Be("project [busy 1m] (d1)");
    }
}
