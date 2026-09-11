using Imrdy.Core.State;
using Imrdy.Core.Status;

namespace Imrdy.Windows.Models;

/// <summary>
/// In-memory model tracking a session's state, icon, and lifecycle.
/// </summary>
internal sealed class SessionEntry : IDisposable
{
    public required string SessionId { get; init; }
    public StateFileModel State { get; set; } = null!;
    public NotifyIcon? Icon { get; set; }
    public ContextMenuStrip? Menu { get; set; }

    /// <summary>Last time the user interacted with this session (click/focus). Used for aging.</summary>
    public DateTimeOffset LastSeenAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>When the current status was set. Used for tooltip age display.</summary>
    public DateTimeOffset StatusSince { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Time after which this session should be removed (grace period on SessionEnd).</summary>
    public DateTimeOffset? RemoveAfter { get; set; }

    /// <summary>Whether the user has dismissed this session via context menu.</summary>
    public bool Dismissed { get; set; }

    /// <summary>Assigned sound pack name.</summary>
    public string? SoundPack { get; set; }

    /// <summary>Assigned icon style (shape name or pack style). Null means use the global default.</summary>
    public string? IconStyle { get; set; }

    /// <summary>Assigned virtual desktop index.</summary>
    public int? DesktopIndex { get; set; }

    /// <summary>When this session was first started (from state file StartedAt or Timestamp fallback).
    /// Populated on first observation by TrayApp; never reset after that.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>Last computed aging tier. Used to avoid unnecessary icon updates.</summary>
    public int LastAgingTier { get; set; } = 0;

    /// <summary>Last rendered D20 disconnected state. Paired with <see cref="LastAgingTier"/>
    /// so the aging tick redraws when a publisher's link drops or returns without the tier
    /// having moved — otherwise the treatment would only appear at the next tier boundary.</summary>
    public bool LastDisconnected { get; set; }

    /// <summary>Timestamp of the last state file processed for this session.
    /// Used by sweep to skip re-processing unchanged state files.</summary>
    public DateTimeOffset? LastProcessedTimestamp { get; set; }

    /// <summary>Status to display: the lead status, except an idle lead whose stored roster still
    /// lists running work shows as "done" (teal). See <see cref="DisplayStatus"/>. Resolved from
    /// <see cref="StateFileModel.RunningTasks"/> alone, so it changes only when a hook writes a new
    /// roster.</summary>
    public string EffectiveStatus
        => DisplayStatus.Resolve(State.Status, State.RunningTasks);

    /// <summary>Last <see cref="EffectiveStatus"/> the drain tick acted on. Used to detect the
    /// teal → green transition, which the state file now announces via the <c>Stop</c> that carries
    /// an empty roster; the drain tick still owns the edge because it is the sole dwell driver for
    /// status changes.</summary>
    public string? LastEffectiveStatus { get; set; }

    public void Dispose()
    {
        Icon?.Visible = false;
        Icon?.Dispose();
        Icon = null;
        Menu?.Dispose();
        Menu = null;
    }
}
