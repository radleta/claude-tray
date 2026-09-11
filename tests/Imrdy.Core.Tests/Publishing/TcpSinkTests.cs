using FluentAssertions;
using Imrdy.Core.Publishing;
using Imrdy.Core.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Imrdy.Core.Tests.Publishing;

public class TcpSinkTests
{
    private static StateFileModel Model(string sessionId = "s1", string status = "busy") => new()
    {
        SessionId = sessionId,
        Status = status,
        Project = "imrdy",
        Cwd = "/home/user/imrdy",
        HookEvent = "Stop",
    };

    private static SinkContext Context(string? authKey = null, params StateFileModel[] snapshot) =>
        new(() => "workstation-Ubuntu", () => authKey, new StateFileReader(), () => snapshot);

    private static TcpSink Sink(TestWireReceiver receiver, SinkContext context) =>
        new("desktop2", "127.0.0.1", receiver.Port, context, NullLogger.Instance);

    [Fact]
    public async Task Connect_SendsHelloFirst()
    {
        using var receiver = new TestWireReceiver();
        using var sink = Sink(receiver, Context(authKey: "secret"));

        (await receiver.WaitForAsync(f => f.Count >= 1)).Should().BeTrue();

        var hello = receiver.Frames[0];
        hello.Type.Should().Be(WireFrameTypes.Hello);
        hello.Machine.Should().Be("workstation-Ubuntu");
        hello.Key.Should().Be("secret");
        hello.SchemaVersion.Should().Be(WireProtocol.SchemaVersion);
    }

    [Fact]
    public async Task Connect_ReportsConnectedHealthUnderTheLinkName()
    {
        using var receiver = new TestWireReceiver();
        using var sink = Sink(receiver, Context());

        (await receiver.WaitForAsync(f => f.Count >= 1)).Should().BeTrue();

        sink.Health.Name.Should().Be("desktop2");
        sink.Health.State.Should().Be(SinkState.Connected);
        sink.Health.IsFailed.Should().BeFalse();
    }

    [Fact]
    public async Task Connect_SendsTheFullSnapshotAfterHello()
    {
        using var receiver = new TestWireReceiver();
        using var sink = Sink(receiver, Context(null, Model("a"), Model("b")));

        (await receiver.WaitForAsync(f => f.Count >= 3)).Should().BeTrue();

        receiver.Frames[0].Type.Should().Be(WireFrameTypes.Hello);
        receiver.Frames.Skip(1).Select(f => f.State!.SessionId).Should().BeEquivalentTo(["a", "b"]);
    }

    [Fact]
    public async Task Publish_StampsOriginMachineOnTheWire()
    {
        using var receiver = new TestWireReceiver();
        using var sink = Sink(receiver, Context());
        (await receiver.WaitForAsync(f => f.Count >= 1)).Should().BeTrue();

        var local = Model();
        await sink.PublishAsync(local, CancellationToken.None);

        (await receiver.WaitForAsync(f => f.Any(x => x.Type == WireFrameTypes.Session))).Should().BeTrue();

        receiver.Frames.Last().State!.OriginMachine.Should().Be("workstation-Ubuntu");

        // D5: the stamp is on the wire only — the caller's own model is untouched, so the
        // publisher's state file stays exactly what the hook wrote.
        local.OriginMachine.Should().BeNull();
    }

    [Fact]
    public async Task Publish_CountsTheSessionInHealth()
    {
        using var receiver = new TestWireReceiver();
        using var sink = Sink(receiver, Context());
        (await receiver.WaitForAsync(f => f.Count >= 1)).Should().BeTrue();

        await sink.PublishAsync(Model("a"), CancellationToken.None);
        await sink.PublishAsync(Model("a", "idle"), CancellationToken.None);
        await sink.PublishAsync(Model("b"), CancellationToken.None);

        sink.Health.SessionCount.Should().Be(2);
        sink.Health.LastSuccessAt.Should().NotBeNull();
        sink.Health.LastError.Should().BeNull();
    }

    [Fact]
    public async Task Remove_SendsARemoveFrameAndDropsTheSessionFromHealth()
    {
        using var receiver = new TestWireReceiver();
        using var sink = Sink(receiver, Context());
        (await receiver.WaitForAsync(f => f.Count >= 1)).Should().BeTrue();

        await sink.PublishAsync(Model("a"), CancellationToken.None);
        await sink.RemoveAsync("a", CancellationToken.None);

        (await receiver.WaitForAsync(f => f.Any(x => x.Type == WireFrameTypes.Remove))).Should().BeTrue();

        receiver.Frames.Last().SessionId.Should().Be("a");
        sink.Health.SessionCount.Should().Be(0);
    }

    [Fact]
    public async Task Publish_WhileTheLinkIsDown_DropsTheEventInsteadOfThrowingOrQueueing()
    {
        // Port 1 on loopback refuses immediately, so this is the down-link case with no wait.
        using var sink = new TcpSink("desktop2", "127.0.0.1", 1, Context(), NullLogger.Instance);

        var publish = async () => await sink.PublishAsync(Model(), CancellationToken.None);

        await publish.Should().NotThrowAsync();
        sink.Health.SessionCount.Should().Be(0);
    }

    [Fact]
    public async Task UnreachableReceiver_ReportsFailedWithAReason()
    {
        using var sink = new TcpSink("desktop2", "127.0.0.1", 1, Context(), NullLogger.Instance);

        var failed = false;
        for (var i = 0; i < 100 && !failed; i++)
        {
            await Task.Delay(20);
            failed = sink.Health.State == SinkState.Failed;
        }

        failed.Should().BeTrue();
        sink.Health.IsFailed.Should().BeTrue();
        sink.Health.LastError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Dispose_StopsTheLinkSoPublishesAfterItAreDropped()
    {
        using var receiver = new TestWireReceiver();
        var sink = Sink(receiver, Context());
        (await receiver.WaitForAsync(f => f.Count >= 1)).Should().BeTrue();

        sink.Dispose();

        await sink.PublishAsync(Model("after-dispose"), CancellationToken.None);

        receiver.Frames.Should().NotContain(f => f.State != null && f.State.SessionId == "after-dispose");
    }

    [Fact]
    public async Task Connect_RetiresAnEndedSessionInsteadOfPublishingItsState()
    {
        // The connect snapshot is a receiver's only repair path for an event dropped while
        // the link was down (D13), and the `end` delta is the event whose loss costs the most:
        // a receiver that misses it draws the finished session forever, because both its
        // eviction paths key on the state file being gone. Skipping the file leaves that
        // unrepairable, so the snapshot sends the removal instead.
        using var receiver = new TestWireReceiver();
        using var sink = Sink(receiver, Context(null, Model("live"), Model("finished", status: "end")));

        (await receiver.WaitForAsync(f => f.Count >= 3)).Should().BeTrue();

        receiver.Frames[0].Type.Should().Be(WireFrameTypes.Hello);

        var session = receiver.Frames.Single(f => f.Type == WireFrameTypes.Session);
        session.State!.SessionId.Should().Be("live");

        var remove = receiver.Frames.Single(f => f.Type == WireFrameTypes.Remove);
        remove.SessionId.Should().Be("finished");
    }

    [Fact]
    public async Task Refused_ByAReceiverThatClosesAtOnce_BacksOffInsteadOfRedialingEverySecond()
    {
        // A receiver that accepts the socket and then closes it is what WireListener does on
        // an auth-key mismatch — a typo in network.authKey, not an attacker. The dial loop used
        // to read "ConnectAsync returned" as a healthy link and reset the backoff there, so
        // that typo left the publisher redialling at 1 Hz forever with MaxBackoff never
        // engaging, re-reading the whole sessions directory on each cycle.
        //
        // The arithmetic this asserts, with InitialBackoff = 1s doubling per cycle and a
        // connection that never survives LinkHeldMinimum past the end of its connect snapshot:
        // dials land at t=0, 1s and 3s, and the fourth is not due until t=7s. Resetting on
        // connect instead would put a dial at every whole second — five inside the same window.
        //
        // The 4.5s of wall clock is the cost of asserting the property at all: it is a timing
        // property of a real socket with no injection seam, and hiding it behind a Category the
        // default filter excludes would mean the guard never runs. The margins are wide on both
        // sides — the second dial lands at 1s, the fourth is 2.5s past the window's end.
        using var receiver = new TestWireReceiver(refuseImmediately: true);
        using var sink = Sink(receiver, Context());

        await Task.Delay(TimeSpan.FromMilliseconds(4500));

        receiver.Accepts.Should().BeGreaterThan(1, "the sink must keep retrying a refused link");
        receiver.Accepts.Should().BeLessThanOrEqualTo(3);
    }
}
