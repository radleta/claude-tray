using FluentAssertions;
using Imrdy.Core.Publishing;
using Imrdy.Core.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Imrdy.Core.Tests.Publishing;

public class SessionPublisherTests : IDisposable
{
    private readonly string _sessionsDir;
    private readonly StateFileReader _reader = new();
    private readonly RecordingSink _sink = new("receiver");
    private readonly SessionPublisher _publisher;
    private List<ISessionSink> _sinks;

    public SessionPublisherTests()
    {
        _sessionsDir = Path.Combine(Path.GetTempPath(), "imrdy-publisher-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_sessionsDir);
        _sinks = [_sink];
        _publisher = new SessionPublisher(_sessionsDir, _reader, () => _sinks, NullLogger.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_sessionsDir))
        {
            Directory.Delete(_sessionsDir, recursive: true);
        }
    }

    private static StateFileModel Model(string sessionId, string? originMachine = null) => new()
    {
        SessionId = sessionId,
        Status = "busy",
        Project = "imrdy",
        Cwd = "/home/user/imrdy",
        HookEvent = "Stop",
        OriginMachine = originMachine,
    };

    private void WriteSession(string sessionId, string? originMachine = null) =>
        _reader.WriteStateFile(Path.Combine(_sessionsDir, $"{sessionId}.json"), Model(sessionId, originMachine));

    [Fact]
    public async Task PublishSnapshotAsync_EmitsEveryLocalSession()
    {
        WriteSession("s1");
        WriteSession("s2");

        await _publisher.PublishSnapshotAsync(CancellationToken.None);

        _sink.Published.Select(s => s.SessionId).Should().BeEquivalentTo(["s1", "s2"]);
    }

    [Fact]
    public async Task PublishSnapshotAsync_EmptyDirectory_EmitsNothing()
    {
        await _publisher.PublishSnapshotAsync(CancellationToken.None);

        _sink.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishSnapshotAsync_SkipsSessionsThatArrivedFromAnotherPublisher()
    {
        // D5's no-re-publish guard. Without it a receiver that is also a publisher loops
        // every remote session back out under a second origin.
        WriteSession("mine");
        WriteSession("theirs", originMachine: "desk2");

        await _publisher.PublishSnapshotAsync(CancellationToken.None);

        _sink.Published.Select(s => s.SessionId).Should().BeEquivalentTo(["mine"]);
    }

    [Fact]
    public async Task PublishSessionAsync_EmitsTheNamedSession()
    {
        WriteSession("s1");

        await _publisher.PublishSessionAsync("s1", CancellationToken.None);

        _sink.Published.Should().ContainSingle().Which.SessionId.Should().Be("s1");
    }

    [Fact]
    public async Task PublishSessionAsync_RemoteSession_EmitsNothing()
    {
        WriteSession("theirs", originMachine: "desk2");

        await _publisher.PublishSessionAsync("theirs", CancellationToken.None);

        _sink.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishSessionAsync_MissingFile_IsSkippedNotReported()
    {
        await _publisher.PublishSessionAsync("never-existed", CancellationToken.None);

        _sink.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishSessionAsync_TornFile_IsSkipped()
    {
        // A mid-write read is the normal case, and the next event carries the same state.
        File.WriteAllText(Path.Combine(_sessionsDir, "s1.json"), "{\"session_id\": \"s1\", \"stat");

        await _publisher.PublishSessionAsync("s1", CancellationToken.None);

        _sink.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task PublishSessionAsync_StampsNothingIntoTheLocalFile()
    {
        // D5: origin_machine is stamped on the wire, never written into the publisher's own
        // state file, which stays exactly what the hook wrote.
        WriteSession("s1");

        await _publisher.PublishSessionAsync("s1", CancellationToken.None);

        _reader.ReadStateFile(Path.Combine(_sessionsDir, "s1.json"))!.OriginMachine.Should().BeNull();
    }

    [Fact]
    public async Task RemoveSessionAsync_MirrorsTheDeleteToEverySink()
    {
        _publisher.RecordOwnership(Model("s1"));

        await _publisher.RemoveSessionAsync("s1", CancellationToken.None);

        _sink.Removed.Should().BeEquivalentTo(["s1"]);
    }

    [Fact]
    public async Task RemoveSessionAsync_SessionThePublisherNeverSaw_IsNotMirrored()
    {
        // The guard has to fail closed. A tray that restarts with another machine's session
        // files still on disk (D20/D21 forbid evicting them) has published no snapshot while
        // no link was enabled, so nothing recorded those files as remote. Adding a link and
        // then clearing that machine's sessions must not send a removal that deletes the
        // publisher's own live state file — that is the receiver -> publisher command D4
        // forbids. An unmirrored removal only leaves a stale session a receiver can clear.
        await _publisher.RemoveSessionAsync("never-seen", CancellationToken.None);

        _sink.Removed.Should().BeEmpty();
    }

    [Fact]
    public async Task RecordOwnership_LetsTheHostAnswerForASessionThePublisherNeverRead()
    {
        // What closes the gap rather than papering over it: the tray holds the session's state
        // in memory when it deletes the file, so it can say what the publisher could not read.
        _publisher.RecordOwnership(Model("theirs", originMachine: "desk2"));
        _publisher.RecordOwnership(Model("mine"));

        await _publisher.RemoveSessionAsync("theirs", CancellationToken.None);
        await _publisher.RemoveSessionAsync("mine", CancellationToken.None);

        _sink.Removed.Should().BeEquivalentTo(["mine"]);
    }

    [Fact]
    public async Task RemoveSessionAsync_DoesNotMirrorARemoteSessionsDeletion()
    {
        // D4 forbids anything travelling receiver -> publisher, and D16 says clearing a remote
        // session clears local state and waits for the publisher to repopulate it. Without this
        // guard, clearing one on the receiver deleted the publisher's own live state file.
        WriteSession("remote", originMachine: "desk2");
        await _publisher.PublishSessionAsync("remote", CancellationToken.None);
        File.Delete(Path.Combine(_sessionsDir, "remote.json"));

        await _publisher.RemoveSessionAsync("remote", CancellationToken.None);

        _sink.Published.Should().BeEmpty("a remote session is never emitted either (D5)");
        _sink.Removed.Should().BeEmpty();
    }

    [Fact]
    public async Task RemoveSessionAsync_StillMirrorsALocalSessionAfterItWasPublished()
    {
        WriteSession("s1");
        await _publisher.PublishSessionAsync("s1", CancellationToken.None);
        File.Delete(Path.Combine(_sessionsDir, "s1.json"));

        await _publisher.RemoveSessionAsync("s1", CancellationToken.None);

        _sink.Removed.Should().BeEquivalentTo(["s1"]);
    }

    [Fact]
    public async Task RemoveSessionAsync_DoesNotNeedTheFileToStillExist()
    {
        // The file is already gone by the time the delete event arrives, so there is no
        // origin_machine left to consult — the recorded answer is the whole input.
        _publisher.RecordOwnership(Model("already-deleted"));

        await _publisher.RemoveSessionAsync("already-deleted", CancellationToken.None);

        _sink.Removed.Should().ContainSingle();
    }

    [Fact]
    public async Task EmitAsync_OneFailingSink_DoesNotStopTheOthers()
    {
        // D13: a receiver being unreachable drops that receiver's events and nothing else.
        var healthy = new RecordingSink("healthy");
        _sinks = [new ThrowingSink("broken"), healthy];
        WriteSession("s1");

        await _publisher.PublishSnapshotAsync(CancellationToken.None);

        healthy.Published.Should().ContainSingle();
    }

    [Fact]
    public async Task RemoveAsync_OneFailingSink_DoesNotStopTheOthers()
    {
        var healthy = new RecordingSink("healthy");
        _sinks = [new ThrowingSink("broken"), healthy];
        _publisher.RecordOwnership(Model("s1"));

        await _publisher.RemoveSessionAsync("s1", CancellationToken.None);

        healthy.Removed.Should().ContainSingle();
    }

    [Fact]
    public async Task Sinks_AreResolvedPerEmit_SoALiveReloadTakesEffect()
    {
        // D25: adding a link in the connections window must take effect with no restart.
        WriteSession("s1");
        var added = new RecordingSink("added-later");
        _sinks = [_sink, added];

        await _publisher.PublishSnapshotAsync(CancellationToken.None);

        added.Published.Should().ContainSingle();
    }

    [Fact]
    public async Task PublishSnapshotAsync_CancelledToken_Throws()
    {
        WriteSession("s1");
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var publish = () => _publisher.PublishSnapshotAsync(cts.Token);

        await publish.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task DrainAsync_MapsEachChangeKindToItsOperation()
    {
        WriteSession("s1");
        _publisher.RecordOwnership(Model("s2"));

        await _publisher.DrainAsync(
            [
                new SessionChange("s1", SessionChangeKind.Changed),
                new SessionChange("s2", SessionChangeKind.Removed),
            ],
            CancellationToken.None);

        _sink.Published.Select(s => s.SessionId).Should().Equal("s1");
        _sink.Removed.Should().Equal("s2");
    }

    [Fact]
    public async Task DrainAsync_RemoteSessionInTheBatch_IsNotRePublished()
    {
        // The receiver's ingest writes into the very directory the tray watches, so a remote
        // session comes back round as a local change event. D5's null-origin guard is what
        // keeps that round trip from becoming an amplifying loop across every link.
        WriteSession("remote", originMachine: "desk2");

        await _publisher.DrainAsync([new SessionChange("remote", SessionChangeKind.Changed)], CancellationToken.None);

        _sink.Published.Should().BeEmpty();
    }

    [Fact]
    public async Task DrainAsync_EmptyBatch_EmitsNothing()
    {
        await _publisher.DrainAsync([], CancellationToken.None);

        _sink.Published.Should().BeEmpty();
        _sink.Removed.Should().BeEmpty();
    }

    [Fact]
    public async Task DrainAsync_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var drain = () => _publisher.DrainAsync([new SessionChange("s1", SessionChangeKind.Changed)], cts.Token);

        await drain.Should().ThrowAsync<OperationCanceledException>();
    }

    private sealed class RecordingSink(string name) : ISessionSink
    {
        public List<StateFileModel> Published { get; } = [];
        public List<string> Removed { get; } = [];

        public SinkHealth Health { get; } = new(name, SinkState.Connected, null, null, 0);

        public Task PublishAsync(StateFileModel state, CancellationToken cancellationToken)
        {
            Published.Add(state);
            return Task.CompletedTask;
        }

        public Task RemoveAsync(string sessionId, CancellationToken cancellationToken)
        {
            Removed.Add(sessionId);
            return Task.CompletedTask;
        }
    }

    private sealed class ThrowingSink(string name) : ISessionSink
    {
        public SinkHealth Health { get; } = new(name, SinkState.Failed, null, "unreachable", 0);

        public Task PublishAsync(StateFileModel state, CancellationToken cancellationToken) =>
            throw new IOException("receiver unreachable");

        public Task RemoveAsync(string sessionId, CancellationToken cancellationToken) =>
            throw new IOException("receiver unreachable");
    }
}
