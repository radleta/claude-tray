using System.Diagnostics;
using Imrdy.Core;
using Imrdy.Core.Hooks;
using Imrdy.Core.Publishing;
using Microsoft.Extensions.Logging;

namespace Imrdy.Linux;

internal sealed class LinuxHookEnvironment(ILogger logger) : IHookEnvironment
{
    public int? ResolveTerminalPid(int currentPid, string sessionId) => null;

    /// <summary>
    /// Spawns the publisher daemon if it is not already up (D7) — the Linux counterpart to
    /// the Windows hook probing the tray mutex.
    /// <para>
    /// Two guards keep this cheap and quiet on the path that runs hundreds of times per
    /// session. Nothing is spawned unless at least one link is registered, so a Linux box
    /// that publishes nowhere never starts a daemon that would have nothing to do. And the
    /// probe never blocks: it takes no lock, waits on no child, and logs rather than rethrows
    /// every failure, because a hook that stalls stalls the user's Claude session.
    /// </para>
    /// </summary>
    public void EnsureTrayRunning()
    {
        try
        {
            if (DaemonLock.IsRunning(ImrdyPaths.DaemonLock))
            {
                return;
            }

            if (!new PublisherStore(ImrdyPaths.Publishers).Load().Publishers.Any(p => p.Enabled))
            {
                return;
            }

            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe))
            {
                logger.LogWarning("Cannot determine process path for daemon spawn");
                return;
            }

            // Launched through `sh` rather than directly so the daemon's stdio goes to
            // /dev/null and the trailing `&` orphans it to init. Inheriting this process's
            // stdout would let daemon output land in the JSON stream Claude Code reads;
            // redirecting into a .NET pipe nobody drains would block the daemon once that
            // pipe filled.
            var quoted = "'" + exe.Replace("'", @"'\''") + "'";
            Process.Start(new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { "-c", $"{quoted} daemon >/dev/null 2>&1 &" },
                UseShellExecute = false,
            });
        }
        catch (Exception ex)
        {
            // Never fail the Claude session over a daemon that did not start, but never lose
            // the reason either — matching TraySpawner.EnsureRunning, the Windows twin. This is
            // the only diagnostic a WSL distro that silently publishes nothing ever gets:
            // `imrdy links` is a CLI process holding no health tables (D26), so it prints the
            // same rows whether the daemon is up or down. The next hook event retries.
            logger.LogWarning(ex, "Daemon spawn failed");
        }
    }

    public string NormalizeCwd(string? cwd) => cwd ?? "";

    public void OnSessionEnd(string sessionId) { /* no-op — no PID cache on Linux */ }

    public string? GetWslDistro() => Environment.GetEnvironmentVariable("WSL_DISTRO_NAME");
}
