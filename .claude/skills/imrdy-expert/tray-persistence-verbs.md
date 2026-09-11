---
tags: [imrdy-expert/persistence]
summary: "Catalog of every place the tray process writes JSON state to disk — a debugging checklist for persistence loss"
---

# Tray Persistence Verbs

Use this page as the diagnostic checklist when a tray-side change "doesn't seem to be saved." Every tray-owned write path is here.

## Config — `~/.imrdy/config.json`

**Entry point:** `ConfigReader.Update(Func<ImrdyConfig, ImrdyConfig> mutate)` (`src/Imrdy.Core/ConfigReader.cs:41`)

**Pattern:** RMW with atomic write via `AtomicFileWriter`. Last-writer-wins. No concurrent writers in production (tray is the only writer of config.json).

**What triggers it:** Anything that mutates global config — controller menu actions (icon style, sound pack default, overlay toggle, etc.). Most paths go through `ConfigReader.Update(c => c with { ... })`.

**Failure modes:**
- The mutate lambda must produce a fully-formed `ImrdyConfig` — partial-record updates that drop nested objects (e.g., `c with { Tray = null }`) can fail `EnsureDefaults` round-tripping on the next read.
- Three-state nullable fields (`DiagnosticsConfig.IpcEnabled` = `bool?`) must NOT be flattened to a concrete `bool` in `EnsureDefaults`. The null state is semantically distinct from `false`. (`OverlayConfig.Interactive` was a `bool?` with the same pattern but was removed in the OverlayPanel redesign.)

## Workspaces — `~/.imrdy/workspaces.json`

**Entry points:** `WorkspaceStore` (`src/Imrdy.Core/Workspace/WorkspaceStore.cs`)

| Method | Effect |
|---|---|
| `Pin(path, name, desktop)` | Add or replace a workspace entry (preserves IconStyle if present) |
| `Unpin(path)` | Remove a workspace entry (no-op if absent) |
| `SetDesktop(path, desktop)` | Update desktop assignment (no-op if absent) |
| `SetIconStyle(path, iconStyle)` | Update icon-style override (null clears it; no-op if absent) |

**Pattern:** Each method does Load → mutate → Save. Save uses `AtomicFileWriter`. Last-writer-wins.

**What triggers it:** Workspace menu actions (pin/unpin via right-click on a tray dot, set workspace icon style via Manage submenu, etc.).

**Failure modes:**
- `Load()` catches `JsonException` and `IOException` and returns an empty `WorkspaceConfig`. A corrupt or mid-write file appears as "no workspaces" — Pin/Unpin then runs against an empty list and **overwrites** the original. (Unlikely in practice since workspaces.json is atomic-write — but worth knowing for diagnosis.)

## Cross-machine links — `~/.imrdy/publishers.json`

**Entry points:** `PublisherStore` (`src/Imrdy.Core/Publishing/PublisherStore.cs`) — deliberately shaped exactly like `WorkspaceStore`.

| Method | Effect |
|---|---|
| `Add(name, endpoint)` | Add or replace a link entry, matched by name |
| `Remove(name)` | Remove a link entry (no-op if absent) |
| `SetDesktopIndex(name, idx)` | Update the machine's desktop mapping (null clears it) |
| `SetMuted(name, muted)` | Update the per-publisher notification mute |
| `SetEnabled(name, enabled)` | Enable/disable the link without deleting the record |

**Pattern:** Load → mutate → `Save` via `AtomicFileWriter`. Last-writer-wins.

**What triggers it:** the Connections window's Add… / Edit… / Remove buttons, routed through `IConnectionsHost.SavePublisher` / `RemovePublisher` on `TrayApp`.

**Registered in:** `MonitorServiceBuilder`, `DaemonServiceBuilder`, `CliServiceBuilder` — **never** `HookServiceBuilder`. The hook must not carry the publish stack.

**Failure modes:** same as `WorkspaceStore` — `Load()` swallows `JsonException`/`IOException` and returns an empty `PublisherConfig`, so a corrupt file reads as "no links". Unlike `WorkspaceStore`, a corrupt load is *not* overwritten on the next mutate; that is pinned by a test.

## Remote session state — `~/.imrdy/sessions/{session_id}.json` (receiver side)

**Entry point:** `SessionIngest` (`src/Imrdy.Core/Publishing/SessionIngest.cs`) — the single writer of remote session files, shared by `FileSink` (writing into *another* machine's directory over a mount) and `WireListener` (writing into this machine's own).

**Pattern:** `RemoteSessionMerge` then `StateFileReader.WriteStateFile` — a direct write, **never** a rename, since delete-then-move suppresses the receiver's FSW `Changed` event (same reason as [State File Write Path](state-file-write-path.md)).

**Merge rule:** the *receiver* keeps `SoundPack`, `IconStyle` and `DesktopIndex`; an incoming `desktop_index` is discarded; `origin_machine` is stamped by the receiving side and never trusted from the payload.

**Failure modes:** a state file that already carries `origin_machine` must never be re-published — `SessionPublisher.IsLocallyOwned` is the guard, and losing it is an infinite publish loop between two machines rather than a dropped write.

## Session state — `~/.imrdy/sessions/{session_id}.json`

**Entry point:** `TrayApp.PersistSessionField(SessionEntry, Func<StateFileModel, StateFileModel>)` (`src/Imrdy.Windows/TrayApp.cs:837`)

**Pattern:** RMW with **non-atomic** `File.WriteAllBytes` via `StateFileReader.WriteStateFile`. **The hook process writes the same file concurrently** — this is the racy path. See [Tray vs Hook Write Race](tray-hook-write-race.md).

**Tray-owned fields written via this verb today:**

| Wrapper | Field updated | Triggered by |
|---|---|---|
| `PersistSessionSoundPack(entry)` | `SoundPack` | Right-click session → Sound Pack submenu |
| `PersistSessionIconStyle(entry)` | `IconStyle` | Right-click session → Icon Style submenu |
| `PersistSessionDesktopIndex(entry)` | `DesktopIndex` | (1) Right-click session → "Assign to this Desktop" menu action. (2) WT auto-lock: new-session branch of `HandleSessionFileChanged` when `state.HookEvent == "SessionStart"` AND `entry.DesktopIndex is null` AND `IsWindowsTerminal(entry)` — captures `_desktopManager.GetCurrentDesktopIndex()` so the active desktop is remembered for the WT session. |

**Failure modes (in order of likelihood):**

1. **Race-loss vs hook event** — see [Tray vs Hook Write Race](tray-hook-write-race.md). Fixable only by adding the field to [Field Preservation Catalog](field-preservation-catalog.md).
2. **Tray reads a missing state file.** `PersistSessionField` reads the current file and exits silently if it returns `null` (deleted or corrupt). The tray's intended mutation is dropped with only a `LogDebug` line. Look for `Could not persist session field for {SessionId}` in the log.
3. **Silent no-op on session removal mid-write.** If the session has been swept (state file deleted) between the tray's user action and the persist call, the file is gone and the write is silently skipped.

**Instrumentation gap:** `PersistSessionField` does not log on success. Only failures emit a Debug-level line. If you suspect a write is being lost, you cannot confirm from the log alone that the write happened — you must inspect the file timestamp or contents.

## Session removal — `~/.imrdy/sessions/{session_id}.json` (delete)

**Entry points:**
- `TrayApp.RemoveSession(sessionId)` — single-session removal triggered by sweep / SessionEnd grace expiry. Direct `File.Delete` on the state file.
- `TrayApp.ClearAllSessions()` — manual menu action. Iterates and deletes each state file.
- `TrayApp.ClearSessionsFromMachine(name)` — Connections window → **Clear sessions**. Deletes every session that arrived under one publisher's name, routed through `RemoveSession` so icon, dwell state, cooldown and file go together. Note that a *disconnected* publisher's sessions are never swept automatically — both cleanup paths key on the state file still existing on disk, and `WireListener` never deletes on disconnect, so this is the only way they go.
- `StateFileReader.RemoveStateFile(sessionsDir, sessionId)` — utility that deletes both the state file and the `.pid-{sessionId}` cache file.

**Pattern:** Direct delete, swallows `IOException`. No atomicity needed — deletion is idempotent.

**Failure modes:** None observed. `File.Delete` on a missing path is a no-op; locked file (rare) is caught and logged.

## What the tray does **not** write

Useful negative knowledge:

- **`.pid-{sessionId}` cache files** — written by the hook (`HookCommand`), not the tray. Tray only deletes them on session removal via `StateFileReader.RemoveStateFile`.
- **`logs/*.log`** — written by Serilog directly (rolling file sink). No tray-side persistence verb.
- **`graphics/packs/`** — read-only from the tray's perspective. Packs are installed by `packs install` CLI; the tray loads them on hot-reload.
- **`sound/packs/`** — same as graphics packs.
- **`imrdy.png`** — written once by the toast notification code (icon extraction). Not part of state.

## Cross-references

- [State File Write Path](state-file-write-path.md) — why session state is non-atomic
- [Tray vs Hook Write Race](tray-hook-write-race.md) — the writer-vs-writer hazard
- [Field Preservation Catalog](field-preservation-catalog.md) — the symmetry contract
- [Architecture](architecture.md) — State File Lifecycle
