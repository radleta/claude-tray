using Imrdy.Core.State;

namespace Imrdy.Core.Publishing;

/// <summary>
/// The seam D3 turns on: one publish pipeline, two sinks. A WSL publisher writes the
/// state file through <c>/mnt/c</c> into the host's sessions directory; every other
/// publisher sends it over TCP. Neither implementation references WinForms.
/// </summary>
public interface ISessionSink
{
    /// <summary>
    /// Delivers one session's state. The state arrives exactly as the hooks wrote it,
    /// plus <see cref="StateFileModel.OriginMachine"/> — no projection, no filtering, no
    /// per-receiver personalization (D11). What the receiver keeps is the receiver's
    /// business.
    /// </summary>
    Task PublishAsync(StateFileModel state, CancellationToken cancellationToken);

    /// <summary>
    /// Mirrors a local state file's disappearance to the receiver, which flows through the
    /// receiver's existing grace-period delete path unchanged (D15).
    /// </summary>
    Task RemoveAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Current health of this link, for <c>imrdy links</c> and the connections window.</summary>
    SinkHealth Health { get; }
}
