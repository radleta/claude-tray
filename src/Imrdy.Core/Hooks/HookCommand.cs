using System.Text.Json;
using Imrdy.Core.State;
using Imrdy.Core.Status;
using Imrdy.Core.Validation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Imrdy.Core.Hooks;

/// <summary>
/// Implements the `imrdy hook` fast-path subcommand.
/// Reads Claude Code hook JSON from stdin, derives status, and writes an atomic state file.
/// </summary>
internal static class HookCommand
{
    /// <summary>
    /// Runs the hook command. Reads JSON from the provided TextReader (stdin),
    /// processes the hook event, and writes the state file.
    /// Returns exit code 0 on success, 1 on error.
    /// </summary>
    public static int Run(ServiceProvider services, TextReader stdin, IHookEnvironment hookEnvironment)
    {
        var logger = services.GetRequiredService<ILoggerFactory>().CreateLogger("HookCommand");

        string? input;
        try
        {
            input = stdin.ReadToEnd();
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "Failed to read stdin");
            return 1;
        }

        // Empty stdin is valid — exit cleanly (e.g., piped empty input)
        if (string.IsNullOrWhiteSpace(input))
        {
            logger.LogDebug("Empty stdin, exiting cleanly");
            return 0;
        }

        HookEventModel? hookEvent;
        try
        {
            hookEvent = JsonSerializer.Deserialize(input, ImrdyJsonContext.Default.HookEventModel);
        }
        catch (JsonException ex)
        {
            logger.LogError(ex, "Failed to parse hook JSON");
            return 1;
        }

        if (hookEvent is null || string.IsNullOrEmpty(hookEvent.SessionId))
        {
            logger.LogError("Hook event is null or missing session_id");
            return 1;
        }

        // Validate session_id is a safe filename (Claude Code uses UUIDs)
        if (!SessionIdValidator.IsValid(hookEvent.SessionId))
        {
            logger.LogError("Invalid session_id format: contains unsafe characters");
            return 1;
        }

        logger.LogDebug("Hook event: {Event} for session {SessionId}",
            hookEvent.HookEventName, hookEvent.SessionId);

        // Derive status from hook event
        var status = StatusDerivation.DeriveStatus(
            hookEvent.HookEventName,
            hookEvent.Source,
            hookEvent.NotificationType);

        // Single-line hook log with all payload fields for grep-friendly diagnostics.
        // Every payload-derived value on this line goes through LogFieldEscaper.Escape. The log is one
        // line per event and is read with grep, so any field carrying CR or LF ends the record
        // early and lets its remainder forge a second line that parses as a genuine hook record
        // (CWE-117). session_id is the one value not escaped here, and only because
        // IsValidSessionId already rejected everything outside [A-Za-z0-9_-] above.
        var parts = new List<string>(8)
        {
            LogFieldEscaper.Escape(hookEvent.HookEventName)
        };
        if (!string.IsNullOrEmpty(hookEvent.NotificationType))
            parts.Add($"type={LogFieldEscaper.Escape(hookEvent.NotificationType)}");
        if (!string.IsNullOrEmpty(hookEvent.ToolName))
            parts.Add($"tool={LogFieldEscaper.Escape(hookEvent.ToolName)}");
        if (!string.IsNullOrEmpty(hookEvent.Source))
            parts.Add($"source={LogFieldEscaper.Escape(hookEvent.Source)}");
        if (!string.IsNullOrEmpty(hookEvent.Message))
            parts.Add($"msg={LogFieldEscaper.Escape(StateFileModel.TruncateMessage(hookEvent.Message, 80))}");
        // background_tasks is a typed property rather than extension data, so it has to be
        // logged explicitly or the diagnostic line silently loses the field (D10). This reads
        // the raw payload value, NOT the degraded roster local built below: an absent field
        // prints no token at all while a present-but-empty one prints tasks=0[], and those are
        // different facts — collapsing them would destroy the distinction on exactly the event
        // where it matters most (an absent field on a Stop). Each entry emits its status because
        // nothing else reads that field post-ship; a token whose entries are not "running" is
        // the only signal that the "every entry is live work" assumption has drifted (D21, RK5).
        // Each field is escaped like every other token on this line, and the stakes are highest
        // here: this token IS the trip-wire, so a roster field able to forge a log line would
        // leave the control reporting readings it never took (D27, CWE-117).
        if (hookEvent.BackgroundTasks is { } payloadTasks)
        {
            var taskTriples = string.Join(",", payloadTasks.Select(
                t => $"{LogFieldEscaper.Escape(t.Type)}:{LogFieldEscaper.Escape(t.Id)}:{LogFieldEscaper.Escape(t.Status)}"));
            parts.Add($"tasks={payloadTasks.Count}[{taskTriples}]");
        }
        // Extension data is the widest opening on this line: it carries whatever undeclared keys
        // a payload holds, tool_input among them, so its values routinely contain text no
        // operator wrote. Step 09 watched a session's own grep output arrive through here and a
        // later census parse it back as a roster reading — this injection class happening by
        // accident, with no attacker (live-run/RESULTS.md). Key and value are both escaped.
        if (hookEvent.ExtensionData is { Count: > 0 })
        {
            foreach (var kv in hookEvent.ExtensionData)
                parts.Add($"{LogFieldEscaper.Escape(kv.Key)}={LogFieldEscaper.Escape(kv.Value.ToString())}");
        }
        var detailStr = string.Join(" ", parts);
        if (!string.IsNullOrEmpty(hookEvent.AgentId))
            logger.LogInformation("Hook: {SessionId} → {Status} ({Details}) [teammate agent={AgentId}]",
                hookEvent.SessionId, status, detailStr, LogFieldEscaper.Escape(hookEvent.AgentId));
        else
            logger.LogInformation("Hook: {SessionId} → {Status} ({Details})",
                hookEvent.SessionId, status, detailStr);

        logger.LogDebug("Hook raw stdin: {RawStdin}", input);

        // Read existing state file for field preservation
        var reader = services.GetRequiredService<StateFileReader>();
        var statePath = Path.Combine(ImrdyPaths.Sessions, $"{hookEvent.SessionId}.json");
        var existing = reader.ReadStateFile(statePath);

        // Extracted once, before the branch: SubagentStop normally carries agent_id and takes the
        // teammate path, but subagent lifecycle events can also reach the lead path without one
        // (the parent spawns and reaps the subagent), so a roster applied on only one branch would
        // be dropped on the other. Declared as the interface both shapes satisfy — inferring the
        // type would fix it to List<T>? and the Array.Empty<T>() reassignment would not compile.
        IReadOnlyList<BackgroundTaskModel>? roster = hookEvent.BackgroundTasks;
        if (roster is null && ClearsRoster(hookEvent.HookEventName, hookEvent.Source))
        {
            roster = Array.Empty<BackgroundTaskModel>();
        }

        // Teammate events (agent_id present): supply the running-task roster. No liveness
        // timestamp is touched. Preserves lead status except when clearing a resolved
        // "permission" state.
        if (!string.IsNullOrEmpty(hookEvent.AgentId))
        {
            if (existing is not null)
            {
                var updated = TeammateGate.ApplyTeammateEvent(existing, hookEvent.HookEventName, roster);

                if (updated.Status != existing.Status)
                {
                    logger.LogInformation("Hook: {SessionId} → {Status} (teammate cleared permission, was {Event})",
                        hookEvent.SessionId, updated.Status, hookEvent.HookEventName);
                }

                try
                {
                    reader.WriteStateFile(statePath, updated);
                }
                catch (IOException ex)
                {
                    logger.LogError(ex, "Failed to write teammate timestamp: {Path}", statePath);
                    return 1;
                }
            }
            else
            {
                logger.LogWarning("Teammate hook fired before lead session exists: {SessionId} agent={AgentId}",
                    hookEvent.SessionId, LogFieldEscaper.Escape(hookEvent.AgentId));
            }

            // Auto-spawn tray if not running (same as lead path).
            // NOT gated on tray.enabled or overlay.enabled — the process does more than
            // render UI (state tracking, dwell timers, toasts, sounds, hot-reload of config).
            // Users can toggle display surfaces at runtime; the monitor must be alive to
            // respond. The proper escape hatch for headless/CI is the IMRDY_NO_TRAY env
            // var, which the platform EnsureTrayRunning implementation honors.
            try
            {
                hookEnvironment.EnsureTrayRunning();
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Tray auto-spawn failed");
            }

            return 0;
        }

        // Lead events (no agent_id): full state file write
        var normalizedCwd = hookEnvironment.NormalizeCwd(hookEvent.Cwd);
        var project = Path.GetFileName(hookEnvironment.NormalizeCwd(hookEvent.Cwd) ?? "");

        // Resolve Claude PID (best-effort — don't fail the hook if this fails)
        int? claudePid = null;
        try
        {
            var currentPid = Environment.ProcessId;
            claudePid = hookEnvironment.ResolveTerminalPid(currentPid, hookEvent.SessionId);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to resolve Claude PID");
        }

        // Resolve last message
        var lastMessage = FieldPreservation.ResolveLastMessage(
            hookEvent.Prompt,
            hookEvent.Message,
            existing?.LastMessage);

        // Subagent lifecycle events (SubagentStart/Stop, TaskCreated/Completed, TeammateIdle) can
        // reach the lead stream without an agent_id, because the parent spawns and reaps the
        // subagent. They describe the subagent, not whether the lead is waiting for the user, so
        // the lead's status carries forward untouched.
        var leadStatus = status;
        if (TeammateGate.IsSubagentLifecycleEvent(hookEvent.HookEventName) && existing is not null)
        {
            leadStatus = existing.Status;
        }

        // Build new state
        var newState = new StateFileModel
        {
            SessionId = hookEvent.SessionId,
            Status = leadStatus,
            Project = project,
            Cwd = normalizedCwd,
            HookEvent = hookEvent.HookEventName,
            NotificationType = hookEvent.NotificationType ?? "",
            LastMessage = lastMessage,
            ClaudePid = claudePid,
            Timestamp = DateTimeOffset.UtcNow,
            SessionName = hookEvent.SessionName,
            ToolName = hookEvent.ToolName,
            WslDistro = hookEvent.WslDistro ?? hookEnvironment.GetWslDistro(),
            RunningTasks = roster,
        };

        // Populate StartedAt on the first SessionStart — persisted via FieldPreservation on all
        // subsequent writes. Guard on existing?.StartedAt so a second SessionStart (reconnect or
        // tray restart) does not overwrite the original session start time.
        if (string.Equals(hookEvent.HookEventName, "SessionStart", StringComparison.OrdinalIgnoreCase)
            && existing?.StartedAt is null)
        {
            newState = newState with { StartedAt = DateTimeOffset.UtcNow };
        }

        // Preserve sound_pack, desktop_index, icon_style, started_at, and has_teammates from existing state
        newState = FieldPreservation.PreserveFields(newState, existing);

        // Write atomic state file (StateFileReader.WriteStateFile creates the directory if needed)
        try
        {
            reader.WriteStateFile(statePath, newState);
            logger.LogDebug("State file written: {Path}", statePath);
        }
        catch (IOException ex)
        {
            logger.LogError(ex, "Failed to write state file: {Path}", statePath);
            return 1;
        }

        // Auto-spawn tray if not running (mutex-gated, IMRDY_NO_TRAY-disablable).
        // NOT gated on config flags — see comment at the teammate-path spawn above.
        try
        {
            hookEnvironment.EnsureTrayRunning();
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Tray auto-spawn failed");
        }

        // Clean up session end
        if (string.Equals(hookEvent.HookEventName, "SessionEnd", StringComparison.OrdinalIgnoreCase))
        {
            hookEnvironment.OnSessionEnd(hookEvent.SessionId);
        }

        return 0;
    }

    /// <summary>
    /// True when an event carrying no roster must nonetheless overwrite the stored one with an
    /// empty list, instead of letting <see cref="FieldPreservation.PreserveFields"/> carry the
    /// previous roster forward. Two events qualify, for two different reasons:
    /// <para>
    /// <c>Stop</c> — the lead finished its turn and reported no running work. Degrading to empty
    /// returns imrdy to lead-readiness-only behaviour should a Claude Code build ever drop the
    /// field, rather than stranding the session at teal forever (D6, spec §8 E7).
    /// </para>
    /// <para>
    /// <c>SessionStart</c> with source <c>startup</c> or <c>resume</c> — a process boundary.
    /// Background tasks are owned by the Claude Code process that spawned them, so a roster
    /// written by a previous process describes work that is already dead (D25, spec §8 E11).
    /// </para>
    /// <para>
    /// <b>Every test is an exact equality, never a substring match.</b> Both event names are
    /// proper substrings of an event that must NOT match: <c>SubagentStop</c>, where an absent
    /// field leaves the stored roster untouched (spec §8 E8), and <c>SubagentStart</c>, which
    /// fires when an agent <i>begins</i> work (spec §8 E12). A substring test compiles, reads
    /// fine, and silently wipes a live roster on every subagent event — the exact false-green
    /// regression this whole mechanism exists to remove. That is also why the helper is named
    /// for what it does rather than for a family of event names.
    /// </para>
    /// <para>
    /// <b>The source filter is an allowlist, deliberately.</b> <c>clear</c> and <c>compact</c>
    /// are intra-session — the owning process is alive and its work keeps running — so they
    /// preserve, and so does any unknown future source value. An unrecognised source therefore
    /// fails toward stale teal, which is silent, rather than false green, which fires a toast at
    /// the user on work that is still running (D25, spec §8 E11b).
    /// </para>
    /// </summary>
    private static bool ClearsRoster(string eventName, string? source) =>
        string.Equals(eventName, "Stop", StringComparison.OrdinalIgnoreCase)
        || (string.Equals(eventName, "SessionStart", StringComparison.OrdinalIgnoreCase)
            && (string.Equals(source, "startup", StringComparison.OrdinalIgnoreCase)
                || string.Equals(source, "resume", StringComparison.OrdinalIgnoreCase)));
}
