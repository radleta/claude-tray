---
tags: [imrdy-expert/architecture]
summary: "config.json FSW routes through OnConfigChanged for full live reload (sound + icon style + tray god toggle + overlay + network); overlay structural-delta: Position/Monitor/Locked/OffsetX/OffsetY apply in-place, Enabled/Size/Spacing recreate; network re-resolves machineName always and rebinds the listener only on a ListenEnabled/ListenPort change; startup uses LoadSoundConfig separately"
---

# Config Live Reload

## How It Works

`TrayApp` maintains a `FileSystemWatcher` on `~/.imrdy/config.json`. When the file changes — whether via the controller menu, `imrdy config` CLI, or direct edit — the FSW fires and the change is applied live without a tray restart.

### FSW Setup

```csharp
_configWatcher = new FileSystemWatcher(ImrdyPaths.Home, "config.json")
{
    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.CreationTime,
    EnableRaisingEvents = true,
};
_configWatcher.Changed += OnConfigFileChanged;
_configWatcher.Created += OnConfigFileChanged;
// NO Deleted subscription — atomic write briefly deletes the file
```

The watcher subscribes to both `Changed` and `Created` because `AtomicFileWriter` uses delete-then-move (not in-place overwrite). The move generates a `Created` event, not `Changed`. See [State File Write Path](state-file-write-path.md) for the full atomicity rationale.

### Drain Path

`OnConfigFileChanged` (background FSW thread) enqueues the token `"CONFIG_RELOAD"` into `_pendingChanges`. The drain timer (UI thread, 100ms) dequeues it and calls:

```csharp
if (item == "CONFIG_RELOAD")
{
    try { OnConfigChanged(ConfigReader.Read()); }
    catch (Exception ex) { _logger.LogError(ex, "Failed to live-reload config from file change"); }
}
```

The try/catch swallows `IOException`/`JsonException` from mid-write transient reads. The next drain cycle will read the completed file.

### What OnConfigChanged Reloads

`OnConfigChanged(ImrdyConfig config)` is the single handler for all live config changes. It reloads everything:

| Setting | Behavior |
|---------|----------|
| Sound (`config.Sound`) | Reloads packs, updates `_soundEnabled`, clears sound bag cache |
| Icon style (`config.Tray.IconStyle`) | Value-compared; on change: refreshes all session icons, invalidates overlay style cache |
| Tray god toggle (`config.Tray.Enabled`) | Value-compared; on change: shows/hides all tray icons via `ApplyTrayEnabledToAll` |
| Overlay (`config.Overlay`) | Structural-delta classification: non-structural changes (Position/Monitor/Locked/OffsetX/OffsetY) call `ApplyPositionConfig` in-place — no flash, no dispose+recreate; structural changes (Enabled/Size/Spacing) or Enabled toggle: disposes old panel, controllers, and subscriptions; recreates from fresh config values if `overlay.enabled: true`. Drag-in-flight guard: defers the entire overlay block via `_overlayReloadDeferred` until `IsDragging == false`. |
| Network (`config.Network`) | `ApplyNetworkConfig`: always re-resolves `_machineName` through `MachineNameResolver`, because sinks resolve the origin per write rather than capturing it — without this a renamed machine kept stamping `origin_machine` with the old name until restart, and the far end saw one machine under two. The inbound listener is disposed and restarted **only** when `ListenEnabled` or `ListenPort` changed; every other field is a no-op rebind-wise. Registered publisher records live in `publishers.json`, not here. |

All comparisons are value-based — a controller-menu change that also writes the file produces a harmless second no-op call.

### Startup vs Live Reload

`LoadSoundConfig()` is called once at startup (before the FSW is active). It loads sound settings only. `OnConfigChanged` is the FSW path — it handles all settings and is never called at startup.

```
Startup:       LoadSoundConfig()        → sound only
FSW trigger:   OnConfigChanged(read)    → sound + icon style + tray toggle + overlay + network
```

## Gotcha: Direct File Edits Apply Immediately

Because `OnConfigChanged` is comprehensive, editing `config.json` directly (or via `imrdy config set`) live-applies all settings — overlay position/lock/monitor/offset, overlay enable/disable, tray enable/disable, icon style changes, and the whole `network` section — without restarting the tray. Structural overlay changes (Size/Spacing/Enabled toggle) dispose and recreate the panel; non-structural changes (Position/Monitor/Locked/OffsetX/OffsetY) apply in-place with no flash. No restart is needed for any config property.

## Cross-references

- [State File Write Path](state-file-write-path.md) — why the FSW subscribes to both Changed and Created (AtomicFileWriter delete-then-move)
- [Tray Persistence Verbs](tray-persistence-verbs.md) — `ConfigReader.Update` as the tray-side config write verb
- [Architecture](architecture.md) — drain timer, State File Lifecycle, timer table
