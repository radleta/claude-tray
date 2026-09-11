using System.Text.Json.Serialization;

namespace Imrdy.Core.Publishing;

/// <summary>
/// One registered link in publishers.json. On a publisher this is a receiver to deliver to;
/// on a receiver it is a publisher whose sessions land here, carrying that publisher's
/// desktop mapping and mute.
/// </summary>
public sealed record PublisherEntry
{
    /// <summary>
    /// Machine name. What the tray labels, what the desktop mapping keys on, and what
    /// <c>origin_machine</c> carries. WSL distros default to <c>&lt;hostname&gt;-&lt;distro&gt;</c>.
    /// </summary>
    [JsonPropertyName("name")]
    public required string Name { get; init; }

    /// <summary>
    /// Where to reach this link: <c>host:port</c> for a TCP sink, or a directory path for a
    /// file sink writing through a mount.
    /// <para>
    /// Null means there is nothing to dial, and that is a legitimate registration rather than
    /// a misconfiguration: a receiver registers a publisher to carry that publisher's
    /// <see cref="DesktopIndex"/> and <see cref="Muted"/>, and must not turn around and dial
    /// it. The endpoint <em>is</em> the direction — a record that carries one is dialed, a
    /// record that omits one is receive-only. (The user's ruling r-1; see
    /// <c>scratch/cross-machine-publish/facts.md</c>.)
    /// </para>
    /// </summary>
    [JsonPropertyName("endpoint")]
    public string? Endpoint { get; init; }

    /// <summary>
    /// The local virtual desktop this machine's sessions activate to (D18) — one number per
    /// machine, not per session. Null means no mapping is configured.
    /// </summary>
    [JsonPropertyName("desktop_index")]
    public int? DesktopIndex { get; init; }

    /// <summary>Per-publisher escape valve from the notification rules remote sessions otherwise share with local ones (D22).</summary>
    [JsonPropertyName("muted")]
    public bool Muted { get; init; }

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; } = true;
}
