using Imrdy.Core.State;

namespace Imrdy.Core.Publishing;

/// <summary>
/// What every sink on this publisher shares, threaded from the daemon or the tray through
/// <see cref="SinkRegistry"/> to <see cref="SinkFactory"/>. Per-link values live on
/// <see cref="PublisherEntry"/> instead.
/// </summary>
/// <param name="ResolveOriginMachine">
/// The name this publisher stamps as <c>origin_machine</c> (D5). Shared by every sink here —
/// not to be confused with <see cref="SinkHealth.Name"/>, which identifies the link. Resolved
/// per use rather than captured, for the same reason the auth key is: <c>network.machineName</c>
/// live-reloads (D25) and a sink outlives many reloads, so a captured name would keep stamping
/// the old one until restart while the far end saw the machine twice under two names.
/// </param>
/// <param name="ResolveAuthKey">
/// Read at dial time rather than captured, because <c>config.json</c> live-reloads (D25) and a
/// sink outlives many reloads: a key captured at construction would stay stale until restart.
/// </param>
/// <param name="Reader">Reads and writes state files for the file sink.</param>
/// <param name="LocalSnapshot">
/// Every session this machine owns, for the full snapshot a TCP sink sends at connect (D12).
/// Resolved per connect, so a reconnect delivers current state rather than what was current
/// when the sink was built.
/// </param>
public sealed record SinkContext(
    Func<string> ResolveOriginMachine,
    Func<string?> ResolveAuthKey,
    StateFileReader Reader,
    Func<IReadOnlyList<StateFileModel>> LocalSnapshot);
