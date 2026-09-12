using System.Text.Json.Serialization;

namespace Imrdy.Core.Publishing;

/// <summary>
/// One machine's link, as both surfaces render it: the connections window (D26) and
/// <c>imrdy links</c>. One row per machine carrying <em>both</em> directions, because a link
/// is diagnosed by the pair — an outbound sink that is delivering while nothing arrives
/// inbound is a different fault from neither working, and two separate lists would make that
/// comparison the operator's job.
/// <para>
/// Property names go on the wire camelCased by <c>ImrdyJsonContext</c>'s global policy, like
/// the other two render view models. No explicit <c>[JsonPropertyName]</c> here: this is a
/// view model, not a file format such as <see cref="PublisherEntry"/>.
/// </para>
/// </summary>
/// <param name="Name">Machine name, as <c>origin_machine</c> carries it.</param>
/// <param name="Endpoint">
/// Where this link is reached, from <c>publishers.json</c>. Null for a machine that connected
/// inbound without a local record — legitimate, since a receiver holds no allow-list (D24).
/// </param>
/// <param name="IsRegistered">True when <c>publishers.json</c> carries a record for this machine.</param>
/// <param name="Enabled">The record's enabled flag. A disabled record builds no sink at all.</param>
/// <param name="Muted">D22's per-publisher notification escape valve.</param>
/// <param name="DesktopIndex">D18's per-publisher desktop mapping, or null when unset.</param>
/// <param name="Outbound">This machine's outbound sink health, or null when no sink exists for it.</param>
/// <param name="Inbound">This machine's inbound link health, or null when it has never connected here.</param>
/// <param name="LastDelivery">
/// The newer of the two directions' last success, already rendered relative to the moment the
/// view model was built — "4m ago", or "never". It is precomputed for the same reason
/// <c>WorkspaceDashboardViewModel.ActivityText</c> is: the surfaces have no clock, so a fixture
/// pins exactly what a render produces and the connections window's PNG is a visual seal
/// rather than a different image every run. The absolute timestamps stay on the
/// <see cref="SinkHealth"/> records for <c>imrdy links --json</c> to consume.
/// </param>
/// <param name="NameIsToken">
/// True only for a file-sink publisher whose beat nothing on this machine could name, where
/// <paramref name="Name"/> is the flattened filename token standing in (see
/// <see cref="MachineBeat.NameIsToken"/>). The row carries it because the name is not merely
/// rendered: the connections window seeds a <see cref="PublisherEntry"/>'s name from it, and a
/// record saved under a flattened name never matches the <c>origin_machine</c> that D18's desktop
/// mapping, D22's mute and clear-this-machine all join on. Defaulted, so every row built from a
/// record or from a <c>hello</c> — where the name came from the operator or from the publisher
/// itself — says false without restating it.
/// </param>
public sealed record ConnectionRow(
    string Name,
    string? Endpoint,
    bool IsRegistered,
    bool Enabled,
    bool Muted,
    int? DesktopIndex,
    SinkHealth? Outbound,
    SinkHealth? Inbound,
    string LastDelivery,
    bool NameIsToken = false)
{
    /// <summary>
    /// True when either direction is in a state the operator should act on. This is what
    /// <c>imrdy links</c> exits non-zero on. A <see cref="SinkState.FileSink"/> row is never
    /// failed: there is no connection to have lost (D27).
    /// </summary>
    [JsonIgnore]
    public bool IsFailed => Outbound?.IsFailed == true || Inbound?.IsFailed == true;
}

/// <summary>
/// The complete render contract for the connections window and <c>imrdy links</c>. Carries
/// precomputed values only — neither surface reads config, the store or the clock.
/// </summary>
/// <param name="MachineName">This machine's own name, as publishers see it.</param>
/// <param name="ListenEnabled">Whether this machine accepts inbound links at all (D9).</param>
/// <param name="ListenPort">The configured inbound port.</param>
/// <param name="AuthKeyConfigured">
/// Whether a shared key is set (D10). False means every peer the firewall lets through is
/// accepted, which both surfaces say out loud on the listening line: the fail-open case is
/// the one an operator reaches by turning the feature on with the minimum config edit, and
/// it is silent otherwise.
/// </param>
/// <param name="Rows">One row per known machine, sorted by name.</param>
public sealed record ConnectionsViewModel(
    string MachineName,
    bool ListenEnabled,
    int ListenPort,
    bool AuthKeyConfigured,
    IReadOnlyList<ConnectionRow> Rows);
