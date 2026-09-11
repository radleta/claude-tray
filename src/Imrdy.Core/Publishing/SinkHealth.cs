namespace Imrdy.Core.Publishing;

/// <summary>
/// The four states a link can be in. <see cref="FileSink"/> is deliberately not a
/// flavour of <see cref="Connected"/>: a file sink has no connection to be healthy,
/// and reporting one as connected would be a lie the operator acts on (D27).
/// </summary>
public enum SinkState
{
    Connected,
    Dialing,
    Failed,
    FileSink,
}

/// <summary>
/// A point-in-time snapshot of one link's health. This is the single shape rendered by
/// both <c>imrdy links</c> and the connections window, in both directions — outbound
/// sinks on a publisher and inbound publishers on a receiver.
/// </summary>
/// <param name="Name">The publisher or receiver this link addresses.</param>
/// <param name="State">Current link state.</param>
/// <param name="LastSuccessAt">When this link last delivered, or null if it never has.</param>
/// <param name="LastError">
/// The most recent failure reason, for display. Null once a delivery succeeds. This is a
/// string for the UI to render and is never a substitute for the logged exception.
/// </param>
/// <param name="SessionCount">Sessions this link has delivered state for since it came up.</param>
public sealed record SinkHealth(
    string Name,
    SinkState State,
    DateTimeOffset? LastSuccessAt,
    string? LastError,
    int SessionCount)
{
    /// <summary>
    /// True when the link is in a state the operator should act on, and what
    /// <see cref="LinksReport.ExitCode"/> keys on. Reachable from <c>imrdy links</c> only when
    /// a running tray answered its live query (r-2) — a CLI process holds no sinks and no
    /// listener, so the records-only fallback it builds for itself has both health slots null
    /// and can never be failed.
    /// </summary>
    public bool IsFailed => State == SinkState.Failed;
}
