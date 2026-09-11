---
tags: [imrdy-expert/architecture]
summary: "Seven entry points, timer interactions, field preservation, and state file lifecycle"
---

# Architecture

## Seven Entry Points (Program.cs)

| Command | Class | Purpose |
|---------|-------|---------|
| `imrdy hook` | HookCommand | Fast-path: read stdin JSON, derive status, write state file. No WinForms. Lightweight DI via HookServiceBuilder. |
| `imrdy <cmd>` | CommandRouter | CLI commands (status, packs, config, workspace, links, stop, inspect-live, render-live). Spectre.Console output. |
| `imrdy preview-dashboard <fixture>` | PreviewDashboardCommand | Standalone WinForms dev tool; inline ServiceCollection, bypasses mutex, runs SessionDashboardForm pinned from fixture JSON. |
| `imrdy render <component> [args]` | RenderCommand | In-process PNG capture of WinForms surfaces; bypasses mutex; sequential STA execution. See [Render Verb Architecture](render-verb-architecture.md). |
| `imrdy inspect-live <id>` | InspectLiveCommand | Thin CLI client: connects to tray via `Local\ImrdyInspect` pipe, emits walker+analyzer JSON. |
| `imrdy render-live <id> --output F` | RenderLiveCommand | Thin CLI client: connects to tray via `Local\ImrdyInspect` pipe, captures live SessionDashboardForm PNG. |
| `imrdy` | TrayApp | WinForms ApplicationContext. Application.Run with message pump. Full DI via MonitorServiceBuilder. |

The hook runs hundreds of times per session. It must be fast (~50ms). No COM, no WinForms initialization. The `inspect-live` and `render-live` commands are thin clients — all heavy work (walking, rendering) runs inside the already-running tray on the UI thread via `BeginInvoke` + `TaskCompletionSource` bridge. See [Tray IPC](inspect-ipc.md) for protocol details.

**A CLI verb needs two touches in `Program.cs`, not one.** `CommandRouter`'s `switch` is the second; the first is the `args[0] is "status" or "packs" or ... ` pattern that decides whether to take the Spectre branch at all. A verb wired only into `CommandRouter` falls through to the tray fallback and *silently starts a tray* — it prints nothing and exits 0, which does not look like a routing bug. `imrdy links` shipped that way for one build.

## The Linux Binary (src/Imrdy.Linux/Program.cs)

A separate binary with its own, much smaller arm set:

| Command | Purpose |
|---------|---------|
| `imrdy hook` | Same fast path. `LinuxHookEnvironment.EnsureTrayRunning` probes `DaemonLock.IsRunning` and spawns the daemon via `sh -c '... &'` (stdio to `/dev/null`, orphaned to init) only when at least one enabled link exists. Every failure swallowed. |
| `imrdy daemon` | `DaemonCommand` → `DaemonHost`. The event loop Linux has no `Application.Run` analogue for. SIGINT and SIGTERM both cancel so shutdown releases `daemon.lock` through `Dispose`. Returns an exit code rather than calling `Environment.Exit`. |
| `imrdy links [--json]` | Plain stdout via `LinksReport.RenderLines` — no Spectre. |

## State File Lifecycle

1. Hook writes `~/.imrdy/sessions/{session_id}.json` atomically
2. TrayApp's FileSystemWatcher detects change
3. Debounce timer (100ms drain) batches rapid changes
4. `HandleSessionFileChanged` reads state, updates icon/menu/overlay
5. Dwell timer gates toast/sound notifications (see [Notification Dwell](notification-dwell.md))

## Field Preservation

`FieldPreservation.PreserveFields()` carries sticky fields across state file writes. The hook writes a new state file on every event, but some fields are tray-owned and must survive:

- `SoundPack` — assigned by tray, not hooks
- `DesktopIndex` — assigned by tray
- `IconStyle` — assigned by tray or workspace
- `StartedAt` — set once on first SessionStart, preserved across reconnects
- `WslDistro` — stable per session, falls back to existing when env var unavailable
- `RunningTasks` (`running_tasks`) — the running-work roster, written by whichever hook carried a
  `background_tasks` array; preserved by every write that carried none

Pattern: `newState.Field ?? existing.Field` — new value wins if set, otherwise keep existing.

`RunningTasks` is the one entry whose `null` is load-bearing: `null` means "this write said nothing
about running work" (preserve), while `[]` means "measured, nothing is running" (a real value that
wins the merge). The two must never be normalised into each other — collapsing `[]` to `null` would
make a `Stop` reporting no running work silently preserve a stale roster instead of clearing it.
`HookCommand.ClearsRoster` is the counterpart on the write side: it substitutes `[]` for an absent
roster on `Stop` and on `SessionStart` with `source` `startup`/`resume`, so those two events reach
`PreserveFields` with a real value rather than a `null` that would preserve. See
[Teammate Detection](teammate-detection.md) for that rule and why its `source` filter is an
allowlist.

This list is also the **symmetry contract** between hook writes and tray writes — any tray-owned field NOT on this list is silently dropped by the next hook event. See [Field Preservation Catalog](field-preservation-catalog.md) for the audit procedure, [Tray vs Hook Write Race](tray-hook-write-race.md) for the race window, [State File Write Path](state-file-write-path.md) for why the file is non-atomic, and [Tray Persistence Verbs](tray-persistence-verbs.md) for the full tray-side write surface.

## Timer Interactions

TrayApp has multiple timers that interact:

| Timer | Interval | Purpose |
|-------|----------|---------|
| Drain timer | 100ms | Process pending file changes, effective-status resolution, dwell dispatch |
| Sweep timer | 10s | Existence-check only via `CleanupGoneSessions`; removes in-memory entries whose state files are gone |
| Stale timer | 60s | Remove sessions past grace period |

The drain timer is the central coordination point:
1. Process queued file change events
2. Recompute `DisplayStatus.Resolve` per session and diff against `SessionEntry.LastEffectiveStatus` — the sole dwell driver for status changes, including the teal → green flip. `Resolve` is time-independent: it reads the stored roster, so this loop fires on genuine state changes rather than on the passage of time (see [Teammate Detection](teammate-detection.md), [Status Mapping](status-mapping.md))
3. Dispatch fired dwell notifications

The sweep timer is **existence-check only** since commit 4702e86 (`sweep-removal-busy-promotion`): it runs `CleanupGoneSessions`, which iterates the in-memory session entries and removes any whose state file no longer exists on disk. It does NOT re-read state file contents. FSW (FileSystemWatcher) is the sole real-time path for content changes — the drain timer drains queued FSW events on the 100ms tick. State file bootstrapping at startup is handled separately by `BootstrapSessions`, a one-time scan that runs before the timers start. `SessionEntry.LastProcessedTimestamp` still exists and is used in the FSW path (`HandleSessionFileChanged` returns early when the file's `Timestamp` matches `LastProcessedTimestamp`) — that early-return logic was preserved when the sweep re-read was removed.

## Session Icon Style Resolution

Chain: session override → workspace override (Cwd match) → global config

`ResolveSessionIconStyle()` implements this fallback. Renderer cache is keyed by style name. Changing a workspace's style refreshes all matching session icons.

## Single Instance

Mutex-gated via `Global\ImrdyMonitor`. Hook fast-path probes mutex to decide whether to spawn tray. `TraySpawner.EnsureRunning()` called from hook on every event.

## Stop Signal

Named `EventWaitHandle` (`Local\ImrdyStop`). `imrdy stop` signals it. Tray listens on background thread, marshals `ExitThread` to UI thread.

## Diagnostics IPC Server

Named pipe `Local\ImrdyInspect`. Controlled by `DiagnosticsConfig.IpcEnabled` (`bool?`); default null = on when `~/.imrdy/.dev-build` exists, off otherwise. `InspectIpcServer` in `src/Imrdy.Windows/Diagnostics/` starts 4 parallel accept loops; each request dispatches to the UI thread via `BeginInvoke` + `TaskCompletionSource` with a 2-second budget. See [Tray IPC](inspect-ipc.md) for full protocol details.
