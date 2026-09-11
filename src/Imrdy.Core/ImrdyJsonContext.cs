using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Imrdy.Core.Diagnostics;
using Imrdy.Core.Display;
using Imrdy.Core.Graphics;
using Imrdy.Core.Hooks;
using Imrdy.Core.Publishing;
using Imrdy.Core.Sound;
using Imrdy.Core.State;
using Imrdy.Core.Workspace;

namespace Imrdy.Core;

[JsonSerializable(typeof(StateFileModel))]
[JsonSerializable(typeof(HookEventModel))]
[JsonSerializable(typeof(BackgroundTaskModel))]
[JsonSerializable(typeof(List<BackgroundTaskModel>))]
[JsonSerializable(typeof(PackJson))]
[JsonSerializable(typeof(GraphicsPackJson))]
[JsonSerializable(typeof(GraphicsPackStateJson))]
[JsonSerializable(typeof(ImrdyConfig))]
[JsonSerializable(typeof(WorkspaceConfig))]
[JsonSerializable(typeof(WorkspaceEntry))]
[JsonSerializable(typeof(List<string>))]
[JsonSerializable(typeof(WorkspaceDashboardViewModel))]
[JsonSerializable(typeof(DashboardViewModel))]
[JsonSerializable(typeof(List<DisplayItem>))]
[JsonSerializable(typeof(HookAccumulation))]
[JsonSerializable(typeof(RecentToolEntry))]
[JsonSerializable(typeof(GitInfo))]
[JsonSerializable(typeof(RateLimits))]
[JsonSerializable(typeof(FleetItem))]
[JsonSerializable(typeof(JsonDocument))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(JsonNode))]
[JsonSerializable(typeof(JsonObject))]
[JsonSerializable(typeof(JsonArray))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(IReadOnlyDictionary<string, string>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(InspectRequest))]
[JsonSerializable(typeof(InspectResponse))]
[JsonSerializable(typeof(RenderResult))]
[JsonSerializable(typeof(InspectResult))]
[JsonSerializable(typeof(FormGeometry))]
[JsonSerializable(typeof(LayoutNode))]
[JsonSerializable(typeof(DiagnosticFinding))]
[JsonSerializable(typeof(DiagnosticsConfig))]
[JsonSerializable(typeof(NetworkConfig))]
[JsonSerializable(typeof(PublisherConfig))]
[JsonSerializable(typeof(WireFrame))]
[JsonSerializable(typeof(PublisherEntry))]
[JsonSerializable(typeof(SinkHealth))]
[JsonSerializable(typeof(List<SinkHealth>))]
[JsonSerializable(typeof(ConnectionRow))]
[JsonSerializable(typeof(ConnectionsViewModel))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class ImrdyJsonContext : JsonSerializerContext
{
    /// <summary>
    /// Indented serializer options using the source-generated context (trim-safe).
    /// </summary>
    private static JsonSerializerOptions? _indented;
    internal static JsonSerializerOptions Indented => _indented ??= new(Default.Options) { WriteIndented = true };
}
