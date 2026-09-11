using Microsoft.Extensions.Logging;

namespace Imrdy.Core.Publishing;

/// <summary>
/// Builds the sink one registered link needs. The endpoint's own shape says which kind it
/// is, so <see cref="PublisherEntry"/> carries no discriminator field to fall out of sync
/// with it: <c>host:port</c> is a receiver to dial, and a rooted path is a mount to write
/// through.
/// </summary>
public static class SinkFactory
{
    /// <summary>
    /// Creates the sink for one entry, or returns null when the entry is disabled or its
    /// endpoint is not usable. An unusable endpoint is always accompanied by a logged reason —
    /// a link that silently does nothing is the failure mode D26 exists to prevent. A disabled
    /// entry is the one silent null: the operator asked for it, and the connections window
    /// renders that row as <c>disabled</c> rather than leaving it unexplained.
    /// </summary>
    public static ISessionSink? TryCreate(PublisherEntry entry, SinkContext context, ILogger logger)
    {
        if (!entry.Enabled)
        {
            return null;
        }

        if (entry.Endpoint is null)
        {
            // r-1: no endpoint means receive-only. The record exists to carry this publisher's
            // desktop mapping and mute, and there is nothing to dial — so this is the second
            // silent null, and deliberately silent: warning here would put a false alarm in the
            // log on every reconcile, for a link the operator configured exactly as intended.
            return null;
        }

        var endpoint = entry.Endpoint.Trim();

        if (endpoint.Length == 0)
        {
            logger.LogWarning("Publisher {Name} has an empty endpoint and was skipped", entry.Name);
            return null;
        }

        // Tested before the rooted-path branch: a Windows path such as C:\Users also carries a
        // colon, and only the port test tells the two apart.
        if (TryParseHostPort(endpoint, out var host, out var port))
        {
            return new TcpSink(entry.Name, host, port, context, logger);
        }

        if (Path.IsPathRooted(endpoint))
        {
            return new FileSink(endpoint, entry.Name, context.ResolveOriginMachine, context.Reader, logger);
        }

        logger.LogWarning(
            "Publisher {Name} endpoint '{Endpoint}' is neither host:port nor a rooted directory path; the link was skipped",
            entry.Name,
            endpoint);

        return null;
    }

    /// <summary>
    /// True when this endpoint would build a <see cref="FileSink"/>. Answered by the same two
    /// branches, in the same order, that <see cref="TryCreate"/> uses, so a link cannot be
    /// reported as one kind and built as the other.
    /// </summary>
    public static bool IsFileEndpoint(string? endpoint)
    {
        if (string.IsNullOrWhiteSpace(endpoint)) return false;

        var trimmed = endpoint.Trim();
        return !TryParseHostPort(trimmed, out _, out _) && Path.IsPathRooted(trimmed);
    }

    /// <summary>
    /// Splits <c>host:port</c> at the last colon so an IPv6 literal in brackets survives. A
    /// port outside the legal range is not a host:port at all, which is what keeps
    /// <c>C:\Users\...</c> falling through to the path branch.
    /// </summary>
    private static bool TryParseHostPort(string endpoint, out string host, out int port)
    {
        host = string.Empty;
        port = 0;

        var separator = endpoint.LastIndexOf(':');
        if (separator <= 0 || separator == endpoint.Length - 1)
        {
            return false;
        }

        if (!int.TryParse(endpoint[(separator + 1)..], out port)
            || port < NetworkConfig.MinListenPort
            || port > NetworkConfig.MaxListenPort)
        {
            return false;
        }

        host = endpoint[..separator].Trim('[', ']');
        return host.Length > 0;
    }
}
