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
/// </summary>
public static class ConnectionsViewModelBuilder
{
    public static ConnectionsViewModel Build(
        PublisherConfig publishers,
        IReadOnlyList<SinkHealth> outbound,
        IReadOnlyList<SinkHealth> inbound,
        string machineName,
        bool listenEnabled,
        int listenPort,
        bool authKeyConfigured,
        DateTimeOffset now)
    {
        var outboundByName = ByName(outbound);
        var inboundByName = ByName(inbound);

        var rows = new List<ConnectionRow>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var entry in publishers.Publishers)
        {
            if (!seen.Add(entry.Name)) continue;

            var outboundHealth = Lookup(outboundByName, entry.Name);
            var inboundHealth = Lookup(inboundByName, entry.Name);

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
}
