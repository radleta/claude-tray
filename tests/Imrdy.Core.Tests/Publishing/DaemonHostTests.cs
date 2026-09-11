using FluentAssertions;
using Imrdy.Core.Publishing;
using Imrdy.Core.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Imrdy.Core.Tests.Publishing;

public class DaemonHostTests : IDisposable
{
    private readonly string _sessionsDir;
    private readonly string _targetDir;
    private readonly StateFileReader _reader = new();
    private readonly SessionChangeQueue _queue = new();
    private readonly FileSink _sink;
    private readonly DaemonHost _host;

    public DaemonHostTests()
    {
        var root = Path.Combine(Path.GetTempPath(), "imrdy-daemonhost-tests", Guid.NewGuid().ToString());
        _sessionsDir = Path.Combine(root, "sessions");
        _targetDir = Path.Combine(root, "target");
        Directory.CreateDirectory(_sessionsDir);
        Directory.CreateDirectory(_targetDir);

        _sink = new FileSink(_targetDir, "host", () => "workstation-Ubuntu", _reader, NullLogger.Instance);
        var publisher = new SessionPublisher(
            _sessionsDir, _reader, () => [_sink], NullLogger.Instance);

        _host = new DaemonHost(publisher, _queue, TimeSpan.FromMilliseconds(10), NullLogger.Instance);
    }

    public void Dispose()
    {
        var root = Directory.GetParent(_sessionsDir)!.FullName;
        if (Directory.Exists(root))
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private void WriteSession(string sessionId) => _reader.WriteStateFile(
        Path.Combine(_sessionsDir, $"{sessionId}.json"),
        new StateFileModel
        {
            SessionId = sessionId,
            Status = "busy",
            Project = "imrdy",
            Cwd = "/home/user/imrdy",
            HookEvent = "Stop",
        });

    private bool DeliveredExists(string sessionId) =>
        File.Exists(Path.Combine(_targetDir, $"{sessionId}.json"));

    [Fact]
    public async Task DrainOnceAsync_EmptyQueue_DoesNothing()
    {
        await _host.DrainOnceAsync(CancellationToken.None);

        Directory.GetFiles(_targetDir).Should().BeEmpty();
    }

    [Fact]
    public async Task DrainOnceAsync_ChangedSession_IsDelivered()
    {
        WriteSession("s1");
        _queue.Enqueue("s1", SessionChangeKind.Changed);

        await _host.DrainOnceAsync(CancellationToken.None);

        DeliveredExists("s1").Should().BeTrue();
        _reader.ReadStateFile(Path.Combine(_targetDir, "s1.json"))!
            .OriginMachine.Should().Be("workstation-Ubuntu");
    }

    [Fact]
    public async Task DrainOnceAsync_RemovedSession_DeletesAtTheReceiver()
    {
        WriteSession("s1");
        _queue.Enqueue("s1", SessionChangeKind.Changed);
        await _host.DrainOnceAsync(CancellationToken.None);

        _queue.Enqueue("s1", SessionChangeKind.Removed);
        await _host.DrainOnceAsync(CancellationToken.None);

        DeliveredExists("s1").Should().BeFalse();
    }

    [Fact]
    public async Task DrainOnceAsync_LeavesTheQueueEmpty()
    {
        WriteSession("s1");
        _queue.Enqueue("s1", SessionChangeKind.Changed);

        await _host.DrainOnceAsync(CancellationToken.None);

        _queue.PendingCount.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_SendsTheConnectSnapshotBeforeAnyEvent()
    {
        // D12: pure deltas leave a session that went quiet before the receiver connected
        // invisible forever.
        WriteSession("went-quiet-earlier");
        using var cts = new CancellationTokenSource();

        var run = _host.RunAsync(cts.Token);
        await WaitFor(() => DeliveredExists("went-quiet-earlier"));
        await cts.CancelAsync();
        await run;

        DeliveredExists("went-quiet-earlier").Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_DrainsEventsUntilCancelled()
    {
        using var cts = new CancellationTokenSource();
        var run = _host.RunAsync(cts.Token);

        WriteSession("s1");
        _queue.Enqueue("s1", SessionChangeKind.Changed);
        await WaitFor(() => DeliveredExists("s1"));

        await cts.CancelAsync();
        await run;

        DeliveredExists("s1").Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_Cancellation_ReturnsWithoutThrowing()
    {
        using var cts = new CancellationTokenSource();
        var run = _host.RunAsync(cts.Token);

        await cts.CancelAsync();
        await run;

        run.IsCompletedSuccessfully.Should().BeTrue("cancellation is the normal way the daemon ends, not a fault");
    }

    [Fact]
    public async Task RunAsync_AlreadyCancelled_ReturnsImmediately()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await _host.RunAsync(cts.Token);

        Directory.GetFiles(_targetDir).Should().BeEmpty();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(10);
        }

        throw new TimeoutException("condition not met within 5s");
    }
}
