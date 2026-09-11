using FluentAssertions;
using Imrdy.Core.Publishing;

namespace Imrdy.Core.Tests.Publishing;

public class DaemonLockTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _lockPath;

    public DaemonLockTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "imrdy-daemonlock-tests", Guid.NewGuid().ToString());
        _lockPath = Path.Combine(_tempDir, "daemon.lock");
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    [Fact]
    public void TryAcquire_OnAFreshPath_TakesTheLockAndCreatesTheDirectory()
    {
        using var held = DaemonLock.TryAcquire(_lockPath);

        held.Should().NotBeNull();
        held!.OwnerPid.Should().Be(Environment.ProcessId);
        File.Exists(_lockPath).Should().BeTrue();
    }

    [Fact]
    public void TryAcquire_WhileHeld_ReturnsNull()
    {
        using var first = DaemonLock.TryAcquire(_lockPath);

        var second = DaemonLock.TryAcquire(_lockPath);

        second.Should().BeNull("a second daemon must not start alongside a live one");
    }

    [Fact]
    public void TryAcquire_AfterRelease_Succeeds()
    {
        var first = DaemonLock.TryAcquire(_lockPath);
        first.Should().NotBeNull();
        first!.Dispose();

        using var second = DaemonLock.TryAcquire(_lockPath);

        second.Should().NotBeNull();
    }

    [Fact]
    public void TryAcquire_OverAStaleLockFile_Succeeds()
    {
        // A terminated WSL distro leaves both files behind with a dead PID in them. Their
        // existence must never be what refuses the lock — only a live holder may.
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(_lockPath, "");
        File.WriteAllText(DaemonLock.PidFilePathFor(_lockPath), "999999");

        using var held = DaemonLock.TryAcquire(_lockPath);

        held.Should().NotBeNull();
        DaemonLock.ReadOwnerPid(_lockPath).Should().Be(Environment.ProcessId);
    }

    [Fact]
    public void ReadOwnerPid_WhileTheLockIsHeld_StillReads()
    {
        // build-dev.sh reads this to signal a running daemon. The PID lives outside the
        // locked file precisely so an exclusive hold cannot lock out a plain reader — a
        // Windows share mode is mandatory and would refuse one outright.
        using var held = DaemonLock.TryAcquire(_lockPath);

        DaemonLock.ReadOwnerPid(_lockPath).Should().Be(Environment.ProcessId);
    }

    [Fact]
    public void ReadOwnerPid_MissingFile_ReturnsNull()
    {
        DaemonLock.ReadOwnerPid(_lockPath).Should().BeNull();
    }

    [Fact]
    public void ReadOwnerPid_GarbageContent_ReturnsNull()
    {
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(DaemonLock.PidFilePathFor(_lockPath), "not a pid");

        DaemonLock.ReadOwnerPid(_lockPath).Should().BeNull();
    }

    [Fact]
    public void Dispose_ClearsThePidFileSoADeadOwnerIsNotReadAsLive()
    {
        DaemonLock.TryAcquire(_lockPath)!.Dispose();

        DaemonLock.ReadOwnerPid(_lockPath).Should().BeNull();
    }

    [Fact]
    public void Dispose_LeavesTheLockFileOnDisk()
    {
        // Deleting it would race a daemon starting in the same instant, which would then
        // hold a lock on an unlinked inode.
        DaemonLock.TryAcquire(_lockPath)!.Dispose();

        File.Exists(_lockPath).Should().BeTrue();
    }

    [Fact]
    public void PidFilePathFor_SitsBesideTheLockFile()
    {
        DaemonLock.PidFilePathFor(_lockPath).Should().Be(Path.Combine(_tempDir, "daemon.pid"));
    }

    [Fact]
    public void IsRunning_NoLockFile_IsFalse()
    {
        DaemonLock.IsRunning(_lockPath).Should().BeFalse();
    }

    [Fact]
    public void IsRunning_WhileHeld_IsTrue()
    {
        using var held = DaemonLock.TryAcquire(_lockPath);

        DaemonLock.IsRunning(_lockPath).Should().BeTrue();
    }

    [Fact]
    public void IsRunning_AfterRelease_IsFalse()
    {
        DaemonLock.TryAcquire(_lockPath)!.Dispose();

        DaemonLock.IsRunning(_lockPath).Should().BeFalse();
    }

    [Fact]
    public void IsRunning_StaleLockFileWithNoHolder_IsFalse()
    {
        // A terminated distro leaves the file. The probe must not read that as a live daemon,
        // or the hook would never respawn one.
        Directory.CreateDirectory(_tempDir);
        File.WriteAllText(_lockPath, "");
        File.WriteAllText(DaemonLock.PidFilePathFor(_lockPath), "999999");

        DaemonLock.IsRunning(_lockPath).Should().BeFalse();
    }

    [Fact]
    public void IsRunning_LeavesNoTrace()
    {
        // The hook calls this hundreds of times per session, so unlike TryAcquire it must
        // record no PID and create no file.
        DaemonLock.IsRunning(_lockPath);

        File.Exists(_lockPath).Should().BeFalse();
        File.Exists(DaemonLock.PidFilePathFor(_lockPath)).Should().BeFalse();
    }

    [Fact]
    public void IsRunning_DoesNotStealTheLock()
    {
        using var held = DaemonLock.TryAcquire(_lockPath);

        DaemonLock.IsRunning(_lockPath);

        DaemonLock.TryAcquire(_lockPath).Should().BeNull("the probe must not have released the live holder's lock");
    }
}
