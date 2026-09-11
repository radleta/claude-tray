namespace Imrdy.Core.State;

/// <summary>
/// The one definition of "would this session still be displayed?", answered from the state
/// file alone. Added by the user's ruling r-4: the publisher emits only the sessions it would
/// itself still display, so a machine's accumulated backlog of ended sessions stops flooding
/// every receiver with chips nobody would show.
/// <para>
/// It answers from <c>status</c> and nothing else, because that is the only term of the tray's
/// display predicate a state file can carry. The full predicate in
/// <c>TrayApp.BuildDisplayItems</c> is <c>!Dismissed &amp;&amp; (RemoveAfter is null ||
/// RemoveAfter > UtcNow) &amp;&amp; Status != "end"</c>; the first two live in memory on
/// <c>SessionEntry</c> in <c>Imrdy.Windows</c> and are never serialized. <c>RemoveAfter</c> is
/// worse than merely absent — it is a <em>receiver-side</em> grace deadline set when a state
/// file disappears, and on a publisher the file is present by definition, so it has no meaning
/// there at all. This is the shared half, and it must not grow to imitate the other two.
/// </para>
/// <para>
/// <b>Do not add an age term here.</b> One was built (a 60-minute window reusing
/// <c>MonitorOptions.StaleMinutes</c>) and the user rejected it outright under ruling r-5:
/// <em>a session that has gone quiet is exactly the state imrdy exists to tell the user
/// about</em>, so dropping it from the publish path inverts the product. That reasoning
/// condemns every age-based variant rather than one threshold, which is why no number is the
/// right number. A publisher with too many live sessions is a presentation problem for the
/// tray and the overlay to solve, and an operator with a legacy backlog already has
/// <c>Clear sessions</c>. The full record is in <c>scratch/cross-machine-publish/facts.md</c>
/// under <c>f-r5-age</c>.
/// </para>
/// </summary>
public static class SessionDisplayFilter
{
    /// <summary>
    /// Whether the state file alone rules this session out of being drawn — that is, whether it
    /// has ended. Ordinal, matching the <c>Status != "end"</c> comparisons in <c>TrayApp</c>.
    /// <para>
    /// True is a <em>maybe</em>, not a promise: a receiver that has dismissed the session draws
    /// nothing for it either, and the two terms that say so are not in the file (see the type's
    /// own remarks). The overclaim is deliberately in this direction — a publisher that sends
    /// slightly too much is corrected by the receiver's own predicate, where the reverse loses
    /// the user a session.
    /// </para>
    /// </summary>
    public static bool WouldDisplay(StateFileModel state) =>
        !string.Equals(state.Status, "end", StringComparison.Ordinal);
}
