using System.Text.Json.Serialization;
using Imrdy.Core.State;

namespace Imrdy.Core.Publishing;

/// <summary>
/// One newline-delimited JSON frame on the TCP wire. The publisher dials and the receiver
/// listens (D8), so every frame travels publisher → receiver; nothing comes back (D4).
/// <para>
/// The three frame shapes <c>idea.md</c> specifies — <c>hello</c>, <c>session</c> and
/// <c>remove</c> — are one record with a <c>type</c> discriminator rather than three, so the
/// receiver reads a line once instead of sniffing the type and re-parsing, and so the field
/// names have exactly one definition. The fields not carried by a given type are omitted from
/// the JSON, which reproduces the three shapes on the wire verbatim.
/// </para>
/// <para>
/// Unknown fields are ignored on read, which is what makes additive changes safe within a
/// schema major (D28). An unknown <c>type</c> is ignored the same way; a differing major is
/// refused outright.
/// </para>
/// </summary>
public sealed record WireFrame
{
    /// <summary>Frame discriminator. See <see cref="WireFrameTypes"/>.</summary>
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    /// <summary>
    /// Schema major, on the <c>hello</c> frame only. Spelled the way
    /// <c>InspectResponse.SchemaVersion</c> spells it, so the two protocols read as siblings.
    /// </summary>
    [JsonPropertyName("schemaVersion")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SchemaVersion { get; init; }

    /// <summary>The publishing machine's name, on the <c>hello</c> frame only.</summary>
    [JsonPropertyName("machine")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Machine { get; init; }

    /// <summary>The shared key (D10), on the <c>hello</c> frame only. Null when none is configured.</summary>
    [JsonPropertyName("key")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Key { get; init; }

    /// <summary>
    /// The session state exactly as the hooks wrote it plus <c>origin_machine</c> (D11), on
    /// the <c>session</c> frame only.
    /// </summary>
    [JsonPropertyName("state")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public StateFileModel? State { get; init; }

    /// <summary>The session whose state file disappeared, on the <c>remove</c> frame only.</summary>
    [JsonPropertyName("session_id")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SessionId { get; init; }

    public static WireFrame Hello(string machine, string? key) => new()
    {
        Type = WireFrameTypes.Hello,
        SchemaVersion = WireProtocol.SchemaVersion,
        Machine = machine,
        Key = key,
    };

    public static WireFrame Session(StateFileModel state) => new()
    {
        Type = WireFrameTypes.Session,
        State = state,
    };

    public static WireFrame Remove(string sessionId) => new()
    {
        Type = WireFrameTypes.Remove,
        SessionId = sessionId,
    };
}

/// <summary>The <c>type</c> values defined in schema major 1.</summary>
public static class WireFrameTypes
{
    public const string Hello = "hello";
    public const string Session = "session";
    public const string Remove = "remove";
}
