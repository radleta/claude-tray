using FluentAssertions;
using Imrdy.Core.Publishing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Imrdy.Core.Tests.Publishing;

public class HeartbeatWriterTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 9, 11, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root;
    private readonly string _targetSessions;
    private readonly List<PublisherEntry> _entries = [];
    private string _machine = "pc-Ubuntu";

    public HeartbeatWriterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "imrdy-heartbeat-writer", Guid.NewGuid().ToString());
        _targetSessions = Path.Combine(_root, "receiver", "sessions");
        Directory.CreateDirectory(_targetSessions);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private HeartbeatWriter Writer() =>
        new(() => _entries, () => _machine, NullLogger.Instance);

    private string BeatPath(string machine) =>
        PublisherHeartbeat.PathFor(_targetSessions, machine);

    private void RegisterFileLink(string name = "receiver", bool enabled = true) =>
        _entries.Add(new PublisherEntry { Name = name, Endpoint = _targetSessions, Enabled = enabled });

    [Fact]
    public void WriteIfDue_FileLink_WritesABeatThatParsesBack()
    {
        RegisterFileLink();

        Writer().WriteIfDue(Now).Should().BeTrue();

        PublisherHeartbeat.TryParse(File.ReadAllText(BeatPath(_machine)), out var beat).Should().BeTrue();
        beat.Should().Be(Now);
    }

    [Fact]
    public void WriteIfDue_CreatesTheHeartbeatDirectoryBesideSessionsRatherThanInsideIt()
    {
        RegisterFileLink();

        Writer().WriteIfDue(Now);

        // A beat inside the sessions directory would be enumerated as a session.
        Directory.GetFiles(_targetSessions).Should().BeEmpty();
        Directory.Exists(PublisherHeartbeat.DirectoryFor(_targetSessions)).Should().BeTrue();
    }

    [Fact]
    public void WriteIfDue_BeforeTheIntervalElapses_DoesNotWriteAgain()
    {
        RegisterFileLink();
        var writer = Writer();
        writer.WriteIfDue(Now);
        var first = File.ReadAllText(BeatPath(_machine));

        writer.WriteIfDue(Now + PublisherHeartbeat.Interval - TimeSpan.FromMilliseconds(1))
            .Should().BeFalse();

        File.ReadAllText(BeatPath(_machine)).Should().Be(first);
    }

    [Fact]
    public void WriteIfDue_OnceTheIntervalElapses_WritesAFreshBeat()
    {
        RegisterFileLink();
        var writer = Writer();
        writer.WriteIfDue(Now);

        var later = Now + PublisherHeartbeat.Interval;
        writer.WriteIfDue(later).Should().BeTrue();

        PublisherHeartbeat.TryParse(File.ReadAllText(BeatPath(_machine)), out var beat);
        beat.Should().Be(later);
    }

    [Fact]
    public void WriteIfDue_NoSessionsInvolved_StillBeats()
    {
        // The property that separates this from inference-from-silence: nothing published,
        // nothing changed, and the publisher still reports itself alive.
        RegisterFileLink();

        Writer().WriteIfDue(Now);

        File.Exists(BeatPath(_machine)).Should().BeTrue();
    }

    [Fact]
    public void WriteIfDue_TcpLink_WritesNothing()
    {
        // A socket is its own liveness signal; a beat on that path would be a second one.
        _entries.Add(new PublisherEntry { Name = "desk2", Endpoint = "100.64.0.2:47600" });

        Writer().WriteIfDue(Now);

        Directory.Exists(PublisherHeartbeat.DirectoryFor(_targetSessions)).Should().BeFalse();
    }

    [Fact]
    public void WriteIfDue_DisabledLink_WritesNothing()
    {
        RegisterFileLink(enabled: false);

        Writer().WriteIfDue(Now);

        File.Exists(BeatPath(_machine)).Should().BeFalse();
    }

    [Fact]
    public void WriteIfDue_ReceiveOnlyRecord_WritesNothing()
    {
        // r-1: a null endpoint is a registration with nothing to dial and nothing to write to.
        _entries.Add(new PublisherEntry { Name = "laptop", Endpoint = null });

        Writer().WriteIfDue(Now);

        Directory.Exists(PublisherHeartbeat.DirectoryFor(_targetSessions)).Should().BeFalse();
    }

    [Fact]
    public void WriteIfDue_LinkAddedAfterConstruction_StartsBeatingWithNoRestart()
    {
        var writer = Writer();
        writer.WriteIfDue(Now);

        RegisterFileLink();
        writer.WriteIfDue(Now + PublisherHeartbeat.Interval);

        File.Exists(BeatPath(_machine)).Should().BeTrue();
    }

    [Fact]
    public void WriteIfDue_ResolvesTheMachineNamePerBeatRatherThanCapturingIt()
    {
        // So the beat's name and the origin_machine the sinks stamp always come from one
        // source. The daemon passes a value fixed at startup, so this is not a live rename;
        // it is what stops the two halves diverging if that ever changes.
        RegisterFileLink();
        var writer = Writer();
        writer.WriteIfDue(Now);

        _machine = "pc-Renamed";
        writer.WriteIfDue(Now + PublisherHeartbeat.Interval);

        File.Exists(BeatPath("pc-Renamed")).Should().BeTrue();
    }

    [Fact]
    public void WriteIfDue_UnreachableTarget_DoesNotThrowIntoTheDaemonLoop()
    {
        // The mount being gone is exactly the state the receiver is about to read as
        // disconnected; throwing here would take publishing down with it.
        var notADirectory = Path.Combine(_root, "not-a-mount");
        File.WriteAllText(notADirectory, "this is a file");
        _entries.Add(new PublisherEntry
        {
            Name = "gone",
            Endpoint = Path.Combine(notADirectory, "sessions"),
        });

        var act = () => Writer().WriteIfDue(Now);

        act.Should().NotThrow();
    }

    [Fact]
    public void WriteIfDue_OneDeadLinkBesideALiveOne_LogsTheFailureOnceRatherThanEveryBeat()
    {
        // The dedup key is per link. A single shared key would be cleared by the live link's
        // success on every pass, so the dead mount would warn every five seconds forever and
        // rotate the daemon's other diagnostics out of a 1 MB log.
        var notADirectory = Path.Combine(_root, "not-a-mount");
        File.WriteAllText(notADirectory, "this is a file");
        RegisterFileLink();
        _entries.Add(new PublisherEntry { Name = "dead", Endpoint = Path.Combine(notADirectory, "sessions") });

        var log = new CapturingLogger();
        var writer = new HeartbeatWriter(() => _entries, () => _machine, log);

        writer.WriteIfDue(Now);
        writer.WriteIfDue(Now + PublisherHeartbeat.Interval);
        writer.WriteIfDue(Now + PublisherHeartbeat.Interval * 2);

        log.Lines.Should().ContainSingle().Which.Should().Contain("dead");
        File.Exists(BeatPath(_machine)).Should().BeTrue("the live link kept beating throughout");
    }

    [Fact]
    public void WriteIfDue_TwoFileLinks_BeatsIntoBoth()
    {
        var second = Path.Combine(_root, "receiver2", "sessions");
        Directory.CreateDirectory(second);
        RegisterFileLink();
        _entries.Add(new PublisherEntry { Name = "receiver2", Endpoint = second });

        Writer().WriteIfDue(Now);

        File.Exists(BeatPath(_machine)).Should().BeTrue();
        File.Exists(PublisherHeartbeat.PathFor(second, _machine)).Should().BeTrue();
    }

    private sealed class CapturingLogger : ILogger
    {
        public List<string> Lines { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Lines.Add(formatter(state, exception));
    }
}
