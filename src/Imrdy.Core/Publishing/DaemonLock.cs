namespace Imrdy.Core.Publishing;

/// <summary>
/// Single-instance guard for the Linux daemon (D32): an exclusively-held lock file, plus a
/// PID file beside it.
/// <para>
/// This is the Linux counterpart to the Windows <c>Global\ImrdyMonitor</c> mutex, and
/// nothing equivalent existed in this codebase before. A plain PID file is not enough on
/// its own: a WSL distro that is terminated rather than shut down leaves the file behind,
/// and PID reuse across a distro restart makes "is that PID alive?" answer yes about the
/// wrong process. Holding a file open with <see cref="FileShare.None"/> fixes that — .NET
/// implements share mode on Unix with <c>flock</c>, which the kernel releases on abnormal
/// termination, so a terminated distro's lock is gone the moment the process is.
/// </para>
/// <para>
/// The PID lives in a second file rather than inside the lock file, because a handle held
/// exclusively is not readable the same way on both platforms — Windows share modes are
/// mandatory and would refuse a reader outright, while a Unix <c>flock</c> is advisory and
/// would let one through. Splitting them removes the difference: the lock file is only ever
/// locked, never read, and the PID file is only ever read, never locked. The PID is for the
/// operator and for <c>build-dev.sh</c> to signal; it is never what decides whether the
/// daemon is already running.
/// </para>
/// </summary>
public sealed class DaemonLock : IDisposable
{
    private readonly FileStream _stream;
    private readonly string _pidFilePath;

    private DaemonLock(FileStream stream, string pidFilePath, int ownerPid)
    {
        _stream = stream;
        _pidFilePath = pidFilePath;
        OwnerPid = ownerPid;
    }

    /// <summary>The PID recorded in the PID file this instance owns.</summary>
    public int OwnerPid { get; }

    /// <summary>The PID file that sits beside a given lock file.</summary>
    public static string PidFilePathFor(string lockFilePath) =>
        Path.ChangeExtension(lockFilePath, ".pid");

    /// <summary>
    /// Takes the lock and records this process's PID, or returns null if another daemon
    /// already holds it. A stale lock file left by a terminated distro never refuses the
    /// lock — only a live holder can.
    /// </summary>
    public static DaemonLock? TryAcquire(string lockFilePath)
    {
        var dir = Path.GetDirectoryName(lockFilePath);
        if (dir is not null && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        FileStream stream;
        try
        {
            stream = new FileStream(lockFilePath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Held by a live daemon. This is the expected negative result, not a fault.
            return null;
        }

        var pid = Environment.ProcessId;
        var pidFilePath = PidFilePathFor(lockFilePath);

        try
        {
            File.WriteAllText(pidFilePath, pid.ToString());
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The lock is what guarantees single-instance; the PID file is a convenience.
            // Failing to write it must not stop the daemon from starting, but it must not
            // leave a previous owner's PID behind either.
            TryDelete(pidFilePath);
        }

        return new DaemonLock(stream, pidFilePath, pid);
    }

    /// <summary>
    /// True when a daemon currently holds the lock. This is the hook's probe — the Linux
    /// counterpart to the Windows hook probing <c>Global\ImrdyMonitor</c> to decide whether
    /// to spawn the tray — and unlike <see cref="TryAcquire"/> it records no PID and leaves
    /// no trace, which matters on a path that runs hundreds of times per session.
    /// </summary>
    public static bool IsRunning(string lockFilePath)
    {
        if (!File.Exists(lockFilePath))
        {
            return false;
        }

        try
        {
            using var _ = new FileStream(lockFilePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            return false;
        }
        catch (IOException)
        {
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // Cannot tell. Reporting "running" is the safe answer: a spurious second daemon
            // would be refused by the lock anyway, but spawning one on every hook event is
            // the cost this probe exists to avoid.
            return true;
        }
    }

    /// <summary>
    /// Reads the recorded PID, for an operator or a stop script. Returns null when the file
    /// is missing or does not hold a number. A returned PID says only what was written,
    /// never that the process is alive — liveness is the lock's job, and the only way to ask
    /// it is <see cref="TryAcquire"/>.
    /// </summary>
    public static int? ReadOwnerPid(string lockFilePath)
    {
        try
        {
            var text = File.ReadAllText(PidFilePathFor(lockFilePath));
            return int.TryParse(text.Trim(), out var pid) ? pid : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Releases the lock and clears the PID file, so a reader cannot mistake a dead owner's
    /// PID for a live daemon. The lock file itself is left on disk deliberately: deleting it
    /// would race a daemon starting in the same instant, which would then hold a lock on an
    /// unlinked inode while a third process created and locked a fresh file at the same path.
    /// </summary>
    public void Dispose()
    {
        TryDelete(_pidFilePath);
        _stream.Dispose();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort — a stale PID file is readable but never authoritative.
        }
    }
}
