using System.Text.Json.Serialization;

namespace Imrdy.Core.Publishing;

/// <summary>Root record for ~/.imrdy/publishers.json.</summary>
public sealed record PublisherConfig
{
    [JsonPropertyName("publishers")]
    public List<PublisherEntry> Publishers { get; init; } = [];
}
