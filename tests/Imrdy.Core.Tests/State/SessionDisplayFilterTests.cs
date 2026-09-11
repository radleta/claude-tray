using FluentAssertions;
using Imrdy.Core.State;

namespace Imrdy.Core.Tests.State;

public class SessionDisplayFilterTests
{
    private static StateFileModel Model(string status, TimeSpan age) => new()
    {
        SessionId = "s1",
        Status = status,
        Project = "imrdy",
        Cwd = "/home/user/imrdy",
        HookEvent = "Stop",
        Timestamp = DateTimeOffset.UtcNow - age,
    };

    [Theory]
    [InlineData("busy", true)]
    [InlineData("idle", true)]
    [InlineData("done", true)]
    [InlineData("permission", true)]
    [InlineData("attention", true)]
    [InlineData("error", true)]
    [InlineData("end", false)]
    public void WouldDisplay_ExcludesTheEndedStatusAndNothingElse(string status, bool expected)
    {
        SessionDisplayFilter.WouldDisplay(Model(status, TimeSpan.Zero)).Should().Be(expected);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(60)]
    [InlineData(60 * 24)]
    [InlineData(60 * 24 * 77)]
    [InlineData(60 * 24 * 365)]
    public void WouldDisplay_IgnoresAgeEntirely(int ageMinutes)
    {
        // The user's ruling r-5. A session that has gone quiet is exactly the state imrdy
        // exists to tell the user about, so no amount of silence removes it from the publish
        // path. A 60-minute window was built here and rejected outright; the ruling condemned
        // every age-based variant rather than one threshold, which is why this asserts across
        // a year rather than around some boundary.
        SessionDisplayFilter.WouldDisplay(Model("busy", TimeSpan.FromMinutes(ageMinutes)))
            .Should().BeTrue();
    }

    [Fact]
    public void WouldDisplay_AnEndedSessionIsExcludedAtEveryAge()
    {
        SessionDisplayFilter.WouldDisplay(Model("end", TimeSpan.Zero)).Should().BeFalse();
        SessionDisplayFilter.WouldDisplay(Model("end", TimeSpan.FromDays(365))).Should().BeFalse();
    }

    [Fact]
    public void WouldDisplay_MatchesTheTraysOwnStatusTerm()
    {
        // TrayApp.BuildDisplayItems compares Status != "end" ordinally. A case-insensitive
        // predicate here would publish a session the tray then refuses to draw, so the two
        // must agree on the comparison as well as on the string.
        SessionDisplayFilter.WouldDisplay(Model("End", TimeSpan.Zero)).Should().BeTrue();
    }
}
