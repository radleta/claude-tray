using System.Net.Sockets;
using FluentAssertions;
using Imrdy.Core.Publishing;
using Imrdy.Core.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Imrdy.Core.Tests.Publishing;

public class WireListenerTests : IDisposable
{
    private readonly string _sessionsDir;
    private readonly StateFileReader _reader = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly List<IDisposable> _disposables = [];

    public WireListenerTests()
    {
        _sessionsDir = Path.Combine(Path.GetTempPath(), "imrdy-listener-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_sessionsDir);
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }

        _cts.Dispose();

        if (Directory.Exists(_sessionsDir))
        {
            Directory.Delete(_sessionsDir, recursive: true);
        }
    }

    private WireListener StartListener(string? authKey = null)
    {
        var listener = new WireListener(
            0,
            () => authKey,
            new SessionIngest(_sessionsDir, _reader),
            NullLogger.Instance);

        _disposables.Add(listener);
        listener.Start(_cts.Token).Should().BeTrue();
        return listener;
    }

    private TcpSink StartSink(WireListener listener, string? authKey = null, params StateFileModel[] snapshot)
    {
        var sink = new TcpSink(
            "receiver",
            "127.0.0.1",
            listener.BoundPort,
            new SinkContext(() => "workstation-Ubuntu", () => authKey, _reader, () => snapshot),
            NullLogger.Instance);

        _disposables.Add(sink);
        return sink;
    }

    private static StateFileModel Model(string sessionId = "s1", string status = "busy") => new()
    {
        SessionId = sessionId,
        Status = status,
        Project = "imrdy",
        Cwd = "/home/user/imrdy",
        HookEvent = "Stop",
    };

    private string PathFor(string sessionId) => Path.Combine(_sessionsDir, $"{sessionId}.json");

    private async Task<bool> WaitAsync(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
        while (DateTime.UtcNow < deadline && !condition())
        {
            await Task.Delay(20);
        }

        return condition();
    }

    /// <summary>Sends raw lines a real <see cref="TcpSink"/> would never produce.</summary>
    private async Task<TcpClient> SendRawAsync(WireListener listener, params string[] lines)
    {
        var client = new TcpClient();
        _disposables.Add(client);
        await client.ConnectAsync("127.0.0.1", listener.BoundPort);

        var stream = client.GetStream();
        foreach (var line in lines)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(line + "\n");
            await stream.WriteAsync(bytes);
        }

        await stream.FlushAsync();
        return client;
    }

    [Fact]
    public async Task SinkToListener_WritesTheSessionFileWithOriginStamped()
    {
        var listener = StartListener();
        var sink = StartSink(listener);

        await WaitAsync(() => sink.Health.State == SinkState.Connected);
        await sink.PublishAsync(Model(), CancellationToken.None);

        (await WaitAsync(() => File.Exists(PathFor("s1")))).Should().BeTrue();

        var stored = _reader.ReadStateFile(PathFor("s1"))!;
        stored.Status.Should().Be("busy");
        stored.OriginMachine.Should().Be("workstation-Ubuntu");
    }

    [Fact]
    public async Task ConnectSnapshot_LandsWithoutAnyFurtherEvents()
    {
        var listener = StartListener();
        StartSink(listener, null, Model("a"), Model("b"));

        (await WaitAsync(() => File.Exists(PathFor("a")) && File.Exists(PathFor("b")))).Should().BeTrue();
    }

    [Fact]
    public async Task Ingest_KeepsTheReceiversOwnFields()
    {
        // D34: a publisher update must not destroy the receiver's per-session overrides.
        _reader.WriteStateFile(
            PathFor("s1"),
            Model("s1", "idle") with { SoundPack = "quiet", IconStyle = "hexagons", DesktopIndex = 4 });

        var listener = StartListener();
        var sink = StartSink(listener);
        await WaitAsync(() => sink.Health.State == SinkState.Connected);

        await sink.PublishAsync(Model("s1") with { DesktopIndex = 99 }, CancellationToken.None);

        (await WaitAsync(() => _reader.ReadStateFile(PathFor("s1"))?.Status == "busy")).Should().BeTrue();

        var stored = _reader.ReadStateFile(PathFor("s1"))!;
        stored.SoundPack.Should().Be("quiet");
        stored.IconStyle.Should().Be("hexagons");
        stored.DesktopIndex.Should().Be(4);
    }

    [Fact]
    public async Task RemoveFrame_DeletesTheSessionFile()
    {
        var listener = StartListener();
        var sink = StartSink(listener);
        await WaitAsync(() => sink.Health.State == SinkState.Connected);

        await sink.PublishAsync(Model(), CancellationToken.None);
        (await WaitAsync(() => File.Exists(PathFor("s1")))).Should().BeTrue();

        await sink.RemoveAsync("s1", CancellationToken.None);

        (await WaitAsync(() => !File.Exists(PathFor("s1")))).Should().BeTrue();
    }

    [Fact]
    public async Task MatchingAuthKey_IsAccepted()
    {
        var listener = StartListener(authKey: "secret");
        var sink = StartSink(listener, authKey: "secret");

        await WaitAsync(() => sink.Health.State == SinkState.Connected);
        await sink.PublishAsync(Model(), CancellationToken.None);

        (await WaitAsync(() => File.Exists(PathFor("s1")))).Should().BeTrue();
    }

    [Fact]
    public async Task WrongAuthKey_IsRefusedWithTheReasonAsTheLinksLastError()
    {
        var listener = StartListener(authKey: "secret");
        var sink = StartSink(listener, authKey: "wrong");

        (await WaitAsync(() => listener.Health().Any(h => h.IsFailed))).Should().BeTrue();

        var link = listener.Health().Single();
        link.Name.Should().Be("workstation-Ubuntu");
        link.LastError.Should().Be("auth key mismatch");

        await sink.PublishAsync(Model(), CancellationToken.None);
        await Task.Delay(200);
        File.Exists(PathFor("s1")).Should().BeFalse();
    }

    [Fact]
    public async Task DifferentSchemaMajor_IsRefusedWithAStatedReason()
    {
        var listener = StartListener();

        await SendRawAsync(
            listener,
            """{"type":"hello","schemaVersion":"2","machine":"future-box"}""",
            """{"type":"session","state":{"session_id":"s1","status":"busy","project":"p","cwd":"/p","hook_event":"Stop"}}""");

        (await WaitAsync(() => listener.Health().Any(h => h.IsFailed))).Should().BeTrue();

        listener.Health().Single().LastError.Should().Contain("schema major 2");
        File.Exists(PathFor("s1")).Should().BeFalse();
    }

    [Fact]
    public async Task FirstFrameThatIsNotHello_IsRefused()
    {
        var listener = StartListener();

        await SendRawAsync(listener, """{"type":"remove","session_id":"s1"}""");

        (await WaitAsync(() => listener.Health().Any(h => h.IsFailed))).Should().BeTrue();
        listener.Health().Single().LastError.Should().Contain("expected 'hello'");
    }

    [Fact]
    public async Task UnknownFrameType_IsSkippedAndTheConnectionSurvives()
    {
        // D28: within a schema major, a peer one revision ahead may send frames this build has
        // no name for. Skipping the frame and keeping the link is the difference between a
        // partial upgrade working and a dead link.
        var listener = StartListener();

        await SendRawAsync(
            listener,
            """{"type":"hello","schemaVersion":"1","machine":"desktop2"}""",
            """{"type":"invented-later","payload":{"anything":1}}""",
            """{"type":"session","state":{"session_id":"s1","status":"busy","project":"p","cwd":"/p","hook_event":"Stop"}}""");

        (await WaitAsync(() => File.Exists(PathFor("s1")))).Should().BeTrue();
        listener.Health().Single().State.Should().Be(SinkState.Connected);
    }

    [Fact]
    public async Task SessionFrameWithATraversingId_IsRefusedAndWritesNothing()
    {
        // The lowest-trust input in the codebase composing a filename. The hook path has
        // validated on the same rule since before the network existed.
        var listener = StartListener();

        await SendRawAsync(
            listener,
            """{"type":"hello","schemaVersion":"1","machine":"desktop2"}""",
            """{"type":"session","state":{"session_id":"../../evil","status":"busy","project":"p","cwd":"/p","hook_event":"Stop"}}""");

        (await WaitAsync(() => listener.Health().Any(h => h.IsFailed))).Should().BeTrue();

        listener.Health().Single().LastError.Should().Contain("session id");
        Directory.GetFiles(_sessionsDir).Should().BeEmpty();
        File.Exists(Path.Combine(_sessionsDir, "..", "..", "evil.json")).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveFrameWithATraversingId_IsRefusedAndDeletesNothing()
    {
        var listener = StartListener();
        var victim = Path.Combine(_sessionsDir, "..", "keep.json");
        File.WriteAllText(victim, "{}");

        await SendRawAsync(
            listener,
            """{"type":"hello","schemaVersion":"1","machine":"desktop2"}""",
            """{"type":"remove","session_id":"../keep"}""");

        (await WaitAsync(() => listener.Health().Any(h => h.IsFailed))).Should().BeTrue();
        File.Exists(victim).Should().BeTrue();
        File.Delete(victim);
    }

    [Fact]
    public async Task MachineNameCarryingControlCharacters_IsEscapedBeforeItIsKept()
    {
        // The name becomes a log field, a dictionary key, a row in the connections window and
        // the origin_machine stamped on every session it delivers (CWE-117).
        var listener = StartListener();

        await SendRawAsync(
            listener,
            """{"type":"hello","schemaVersion":"1","machine":"desk2\r\nforged"}""");

        (await WaitAsync(() => listener.Health().Count > 0)).Should().BeTrue();

        listener.Health().Single().Name.Should().Be(@"desk2\r\nforged");
    }

    [Fact]
    public async Task Disconnect_ReportsTheLinkUnhealthyButKeepsItsSessionsOnDisk()
    {
        // D20: connection drops are common and transient; icons that vanish and reappear are
        // worse than one that looks stale.
        var listener = StartListener();
        var sink = StartSink(listener);
        await WaitAsync(() => sink.Health.State == SinkState.Connected);

        await sink.PublishAsync(Model(), CancellationToken.None);
        (await WaitAsync(() => File.Exists(PathFor("s1")))).Should().BeTrue();

        sink.Dispose();

        (await WaitAsync(() => listener.Health().Single().IsFailed)).Should().BeTrue();

        listener.Health().Single().Name.Should().Be("workstation-Ubuntu");
        File.Exists(PathFor("s1")).Should().BeTrue();
    }

    [Fact]
    public async Task Health_CountsSessionsPerPublisher()
    {
        var listener = StartListener();
        var sink = StartSink(listener);
        await WaitAsync(() => sink.Health.State == SinkState.Connected);

        await sink.PublishAsync(Model("a"), CancellationToken.None);
        await sink.PublishAsync(Model("b"), CancellationToken.None);

        (await WaitAsync(() => listener.Health().Single().SessionCount == 2)).Should().BeTrue();

        listener.Health().Single().LastSuccessAt.Should().NotBeNull();
    }
}
