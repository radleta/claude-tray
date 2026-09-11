using FluentAssertions;
using Imrdy.Core.Publishing;

namespace Imrdy.Core.Tests.Publishing;

public class PublisherHeartbeatTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void StaleAfter_ClearsOneIntervalPlusTheMeasuredMountLatency()
    {
        // The floor the threshold is derived against: a beat written one interval ago and seen
        // a quarter-second later must still read fresh, or a live publisher reports as gone.
        var floor = PublisherHeartbeat.Interval + TimeSpan.FromMilliseconds(257);

        PublisherHeartbeat.StaleAfter.Should().BeGreaterThan(floor);
    }

    [Fact]
    public void StaleAfter_ClearsTheMeasuredBuildDevRedeployGap()
    {
        // The binding input the floor above does not cover: a build-dev.sh stop-deploy-relaunch
        // on the publisher box measured a 22.04 s beat gap. A threshold under it paints the
        // disconnected glyph on every dev redeploy, which trains the operator to ignore it.
        var redeployGap = TimeSpan.FromSeconds(22.04);

        PublisherHeartbeat.StaleAfter.Should().BeGreaterThan(redeployGap);
    }

    [Fact]
    public void IsStale_BeatInsideTheThreshold_IsFresh()
    {
        var beat = Now - PublisherHeartbeat.StaleAfter + TimeSpan.FromSeconds(1);

        PublisherHeartbeat.IsStale(beat, Now).Should().BeFalse();
    }

    [Fact]
    public void IsStale_BeatPastTheThreshold_IsStale()
    {
        var beat = Now - PublisherHeartbeat.StaleAfter - TimeSpan.FromSeconds(1);

        PublisherHeartbeat.IsStale(beat, Now).Should().BeTrue();
    }

    [Fact]
    public void IsStale_BeatExactlyAtTheThreshold_IsFresh()
    {
        PublisherHeartbeat.IsStale(Now - PublisherHeartbeat.StaleAfter, Now).Should().BeFalse();
    }

    [Fact]
    public void IsStale_BeatFromAClockRunningAhead_IsFresh()
    {
        // Fail open: D20 would rather show a departed publisher a few seconds longer than
        // mark a live one disconnected because its clock is skewed forward.
        PublisherHeartbeat.IsStale(Now + TimeSpan.FromMinutes(5), Now).Should().BeFalse();
    }

    [Fact]
    public void FormatAndTryParse_RoundTrip()
    {
        var beat = new DateTimeOffset(2026, 9, 11, 12, 34, 56, 789, TimeSpan.FromHours(-5));

        PublisherHeartbeat.TryParse(PublisherHeartbeat.Format(beat), out var parsed).Should().BeTrue();
        parsed.Should().Be(beat);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("not-a-timestamp")]
    [InlineData("2026-09-11T12:00")] // a beat torn mid-write
    [InlineData(null)]
    public void TryParse_RejectsAnythingThatIsNotABeat(string? text)
    {
        PublisherHeartbeat.TryParse(text, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("pc-excalibur-Ubuntu-24.04", "pc-excalibur-ubuntu-24_04")]
    [InlineData("PC-EXCALIBUR", "pc-excalibur")]
    [InlineData("a b", "a_b")]
    public void TokenFor_SanitizesAndLowerCases(string machine, string expected)
    {
        PublisherHeartbeat.TokenFor(machine).Should().Be(expected);
    }

    [Theory]
    [InlineData("../../etc/passwd")]
    [InlineData("..")]
    [InlineData("C:\\Windows\\System32")]
    [InlineData("/etc/shadow")]
    [InlineData("a\\b")]
    public void TokenFor_LeavesNothingAPathCanEscapeOn(string hostile)
    {
        var token = PublisherHeartbeat.TokenFor(hostile);

        token.Should().MatchRegex("^[a-z0-9_-]+$");
        token.Should().NotContain("..");
        Path.GetFileName(token).Should().Be(token);
    }

    [Fact]
    public void TokenFor_BoundsTheName()
    {
        PublisherHeartbeat.TokenFor(new string('a', 500)).Should().HaveLength(64);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TokenFor_EmptyName_FallsBackRatherThanProducingAnEmptyFilename(string? machine)
    {
        PublisherHeartbeat.TokenFor(machine).Should().Be("unnamed");
    }

    [Fact]
    public void DirectoryFor_IsASiblingOfTheSessionsDirectory()
    {
        var sessions = Path.Combine(Path.GetTempPath(), "imrdy-home", "sessions");

        PublisherHeartbeat.DirectoryFor(sessions)
            .Should().Be(Path.Combine(Path.GetTempPath(), "imrdy-home", "heartbeats"));
    }

    [Fact]
    public void DirectoryFor_TrailingSeparator_ResolvesToTheSameSibling()
    {
        var sessions = Path.Combine(Path.GetTempPath(), "imrdy-home", "sessions");

        PublisherHeartbeat.DirectoryFor(sessions + Path.DirectorySeparatorChar)
            .Should().Be(PublisherHeartbeat.DirectoryFor(sessions));
    }

    [Fact]
    public void DirectoryFor_IsNeverInsideTheSessionsDirectory()
    {
        // The trap this placement exists to avoid: everything in the sessions directory is
        // read as a session, so a beat there is a phantom tray dot.
        var sessions = Path.Combine(Path.GetTempPath(), "imrdy-home", "sessions");

        PublisherHeartbeat.DirectoryFor(sessions)
            .Should().NotStartWith(Path.GetFullPath(sessions) + Path.DirectorySeparatorChar);
    }

    [Fact]
    public void PathFor_CombinesTheSiblingDirectoryWithTheToken()
    {
        var sessions = Path.Combine(Path.GetTempPath(), "imrdy-home", "sessions");

        PublisherHeartbeat.PathFor(sessions, "PC-EXCALIBUR-Ubuntu")
            .Should().Be(Path.Combine(
                Path.GetTempPath(), "imrdy-home", "heartbeats", "pc-excalibur-ubuntu.hb"));
    }

    [Fact]
    public void PathFor_HostileMachineName_StaysInsideTheHeartbeatDirectory()
    {
        var sessions = Path.Combine(Path.GetTempPath(), "imrdy-home", "sessions");
        var root = PublisherHeartbeat.DirectoryFor(sessions) + Path.DirectorySeparatorChar;

        var path = Path.GetFullPath(PublisherHeartbeat.PathFor(sessions, "../../../evil"));

        path.Should().StartWith(root);
    }

    [Fact]
    public void FileExtension_IsNotJson()
    {
        // *.json is what every session watcher and ReadAllStateFiles enumerates.
        PublisherHeartbeat.FileExtension.Should().NotBe(".json");
    }
}
