using System.Diagnostics;
using System.Text.Json;
using FluentAssertions;
using Imrdy.Core.Publishing;
using Imrdy.Core.State;
using Imrdy.Integration.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Imrdy.Integration.Tests.Publishing;

/// <summary>
/// The file-sink path end to end, with the two halves that cannot be faked in a unit test:
/// the state file is written by the <em>published binary's</em> hook, and the publisher is
/// driven by a real <see cref="FileSystemWatcher"/> rather than by a hand-enqueued change.
/// <para>
/// Nothing else in the corpus waits on OS watcher events, so the shape here is deliberate:
/// every wait is a poll against the thing being asserted with a generous ceiling, never a
/// fixed sleep, and the drain loop runs on the same cadence as the tray's 100ms tick so the
/// coalescing under test is the coalescing that ships.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
public class PublishPipelineIntegrationTests : IDisposable
{
    /// <summary>Ceiling for any wait on the OS delivering a watcher event. Overshoot is free; a flake is not.</summary>
    private static readonly TimeSpan WatcherTimeout = TimeSpan.FromSeconds(15);

    private const string OriginMachine = "publisher-box";

    private readonly CliTestFixture _cli = new();
    private readonly string _root;
    private readonly string _publisherHome;
    private readonly string _receiverSessions;
    private readonly string _sessionId = $"pipe{Guid.NewGuid():N}"[..24];

    private readonly StateFileReader _reader = new();
    private readonly SessionChangeQueue _queue = new();
    private readonly SessionDirectoryWatcher _watcher;
    private readonly FileSink _sink;
    private readonly SessionPublisher _publisher;
    private readonly List<SessionChange> _drained = [];

    public PublishPipelineIntegrationTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "imrdy-pipeline-" + Guid.NewGuid().ToString("N")[..8]);
        _publisherHome = Path.Combine(_root, "publisher");
        _receiverSessions = Path.Combine(_root, "receiver", "sessions");
        Directory.CreateDirectory(PublisherSessions);
        Directory.CreateDirectory(_receiverSessions);

        // The hook probes the tray mutex and may spawn a tray otherwise. This suite is about
        // the publish pipeline, not the tray, and a spawned tray would hold this temp home.
        File.WriteAllText(Path.Combine(_publisherHome, "config.json"), """{"tray":{"enabled":false}}""");

        _watcher = new SessionDirectoryWatcher(PublisherSessions, _queue);
        _sink = new FileSink(_receiverSessions, "host-link", () => OriginMachine, _reader, NullLogger.Instance);
        _publisher = new SessionPublisher(PublisherSessions, _reader, () => [_sink], NullLogger.Instance);
        _watcher.Start();
    }

    private string PublisherSessions => Path.Combine(_publisherHome, "sessions");

    private string DeliveredPath => Path.Combine(_receiverSessions, $"{_sessionId}.json");

    private Task<(int ExitCode, string StdOut, string StdErr)> RunHookAsync(string hookEvent, string status = "busy") =>
        _cli.RunAsync(
            "hook",
            stdin: JsonSerializer.Serialize(new
            {
                session_id = _sessionId,
                hook_event_name = hookEvent,
                cwd = "/d/dev/test",
                status,
            }),
            workingDirectory: _publisherHome,
            environmentVariables: new Dictionary<string, string> { ["IMRDY_HOME"] = _publisherHome });

    /// <summary>
    /// Drains on the tray's own cadence until <paramref name="until"/> holds, recording every
    /// change so a test can assert what the OS actually delivered and in what order.
    /// </summary>
    private async Task<bool> PumpUntilAsync(Func<bool> until)
    {
        var deadline = Stopwatch.StartNew();
        while (deadline.Elapsed < WatcherTimeout)
        {
            var batch = _queue.Drain();
            if (batch.Count > 0)
            {
                _drained.AddRange(batch);
                await _publisher.DrainAsync(batch, CancellationToken.None);
            }

            if (until()) return true;
            await Task.Delay(100);
        }

        return false;
    }

    [Fact]
    public async Task HookWrite_ReachesTheFileSink_ThroughARealWatcher()
    {
        var (exitCode, _, _) = await RunHookAsync("Stop");
        exitCode.Should().Be(0);

        (await PumpUntilAsync(() => File.Exists(DeliveredPath)))
            .Should().BeTrue("the watcher should have delivered the hook's write within {0}s", WatcherTimeout.TotalSeconds);

        var delivered = _reader.ReadStateFile(DeliveredPath);
        delivered.Should().NotBeNull();
        delivered!.SessionId.Should().Be(_sessionId);
        delivered.OriginMachine.Should().Be(OriginMachine, "the sink stamps origin on the way out (D5)");

        var local = _reader.ReadStateFile(Path.Combine(PublisherSessions, $"{_sessionId}.json"));
        local!.OriginMachine.Should().BeNull("the publisher's own file stays exactly what the hook wrote (D5)");
    }

    [Fact]
    public async Task DeleteThenRecreate_LeavesTheSessionDeliveredRatherThanRemoved()
    {
        // The race idea.md names: the receiver deletes a session file and a live file-sink
        // publisher recreates it. Both events reach one watcher, and the last one is the
        // truth. Nothing else in the corpus exercises this against a real watcher.
        await RunHookAsync("Stop");
        (await PumpUntilAsync(() => File.Exists(DeliveredPath))).Should().BeTrue();

        File.Delete(Path.Combine(PublisherSessions, $"{_sessionId}.json"));
        (await PumpUntilAsync(() => !File.Exists(DeliveredPath)))
            .Should().BeTrue("a delete mirrors to the sink (D12)");

        await RunHookAsync("UserPromptSubmit");
        (await PumpUntilAsync(() => File.Exists(DeliveredPath)))
            .Should().BeTrue("the recreate must win over the delete that preceded it");

        _drained.Select(c => c.Kind).Should()
            .Contain(SessionChangeKind.Removed).And.Contain(SessionChangeKind.Changed);
        _drained.Should().OnlyContain(c => c.SessionId == _sessionId);
        _drained[^1].Kind.Should().Be(SessionChangeKind.Changed, "the last event describes the file as it now is");
    }

    [Fact]
    public async Task RemovedSession_IsMirroredToTheSink()
    {
        await RunHookAsync("Stop");
        (await PumpUntilAsync(() => File.Exists(DeliveredPath))).Should().BeTrue();

        File.Delete(Path.Combine(PublisherSessions, $"{_sessionId}.json"));

        (await PumpUntilAsync(() => !File.Exists(DeliveredPath)))
            .Should().BeTrue("publishers mirror deletes rather than leaving a ghost (D12, D15)");
    }

    [Fact]
    public async Task RemoteSession_IsNotRepublished()
    {
        // D5's no-re-publish guard, through the real watcher: a file that already carries an
        // origin arrived from somewhere else, and re-emitting it would loop it back out.
        var remoteId = $"remote{Guid.NewGuid():N}"[..24];
        File.WriteAllText(
            Path.Combine(PublisherSessions, $"{remoteId}.json"),
            $$"""{"session_id":"{{remoteId}}","status":"busy","origin_machine":"other-box"}""");

        await RunHookAsync("Stop");
        (await PumpUntilAsync(() => File.Exists(DeliveredPath))).Should().BeTrue();

        File.Exists(Path.Combine(_receiverSessions, $"{remoteId}.json"))
            .Should().BeFalse("a session that already carries an origin is not this machine's to publish");
    }

    public void Dispose()
    {
        _watcher.Dispose();

        try
        {
            if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
        }
        catch (IOException ex)
        {
            Console.Error.WriteLine($"[PublishPipelineIntegrationTests] cleanup of '{_root}' failed: {ex.Message}");
        }
    }
}
