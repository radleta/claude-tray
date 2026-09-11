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
    /// plus <see cref="StateFileModel.OriginMachine"/> — no projection and no per-receiver
    /// personalization (D11). What the receiver keeps is the receiver's business.
    /// <para>
    /// The publisher applies exactly one filter before reaching a sink, and it is uniform
    /// across every receiver: a snapshot carries only the sessions the publisher would
    /// itself still display (<see cref="SessionPublisher.SnapshotActionFor"/>, the user's
    /// ruling r-4). D11's concern is that a publisher cannot know what a given receiver wants, and
    /// a session dead on one receiver is dead on all of them, so that filter does not reopen
    /// it. Anything narrower — per-receiver filtering, field projection — remains forbidden.
    /// </para>
    /// </summary>
    Task PublishAsync(StateFileModel state, CancellationToken cancellationToken);

    /// <summary>
    /// Mirrors a local state file's disappearance to the receiver, which flows through the
    /// receiver's existing grace-period delete path unchanged (D15).
    /// <para>
    /// A snapshot sends this for a session that has <em>ended</em> as well, which is how a
    /// receiver that missed the <c>end</c> delta converges (<see cref="SnapshotAction.Retire"/>).
    /// So it must be safe to send for a session the receiver may have dropped already, or never
    /// held: both implementations reduce that to a delete of a file that is not there.
    /// </para>
    /// </summary>
    Task RemoveAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Current health of this link, for <c>imrdy links</c> and the connections window.</summary>
    SinkHealth Health { get; }
}
