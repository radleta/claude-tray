using Imrdy.Core.State;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Imrdy.Core.Publishing;

/// <summary>
/// The <c>imrdy daemon</c> entry point: take the single-instance lock, wire the pipeline,
/// and run until stopped. Returns an exit code rather than calling <c>Environment.Exit</c>,
/// so <c>Program.Main</c> stays the only place that decides the process's fate.
/// </summary>
public static class DaemonCommand
{
    /// <summary>Exit code when another daemon already holds the lock. Not a failure.</summary>
    public const int ExitAlreadyRunning = 0;

    private static readonly TimeSpan DrainInterval = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Runs the daemon. <paramref name="cancellationToken"/> is the stop signal; the caller
    /// wires it to SIGINT and SIGTERM so a distro shutdown releases the lock cleanly rather
    /// than relying on the kernel to do it.
    /// </summary>
    public static async Task<int> RunAsync(
        ServiceProvider services,
        string? wslDistro,
        CancellationToken cancellationToken)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("DaemonCommand");

        using var heldLock = DaemonLock.TryAcquire(ImrdyPaths.DaemonLock);
        if (heldLock is null)
        {
            var owner = DaemonLock.ReadOwnerPid(ImrdyPaths.DaemonLock);
            logger.LogInformation(
                "imrdy daemon is already running (pid {Pid}); this invocation is exiting",
                owner?.ToString() ?? "unknown");
            return ExitAlreadyRunning;
        }

        var machineName = MachineNameResolver.Resolve(
            ConfigReader.Read().Network.MachineName,
            Environment.MachineName,
            wslDistro);

        var reader = services.GetRequiredService<StateFileReader>();
        var store = services.GetRequiredService<PublisherStore>();

        // AuthKey and the snapshot are both resolved lazily, so a config edit or a session that
        // started after the daemon did is visible to the next dial rather than to the next restart.
        var sinkContext = new SinkContext(
            () => machineName,
            () => ConfigReader.Read().Network.AuthKey,
            reader,
            () => reader.ReadAllStateFiles(ImrdyPaths.Sessions)
                .Where(SessionPublisher.IsLocallyOwned)
                .ToList());

        using var registry = new SinkRegistry(store, sinkContext, logger);
        var queue = new SessionChangeQueue();
        var publisher = new SessionPublisher(ImrdyPaths.Sessions, reader, registry.Current, logger);

        using var watcher = new SessionDirectoryWatcher(ImrdyPaths.Sessions, queue);
        watcher.Start();

        logger.LogInformation(
            "imrdy daemon publishing as {MachineName} (pid {Pid}) from {SessionsDir}",
            machineName,
            heldLock.OwnerPid,
            ImrdyPaths.Sessions);

        // The links are re-read per beat rather than captured, so a file link added through the
        // receiver's connections window starts beating without a daemon restart (D25).
        var heartbeat = new HeartbeatWriter(
            () => store.Load().Publishers,
            () => machineName,
            logger);

        await new DaemonHost(publisher, queue, DrainInterval, logger, heartbeat)
            .RunAsync(cancellationToken)
            .ConfigureAwait(false);

        return 0;
    }
}
