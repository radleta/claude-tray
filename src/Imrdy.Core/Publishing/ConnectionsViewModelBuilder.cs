using Imrdy.Core.Time;

namespace Imrdy.Core.Publishing;

/// <summary>
/// Joins <c>publishers.json</c> to both health tables into the one shape the connections
/// window and <c>imrdy links</c> render. Pure and deterministic: every value it needs is a
/// parameter, <c>now</c> included, so a fixture round-trips through it and a test needs no
/// store, no socket and no clock.
/// <para>
/// The join is deliberately outer on all three inputs. A registered machine that has never
/// built a sink and never connected still gets a row — that is a disabled or misconfigured
/// link, and dropping it would make the one thing the operator came to look at invisible.
/// A machine that connected inbound without a record also gets a row, because a receiver
/// holds no allow-list (D24) and an unexpected publisher is exactly what the window is for.
/// </para>
/// <para>
/// <b>Inbound is two transports, not one.</b> <paramref name="inbound"/> comes from
/// <c>WireListener</c> and therefore knows only TCP publishers; a file-sink publisher opens no
/// socket and would be invisible here no matter how hard it was delivering. Its beats arrive as
/// <see cref="MachineBeat"/>s instead and join the same <c>seen</c> set in the same pass order,
/// so a machine known by a record, a socket and a beat still produces exactly one row. See
/// <c>facts.md</c> <c>f-filesink-no-socket</c>.
/// </para>
/// </summary>
public static class ConnectionsViewModelBuilder
{
    public static ConnectionsViewModel Build(
        PublisherConfig publishers,
        IReadOnlyList<SinkHealth> outbound,
        IReadOnlyList<SinkHealth> inbound,
        IReadOnlyList<MachineBeat> heartbeats,
        string machineName,
        bool listenEnabled,
        int listenPort,
        bool authKeyConfigured,
        DateTimeOffset now)
    {
        var outboundByName = ByName(outbound);
        var inboundByName = ByName(inbound);
        var beatsByToken = ByToken(heartbeats);

        var rows = new List<ConnectionRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var beatsClaimed = new HashSet<string>(StringComparer.Ordinal);

        foreach (var entry in publishers.Publishers)
        {
            if (!seen.Add(entry.Name)) continue;

            var outboundHealth = Lookup(outboundByName, entry.Name);

            // The beat is claimed whether or not it is used, so a record and a beat for one
            // machine can never produce two rows. A live socket wins when both exist: it is the
            // stronger signal, and a beat beside it would only ever have come from the same box.
            var beatHealth = ClaimBeat(beatsByToken, beatsClaimed, entry.Name, now);
            var inboundHealth = Lookup(inboundByName, entry.Name) ?? beatHealth;

            rows.Add(new ConnectionRow(
                Name: entry.Name,
                Endpoint: entry.Endpoint,
                IsRegistered: true,
                Enabled: entry.Enabled,
                Muted: entry.Muted,
                DesktopIndex: entry.DesktopIndex,
                Outbound: outboundHealth,
                Inbound: inboundHealth,
                LastDelivery: ConnectionRowFormatter.LastDelivery(outboundHealth, inboundHealth, now)));
        }

        // Inbound-only machines: connected here, no local record. Their defaults say what is
        // actually true of them — no endpoint to dial, no desktop mapping, not muted — rather
        // than borrowing a registered row's values.
        foreach (var health in inbound)
        {
            if (!seen.Add(health.Name)) continue;

            ClaimBeat(beatsByToken, beatsClaimed, health.Name, now);

            var outboundHealth = Lookup(outboundByName, health.Name);

            rows.Add(new ConnectionRow(
                Name: health.Name,
                Endpoint: null,
                IsRegistered: false,
                Enabled: true,
                Muted: false,
                DesktopIndex: null,
                Outbound: outboundHealth,
                Inbound: health,
                LastDelivery: ConnectionRowFormatter.LastDelivery(outboundHealth, health, now)));
        }

        // File-sink publishers: delivering here, no socket to be seen on and no local record.
        // Same shape as the loop above — an unregistered machine's defaults say what is true of
        // it — because it is the same case arriving over the other transport.
        foreach (var beat in heartbeats)
        {
            if (beatsClaimed.Contains(PublisherHeartbeat.TokenFor(beat.Name))) continue;
            if (!seen.Add(beat.Name)) continue;

            var health = BeatHealth(beat.Name, beat.BeatAt, now, beat.NameIsToken);
            var outboundHealth = Lookup(outboundByName, beat.Name);

            rows.Add(new ConnectionRow(
                Name: beat.Name,
                Endpoint: null,
                IsRegistered: false,
                Enabled: true,
                Muted: false,
                DesktopIndex: null,
                Outbound: outboundHealth,
                Inbound: health,
                LastDelivery: ConnectionRowFormatter.LastDelivery(outboundHealth, health, now),
                NameIsToken: beat.NameIsToken));
        }

        rows.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

        return new ConnectionsViewModel(machineName, listenEnabled, listenPort, authKeyConfigured, rows);
    }

    private static Dictionary<string, SinkHealth> ByName(IReadOnlyList<SinkHealth> health)
    {
        var map = new Dictionary<string, SinkHealth>(StringComparer.OrdinalIgnoreCase);
        foreach (var h in health)
        {
            // Last one wins rather than throwing: two links reporting one name is D24's
            // accepted "two publishers claiming the same machine name" case, and a duplicate
            // key must not take the whole window down.
            map[h.Name] = h;
        }
        return map;
    }

    private static SinkHealth? Lookup(Dictionary<string, SinkHealth> map, string name) =>
        map.TryGetValue(name, out var health) ? health : null;

    private static Dictionary<string, MachineBeat> ByToken(IReadOnlyList<MachineBeat> beats)
    {
        var map = new Dictionary<string, MachineBeat>(StringComparer.Ordinal);
        foreach (var beat in beats)
        {
            map[PublisherHeartbeat.TokenFor(beat.Name)] = beat;
        }
        return map;
    }

    /// <summary>
    /// Takes this machine's beat out of circulation and renders it as health, or null when it
    /// never beat. Matching is by token, never by name: <c>seen</c>'s case-insensitive
    /// comparison does not model the dot-to-underscore flattening
    /// <see cref="PublisherHeartbeat.TokenFor"/> applies, so <c>PC-Excalibur-Ubuntu-24.04</c>
    /// and its beat file would look like two different machines.
    /// </summary>
    private static SinkHealth? ClaimBeat(
        Dictionary<string, MachineBeat> beatsByToken,
        HashSet<string> claimed,
        string name,
        DateTimeOffset now)
    {
        var token = PublisherHeartbeat.TokenFor(name);
        if (!beatsByToken.TryGetValue(token, out var beat)) return null;

        claimed.Add(token);

        // The caller's name won — a record's or a hello's — so whatever the beat resolved to is
        // irrelevant here and the derived-name advisory must not ride along on a real name.
        return BeatHealth(name, beat.BeatAt, now, nameIsToken: false);
    }

    /// <summary>
    /// One beat as a <see cref="SinkHealth"/>. The state stays
    /// <see cref="SinkState.FileSink"/> even when the beat is stale: D27 says a file sink has no
    /// connection to be healthy, and <see cref="SinkState.Failed"/> is what
    /// <c>imrdy links</c> exits 1 on — a shell guard for dropped <em>links</em>, which this is
    /// not. Staleness is reported where the operator reads it instead: the last-delivery cell
    /// ages off <see cref="SinkHealth.LastSuccessAt"/>, and past
    /// <see cref="PublisherHeartbeat.StaleAfter"/> the last-error cell says so outright — the
    /// same moment the tray paints D20's disconnected treatment, from the same beat.
    /// <para>
    /// The last-error cell is also where a token-derived name says so. That cell is this row's one
    /// channel to the operator on both surfaces — `imrdy links` has no edit path at all, so it is
    /// the only place the CLI can warn — and it already carries the staleness advisory, which is
    /// no more a transport error than this is. Both can be true at once, so they compose.
    /// </para>
    /// </summary>
    private static SinkHealth BeatHealth(string name, DateTimeOffset beat, DateTimeOffset now, bool nameIsToken)
    {
        var notes = new List<string>(2);

        if (PublisherHeartbeat.IsStale(beat, now))
        {
            notes.Add($"no heartbeat for {RelativeTimeFormatter.FormatDuration(now - beat)} — publisher may be gone");
        }

        if (nameIsToken)
        {
            notes.Add(ConnectionRowFormatter.NameDerived);
        }

        return new SinkHealth(
            name,
            SinkState.FileSink,
            beat,
            notes.Count == 0 ? null : string.Join(" · ", notes),
            SessionCount: 0);
    }
}
