using FluentAssertions;
using Imrdy.Core.Publishing;
using Imrdy.Core.State;
using Microsoft.Extensions.Logging.Abstractions;

namespace Imrdy.Core.Tests.Publishing;

public class FileSinkTests : IDisposable
{
    private readonly string _targetDir;
    private readonly StateFileReader _reader = new();
    private readonly FileSink _sink;

    public FileSinkTests()
    {
        _targetDir = Path.Combine(Path.GetTempPath(), "imrdy-filesink-tests", Guid.NewGuid().ToString());
        Directory.CreateDirectory(_targetDir);
        _sink = new FileSink(_targetDir, "host-link", () => "workstation-Ubuntu", _reader, NullLogger.Instance);
    }

    public void Dispose()
    {
        if (Directory.Exists(_targetDir))
        {
            Directory.Delete(_targetDir, recursive: true);
        }
    }

    private static StateFileModel Model(string sessionId = "s1", string status = "busy") => new()
    {
        SessionId = sessionId,
        Status = status,
        Project = "imrdy",
        Cwd = "/home/user/imrdy",
        HookEvent = "Stop",
    };

    private string PathFor(string sessionId) => Path.Combine(_targetDir, $"{sessionId}.json");

    [Fact]
    public async Task PublishAsync_WritesStampedStateIntoTargetDirectory()
    {
        await _sink.PublishAsync(Model(), CancellationToken.None);

        var written = _reader.ReadStateFile(PathFor("s1"));
        written.Should().NotBeNull();
        written!.OriginMachine.Should().Be("workstation-Ubuntu");
        written.Status.Should().Be("busy");
    }

    [Fact]
    public async Task PublishAsync_LeavesNoTempOrRenameArtifacts()
    {
        // D15: the write is direct. A temp-then-rename is reported to the host watcher as
        // Deleted-then-Renamed ~2ms apart, which the receiver cannot tell from a session end.
        await _sink.PublishAsync(Model(), CancellationToken.None);

        Directory.GetFileSystemEntries(_targetDir).Should().ContainSingle()
            .Which.Should().EndWith("s1.json");
    }

    [Fact]
    public async Task PublishAsync_OverExistingFile_KeepsReceiverOwnedFields()
    {
        _reader.WriteStateFile(PathFor("s1"), Model() with { SoundPack = "retro", DesktopIndex = 3 });

        await _sink.PublishAsync(Model(status: "idle"), CancellationToken.None);

        var written = _reader.ReadStateFile(PathFor("s1"))!;
        written.Status.Should().Be("idle");
        written.SoundPack.Should().Be("retro");
        written.DesktopIndex.Should().Be(3);
    }

    [Fact]
    public async Task RemoveAsync_DeletesTheStateFile()
    {
        await _sink.PublishAsync(Model(), CancellationToken.None);

        await _sink.RemoveAsync("s1", CancellationToken.None);

        File.Exists(PathFor("s1")).Should().BeFalse();
    }

    [Fact]
    public async Task RemoveAsync_UnknownSession_IsANoOp()
    {
        await _sink.RemoveAsync("never-seen", CancellationToken.None);

        _sink.Health.LastError.Should().BeNull();
    }

    [Fact]
    public void Health_BeforeAnyDelivery_IsFileSinkWithNoSuccess()
    {
        var health = _sink.Health;

        health.State.Should().Be(SinkState.FileSink);
        health.Name.Should().Be("host-link", "health identifies the link, not this publisher");
        health.LastSuccessAt.Should().BeNull();
        health.SessionCount.Should().Be(0);
        health.IsFailed.Should().BeFalse();
    }

    [Fact]
    public async Task Health_NeverReportsConnected()
    {
        // D27: a file sink has no connection, and reporting one as Connected would be a lie
        // the operator acts on.
        await _sink.PublishAsync(Model(), CancellationToken.None);

        _sink.Health.State.Should().Be(SinkState.FileSink);
    }

    [Fact]
    public async Task Health_CountsDistinctSessions_AndDropsRemovedOnes()
    {
        await _sink.PublishAsync(Model("s1"), CancellationToken.None);
        await _sink.PublishAsync(Model("s2"), CancellationToken.None);
        await _sink.PublishAsync(Model("s1", "idle"), CancellationToken.None);

        _sink.Health.SessionCount.Should().Be(2);
        _sink.Health.LastSuccessAt.Should().NotBeNull();

        await _sink.RemoveAsync("s1", CancellationToken.None);

        _sink.Health.SessionCount.Should().Be(1);
    }

    [Fact]
    public async Task PublishAsync_UnreachableTarget_RecordsLastErrorWithoutThrowing()
    {
        var badDir = Path.Combine(_targetDir, "file-not-a-dir", "sessions");
        File.WriteAllText(Path.Combine(_targetDir, "file-not-a-dir"), "blocks directory creation");
        var sink = new FileSink(badDir, "host-link", () => "workstation", _reader, NullLogger.Instance);

        await sink.PublishAsync(Model(), CancellationToken.None);

        sink.Health.LastError.Should().NotBeNullOrEmpty();
        sink.Health.SessionCount.Should().Be(0);
    }

    [Fact]
    public async Task PublishAsync_AfterAFailure_ClearsLastErrorOnRecovery()
    {
        // The mount goes away (distro stopped, share dropped) and comes back. LastError is
        // what the connections window renders, so a stale one outlives the fault it named.
        var mount = Path.Combine(_targetDir, "mount");
        var sink = new FileSink(Path.Combine(mount, "sessions"), "host-link", () => "workstation", _reader, NullLogger.Instance);
        File.WriteAllText(mount, "a file where the mount point should be");

        await sink.PublishAsync(Model(), CancellationToken.None);
        sink.Health.LastError.Should().NotBeNullOrEmpty();

        File.Delete(mount);
        await sink.PublishAsync(Model(), CancellationToken.None);

        sink.Health.LastError.Should().BeNull();
        sink.Health.SessionCount.Should().Be(1);
    }

    [Fact]
    public async Task PublishAsync_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        var publish = () => _sink.PublishAsync(Model(), cts.Token);

        await publish.Should().ThrowAsync<OperationCanceledException>();
    }
}
