using Microsoft.Extensions.Logging;

namespace Imrdy.Core.Publishing;

/// <summary>
/// The daemon's event loop. <c>src/Imrdy.Linux/Program.cs</c> exits after each invocation
/// like a CLI tool and has no <c>Application.Run</c> analogue, so this supplies one: take
/// the single-instance lock, send the connect snapshot, then drain coalesced filesystem
/// events until cancelled.
/// <para>
/// This lives in <c>Imrdy.Core</c>, not <c>Imrdy.Linux</c>, for the same reason
/// <c>HookServiceBuilder</c> does — so it is reachable from both target frameworks and so it
/// can be unit-tested on the machine the suite actually runs on.
/// </para>
/// </summary>
public sealed class DaemonHost
{
    private readonly SessionPublisher _publisher;
    private readonly SessionChangeQueue _queue;
    private readonly TimeSpan _drainInterval;
    private readonly ILogger _logger;
    private readonly HeartbeatWriter? _heartbeat;

    /// <param name="heartbeat">
    /// The file-sink liveness signal, beaten off this loop's tick. Null in tests that are only
    /// exercising the drain; the daemon always supplies one.
    /// </param>
    public DaemonHost(
        SessionPublisher publisher,
        SessionChangeQueue queue,
        TimeSpan drainInterval,
        ILogger logger,
        HeartbeatWriter? heartbeat = null)
    {
        _publisher = publisher;
        _queue = queue;
        _drainInterval = drainInterval;
        _logger = logger;
        _heartbeat = heartbeat;
    }

    /// <summary>
    /// Sends the full snapshot, then drains until cancelled. Returns when the token trips;
    /// cancellation is the normal way this ends, so it is not surfaced as an error.
    /// </summary>
    public async Task RunAsync(CancellationToken cancellationToken)
    {
        _logger.LogInformation("imrdy daemon starting");

        try
        {
            // Beside the snapshot, not only inside the loop: a receiver that read the
            // heartbeat directory during the first period would otherwise see the previous
            // run's stale beat and call a daemon that just started disconnected.
            _heartbeat?.WriteIfDue(DateTimeOffset.UtcNow);

            await _publisher.PublishSnapshotAsync(cancellationToken).ConfigureAwait(false);

            using var timer = new PeriodicTimer(_drainInterval);
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                // Unconditional, and before the drain: the beat says this process is alive,
                // which is true whether or not any session changed. Gating it on publishing
                // would make a quiet publisher read as a gone one — the inference-from-silence
                // failure this whole mechanism is shaped to avoid.
                _heartbeat?.WriteIfDue(DateTimeOffset.UtcNow);

                await DrainOnceAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown.
        }

        _logger.LogInformation("imrdy daemon stopped");
    }

    /// <summary>
    /// Emits one coalesced batch. Public so the drain can be stepped directly in a test
    /// rather than by waiting on a timer.
    /// </summary>
    public Task DrainOnceAsync(CancellationToken cancellationToken) =>
        _publisher.DrainAsync(_queue.Drain(), cancellationToken);
}
