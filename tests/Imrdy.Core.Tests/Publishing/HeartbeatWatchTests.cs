using FluentAssertions;
using Imrdy.Core.Publishing;

namespace Imrdy.Core.Tests.Publishing;

public class HeartbeatWatchTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root;
    private readonly string _sessions;
    private readonly string _heartbeats;

    public HeartbeatWatchTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "imrdy-heartbeat-watch", Guid.NewGuid().ToString());
        _sessions = Path.Combine(_root, "sessions");
        _heartbeats = PublisherHeartbeat.DirectoryFor(_sessions);
        Directory.CreateDirectory(_sessions);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private HeartbeatWatch Watch()
    {
        var watch = new HeartbeatWatch(_heartbeats);
        watch.Refresh();
        return watch;
    }

    private void Beat(string machine, DateTimeOffset at)
    {
        Directory.CreateDirectory(_heartbeats);
        File.WriteAllText(PublisherHeartbeat.PathFor(_sessions, machine), PublisherHeartbeat.Format(at));
    }

    [Fact]
    public void IsDisconnected_FreshBeat_IsConnected()
    {
        Beat("pc-Ubuntu", Now - TimeSpan.FromSeconds(1));

        Watch().IsDisconnected("pc-Ubuntu", Now).Should().BeFalse();
    }

    [Fact]
    public void IsDisconnected_StaleBeat_IsDisconnected()
    {
        // Outcome 5: the distro stopped, so the beat stopped, and the sessions it left on disk
        // now carry the disconnected treatment.
        Beat("pc-Ubuntu", Now - PublisherHeartbeat.StaleAfter - TimeSpan.FromSeconds(1));

        Watch().IsDisconnected("pc-Ubuntu", Now).Should().BeTrue();
    }

    [Fact]
    public void IsDisconnected_MachineThatNeverBeat_IsConnected()
    {
        // Absence is not disconnection: a publisher that does not beat behaves exactly as it
        // did before heartbeats existed, rather than showing a permanent false alarm.
        Beat("pc-Ubuntu", Now);

        Watch().IsDisconnected("some-other-machine", Now).Should().BeFalse();
    }

    [Fact]
    public void IsDisconnected_NoHeartbeatDirectoryAtAll_IsConnected()
    {
        Directory.Exists(_heartbeats).Should().BeFalse();

        Watch().IsDisconnected("pc-Ubuntu", Now).Should().BeFalse();
    }

    [Fact]
    public void IsDisconnected_BeforeAnyRefresh_IsConnected()
    {
        Beat("pc-Ubuntu", Now - TimeSpan.FromHours(1));

        new HeartbeatWatch(_heartbeats).IsDisconnected("pc-Ubuntu", Now).Should().BeFalse();
    }

    [Fact]
    public void IsDisconnected_MatchesTheMachineNameCaseInsensitively()
    {
        // Links are matched case-insensitively everywhere else; the token carries that here.
        Beat("PC-Ubuntu", Now - PublisherHeartbeat.StaleAfter - TimeSpan.FromSeconds(1));

        Watch().IsDisconnected("pc-UBUNTU", Now).Should().BeTrue();
    }

    [Fact]
    public void IsDisconnected_UnparseableBeat_IsConnected()
    {
        Directory.CreateDirectory(_heartbeats);
        File.WriteAllText(Path.Combine(_heartbeats, "pc-ubuntu.hb"), "half-writ");

        Watch().IsDisconnected("pc-Ubuntu", Now).Should().BeFalse();
    }

    [Fact]
    public void Refresh_PicksUpABeatWrittenSinceTheLastOne()
    {
        Beat("pc-Ubuntu", Now - PublisherHeartbeat.StaleAfter - TimeSpan.FromSeconds(1));
        var watch = Watch();
        watch.IsDisconnected("pc-Ubuntu", Now).Should().BeTrue();

        // The distro came back and beat again — no receiver restart.
        Beat("pc-Ubuntu", Now);
        watch.Refresh();

        watch.IsDisconnected("pc-Ubuntu", Now).Should().BeFalse();
    }

    [Fact]
    public void Refresh_IgnoresFilesThatAreNotBeats()
    {
        Directory.CreateDirectory(_heartbeats);
        File.WriteAllText(Path.Combine(_heartbeats, "notes.txt"), PublisherHeartbeat.Format(Now));

        var act = () => Watch();

        act.Should().NotThrow();
    }

    [Fact]
    public void Refresh_TwoPublishers_AreTrackedIndependently()
    {
        Beat("pc-Ubuntu", Now);
        Beat("pc-Debian", Now - PublisherHeartbeat.StaleAfter - TimeSpan.FromSeconds(1));
        var watch = Watch();

        watch.IsDisconnected("pc-Ubuntu", Now).Should().BeFalse();
        watch.IsDisconnected("pc-Debian", Now).Should().BeTrue();
    }
}
