using System.Text.Json.Serialization;
using Imrdy.Core.Hooks;

namespace Imrdy.Core.State;

/// <summary>
/// 12-field JSON model matching the session state file format.
/// Written by hook, read by monitor.
/// </summary>
public sealed record StateFileModel
{
    internal const int MaxMessageLength = 120;

    [JsonPropertyName("session_id")]
    public required string SessionId { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("project")]
    public required string Project { get; init; }

    [JsonPropertyName("cwd")]
    public required string Cwd { get; init; }

    [JsonPropertyName("hook_event")]
    public required string HookEvent { get; init; }

    [JsonPropertyName("notification_type")]
    public string NotificationType { get; init; } = "";

    [JsonPropertyName("last_message")]
    public string LastMessage { get; init; } = "";

    [JsonPropertyName("claude_pid")]
    public int? ClaudePid { get; init; }

    [JsonPropertyName("sound_pack")]
    public string? SoundPack { get; init; }

    [JsonPropertyName("icon_style")]
    public string? IconStyle { get; init; }

    [JsonPropertyName("tool_name")]
    public string? ToolName { get; init; }

    [JsonPropertyName("desktop_index")]
    public int? DesktopIndex { get; init; }

    [JsonPropertyName("timestamp")]
    public DateTimeOffset Timestamp { get; init; }

    [JsonPropertyName("session_name")]
    public string? SessionName { get; init; }

    /// <summary>
    /// The roster of work still running, as measured by the most recent `Stop` /
    /// `SubagentStop` hook event. `null` means unknown (no measurement carried by this
    /// write); `[]` means measured-empty (everything finished). The two are never
    /// normalised into each other — see <see cref="Imrdy.Core.Hooks.FieldPreservation"/>.
    /// </summary>
    [JsonPropertyName("running_tasks")]
    public IReadOnlyList<BackgroundTaskModel>? RunningTasks { get; init; }

    [JsonPropertyName("started_at")]
    public DateTimeOffset? StartedAt { get; init; }

    [JsonPropertyName("wsl_distro")]
    public string? WslDistro { get; init; }

    /// <summary>
    /// The publisher machine a remote session originated on. `null` means the session
    /// originated on this machine — which is also the publisher's no-re-publish guard,
    /// so no hook on either side ever writes it. The publisher stamps it on the wire at
    /// emit time; the receiver writes it during ingest. It is receiver-owned at the
    /// ingest seam and therefore has no <see cref="Imrdy.Core.Hooks.FieldPreservation"/>
    /// entry: the hook seam has nothing to preserve.
    /// </summary>
    [JsonPropertyName("origin_machine")]
    public string? OriginMachine { get; init; }

    /// <summary>
    /// Truncates a message to the maximum allowed length.
    /// Port of truncateMessage() from hook-lib.mjs.
    /// </summary>
    public static string TruncateMessage(string? message, int maxLength = MaxMessageLength)
    {
        if (string.IsNullOrEmpty(message))
        {
            return "";
        }

        return message.Length <= maxLength ? message : message[..maxLength];
    }
}
