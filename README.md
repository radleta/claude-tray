# imrdy

System tray monitor for Claude Code sessions on Windows. Replaces the PowerShell + Node.js dual-runtime architecture with a single .NET executable.

## Why

Managing multiple Claude Code sessions in parallel is an attention problem: knowing which session needs you, which is working, which is idle, and acting on the right one without losing focus on your work. imrdy puts that information in the system tray where it stays glanceable in peripheral vision and never demands foreground attention.

## How it looks

Each active Claude Code session gets a colored circle icon in the system tray:
- **Red** = busy (working)
- **Teal** = idle, but the session's `background_tasks` roster still lists running work — agents or backgrounded shells (waiting for you; silent, no toast — may resume itself)
- **Green** = idle, nothing running (genuinely waiting for your input)
- **Orange** = needs attention
- **Purple** = permission requested (elicitation dialog)
- **Yellow** = error (tool or stop failure)
- Icons age (darken) over time based on last interaction
- Sessions published from another machine whose link has dropped shrink slightly and get a dashed ring — distinct from the aging dim

Click a session icon to switch to its virtual desktop and focus the terminal window.

## Installation

### Plugin (recommended)

```bash
claude plugin add https://github.com/radleta/imrdy
```

The plugin auto-installs the binary and default sound pack (`assistant`) on first session start via the bootstrap script. The sound pack is downloaded from the latest `pack-assistant-*` GitHub Release. The default config uses `"random"` pack selection, which picks randomly from enabled installed packs.

### Manual

**PowerShell (one-liner):**
```powershell
irm https://raw.githubusercontent.com/radleta/imrdy/main/install.ps1 | iex
```

**Or download from [Releases](https://github.com/radleta/imrdy/releases)** and place `imrdy.exe` in a directory on your PATH (e.g., `~/.local/bin/`).

## How It Works

1. **Hook** (`imrdy hook`): Called by Claude Code on every session event (start, prompt, tool use, stop, etc.). Reads hook JSON from stdin, writes a session state file to `~/.imrdy/sessions/`.

2. **Monitor** (`imrdy` with no args): WinForms system tray app that watches session state files via FileSystemWatcher. Creates/updates/removes tray icons as sessions change.

3. **CLI** (`imrdy status|packs|config|workspace|links|inspect-live|render-live`): Management commands for checking status, managing sound packs, editing config, pinning workspaces, inspecting cross-machine links, and live diagnostic inspection of the running tray.

The tray monitor auto-starts on the first hook event (mutex-gated — only one instance runs). To disable auto-start:
```bash
imrdy config set tray.enabled false
```

## CLI Commands

```
imrdy status              Show active sessions and workspaces
imrdy status --json       Machine-readable output

imrdy packs list          List installed sound packs
imrdy packs test <name>   Play a random sound from a pack
imrdy packs validate      Validate pack structure
imrdy packs set-default   Set the default sound pack
imrdy packs remove <name> Remove an installed pack
imrdy packs pack <path>   Validate and package a pack as ZIP

imrdy config show         Show current configuration
imrdy config set <k> <v>  Update a config value
imrdy config path         Show all file paths
imrdy config validate     Validate config and workspace files

imrdy workspace list      List pinned workspaces
imrdy workspace pin <p>   Pin a workspace (auto-derives name)
imrdy workspace unpin <p> Unpin a workspace

imrdy links               Show every link, both directions — live health from the running
                          tray, or publishers.json records when no tray answers
imrdy links --json        The same view model as JSON

imrdy stop                Stop the tray app (auto-restarts on next hook)

imrdy inspect-live <id>   Walk the live SessionDashboardForm for <id> and emit control-tree JSON + diagnostics
imrdy render-live <id>    Capture a live SessionDashboardForm PNG for <id> via the tray IPC server

imrdy --help              Show help
imrdy --version           Show version
```

All commands support `--json` for machine consumption.

**On Linux the command set is smaller.** That binary is a publisher, not a monitor, and it ships five arms and no others:

```
imrdy hook            Process a Claude Code hook event from stdin
imrdy daemon          Publish this machine's sessions to every enabled link
imrdy links [--json]  Show this machine's registered links (records only — see below)
imrdy --version       Show version
imrdy --help          Show this help
```

Everything else listed above — `status`, `packs`, `config`, `workspace`, `stop`, `inspect-live`, `render-live` — is Windows-only. `imrdy config path` in particular does not exist on Linux; the paths are the same ones in the table under [Configuration](#configuration), rooted at `~/.imrdy/`. Anything the Linux binary does not recognize prints `unrecognized command` to stderr and exits 1.

## Sound Packs

Sound packs live in `~/.imrdy/sounds/packs/<pack-name>/`. Each pack has a `pack.json` manifest and event folders containing `.wav` files.

**Events:** GettingToWork, Finished, SessionEnd, NeedsYou, Forgotten

**Pack structure:**
```
~/.imrdy/sounds/packs/my-pack/
  pack.json
  getting-to-work/
    clip1.wav
    clip2.wav
  finished/
    clip1.wav
  ...
```

Set the default pack:
```bash
imrdy packs set-default my-pack
```

### Authoring Sound Packs

To create and distribute a custom sound pack:

1. Create a pack directory under `sounds/` in the repo (e.g., `sounds/my-pack/`) with the standard structure above
2. Validate and package it:
   ```bash
   imrdy packs pack sounds/my-pack/ --output ./dist/
   ```
3. Tag a release: `git tag pack-my-pack-v1.0.0 && git push --tags`
4. The `release-packs.yml` workflow builds the ZIP and creates a GitHub Release with the artifact and SHA256 checksum

Or map packs to specific projects via config:
```bash
imrdy config set sound.defaultPack my-pack
```

## Graphics Packs

Graphics packs let you replace the default colored-dot tray icons with custom SVG artwork. Each session icon is rendered by a pack at runtime via Svg.NET.

**Pack location:** `~/.imrdy/graphics/packs/<pack-name>/`

**Pack structure:**
```
~/.imrdy/graphics/packs/my-pack/
  pack.json
  idle.svg
  busy.svg
  attention.svg
  permission.svg
  compact.svg
  unknown.svg
  workspace.svg
```

**Minimal `pack.json`:**
```json
{
  "name": "my-pack",
  "format": "svg",
  "version": "1.0.0",
  "license": "MIT",
  "states": {
    "idle":       { "file": "idle.svg" },
    "busy":       { "file": "busy.svg" },
    "attention":  { "file": "attention.svg" },
    "permission": { "file": "permission.svg" },
    "compact":    { "file": "compact.svg" },
    "unknown":    { "file": "unknown.svg" },
    "workspace":  { "file": "workspace.svg" }
  }
}
```

**Install a pack:** Drop the pack folder into `~/.imrdy/graphics/packs/`. Only install packs from trusted sources — SVG files are rendered at runtime and pack content is not sanitized in this release.

**Switch to a pack:**
```bash
imrdy config set tray.iconStyle pack:my-pack
```

**Switch back to dots:**
```bash
imrdy config set tray.iconStyle dots
```

Aging (session idle time) is applied automatically to all packs via `ColorMatrix` desaturation and dimming — no pack-specific work required. If a pack fails to load, the tray silently falls back to the built-in dot renderer.

All packs must declare a `license` field in `pack.json`. A stub `dev-test` pack ships with the source under `src/Imrdy.Windows/Resources/graphics-packs/` for use in development.

## Overlay (Mode B)

An alternative to the 16px tray icons: a floating borderless window that renders session characters as a horizontal row. Uses the active graphics pack (or colored circles in dots mode). Stays on top via `Form.TopMost = true`, and never steals focus from your terminal.

The overlay free-floats: drag it by the grip handle on its left edge (six dots, dimmed until you hover it) and drop it anywhere. Release within ~24px of a screen edge or corner and it snaps flush; drop it further in and it stays where you put it. The position is remembered per monitor.

**Enable:**
```bash
imrdy config set overlay.enabled true
```

**Config fields:**

| Field | Default | Description |
|-------|---------|-------------|
| `overlay.enabled` | `false` | Show the overlay window |
| `overlay.position` | `"bottom-right"` | Fallback anchor used when no offset is set: `top-left`, `top-center`, `top-right`, `bottom-left`, `bottom-center`, `bottom-right` |
| `overlay.size` | `64` | Icon size in pixels (32–256) |
| `overlay.spacing` | `8` | Gap between icons in pixels (0–32) |
| `overlay.monitor` | `0` | Monitor index to dock to (0 = primary) |
| `overlay.locked` | `false` | Prevent repositioning by dragging the grip |
| `overlay.offsetX` | `null` | Free-float X, in logical px from the target monitor's working-area origin. `null` falls back to `overlay.position` |
| `overlay.offsetY` | `null` | Free-float Y, same units. `null` falls back to `overlay.position` |

`overlay.offsetX` / `overlay.offsetY` are written for you when you drag the overlay or pick a position from its menu — you rarely need to set them by hand.

**CLI examples:**
```bash
imrdy config set overlay.position bottom-left
imrdy config set overlay.size 128
```

`imrdy config set` covers `overlay.enabled`, `overlay.position`, `overlay.size`, and `overlay.spacing`. Set `monitor`, `locked`, and the offsets from the overlay menu (below) or by editing `~/.imrdy/config.json`.

**Mouse:**

- **Drag the grip** (left edge) — reposition the overlay; snaps to a nearby edge or corner
- **Left-click an icon** — switch to that session's desktop and focus its terminal
- **Right-click an icon** — session or workspace menu
- **Right-click the empty area** — overlay settings menu: the 6 positions, spacing presets, monitor selector, and a **Lock** toggle

**Controller menu:** Right-click the controller icon → **Overlay** — the same settings menu: toggle enabled, position, size, spacing, monitor, and Lock.

**Limitations:** No animation, no peek mode.

## Controller Tray Icon

A persistent controller icon (headphones) appears in the system tray whenever the monitor is running. Right-click for a context menu:

- **Sounds** — Toggle sound playback on/off (checked = enabled)
- **Sound Pack** — Switch the active pack (Random, installed packs, or None)
- **Enabled Packs** — Toggle individual packs on/off for random selection
- **Icon Style** — Switch between dot icons and installed graphics packs (Dots, installed packs)
- **Overlay** — Toggle overlay window, select position, size, spacing, and monitor; lock its position
- **Sessions** — View and switch to active sessions
- **Workspaces** — View and switch to pinned workspaces
- **Connections…** — Open the cross-machine links window (add, edit, remove links; clear a machine's sessions)
- **Open Config Folder / Open Sounds Folder / View Log** — Quick access to file locations
- **Exit** — Shut down the monitor

Sound can also be toggled via CLI:
```bash
imrdy config set sound.enabled false   # disable sounds
imrdy config set sound.enabled true    # enable sounds
```

## Virtual Desktops

imrdy integrates with Windows virtual desktops:
- Left-click a session icon to switch to its desktop and focus the terminal
- Toast notifications are suppressed for sessions on the current desktop
- Pin workspaces to specific desktops via `imrdy workspace pin <path> --desktop 2`

Supports Windows 10 (20H1+) and Windows 11 (all versions through 24H2).

## Cross-Machine Sessions

Sessions running on another box — a second workstation, a WSL distro, a Linux server — can show up in this machine's tray beside the local ones. One machine **publishes** its session state; another **receives** it.

**On the receiving machine** (the one with the tray), enable the listener in `~/.imrdy/config.json`:

```json
"network": { "listenEnabled": true, "listenPort": 47600, "authKey": "some-shared-secret" }
```

**On the publishing machine**, register the receiver. On a Windows tray that is the Connections window's **Add…** button (see below) — it is the supported route and it takes effect immediately. A headless Linux publisher has no window, so its records go into `~/.imrdy/publishers.json` directly and the daemon picks them up on its next local session event. Either way the record reads:

```json
{
  "publishers": [
    { "name": "desk-win", "endpoint": "192.168.1.20:47600", "enabled": true }
  ]
}
```

On a publishing record `endpoint` is required, and is either `host:port` (a TCP link) or a directory path (a file link that writes into the receiver's `sessions/` folder over a mount — handy for WSL, where both sides share a filesystem). It is the thing this machine dials, which is why the receiving side's record omits it — see below. The publisher's `network.authKey` must match the receiver's, and the receiver's firewall must allow the port — imrdy does not create firewall rules for you.

On Linux the publisher is a daemon:

```bash
imrdy daemon      # publish this machine's sessions to every enabled link
```

You rarely start it by hand — the Linux hook spawns it on the next session event, but only when at least one enabled link is registered.

**On the receiving machine**, register the publisher too — again through the Connections window — so its sessions get a desktop and notification policy. **Give that record an `endpoint` only if this machine also publishes to the other one.** In the one-directional setup above it does not — the publisher connects here, so there is nothing to dial — and the record exists only to carry the desktop mapping and the mute:

```json
{
  "publishers": [
    { "name": "build-box", "desktop_index": 2, "muted": false, "enabled": true }
  ]
}
```

Adding an endpoint here when the other machine does not listen makes your tray dial *back* at a machine that is only publishing to it. A Linux publisher daemon never binds a port, so that link sits `Failed` forever and `imrdy links` exits 1 permanently, which defeats using it as a shell guard. With a directory endpoint it is worse: the receiver starts writing its own local sessions into the publisher's sessions folder.

Two Windows trays paired both ways is a supported setup — each is a publisher (the tray publishes) and a receiver (`network.listenEnabled`), and one record per machine legitimately carries both the endpoint you publish to and the `desktop_index` for what arrives from there.

| Field | Meaning |
|-------|---------|
| `name` | Machine name. Must match what the other side publishes under (`network.machineName`, defaulting to the hostname, or `<hostname>-<distro>` in WSL) |
| `endpoint` | **Optional.** `host:port` for TCP, or a directory path for a file link. Omit it entirely for a receive-only record — a machine you receive from but never send to. A record with no endpoint is never dialed; one with an endpoint always is |
| `desktop_index` | Virtual desktop this machine's sessions activate to — one number per machine, not per session |
| `muted` | Suppress toasts and sounds from this machine |
| `enabled` | Set false to keep the record but stop using the link |

**Connections window:** Right-click the controller icon → **Connections…** for a live view of every link in both directions — state, last delivery, last error — with **Add… / Edit… / Remove / Clear sessions** buttons. `Clear sessions` drops the session files that arrived from one machine without removing the link.

**From the shell:**

```bash
imrdy links           # a table of every link, both directions
imrdy links --json    # the same view model as JSON
```

`imrdy links` asks the running tray for live link health over its diagnostics pipe, and falls back to the records in `publishers.json` when no tray answers. Every run states which of the two it did, on its own `health:` line — with `--json` that line goes to stderr, so a pipe into `jq` gets only the payload.

That makes it a shell guard **conditionally**: with live health, a `Failed` link exits 1. Records-only, nothing can report as failed and the run always exits 0. Read the `health:` line before trusting the exit code — the pipe is off unless `diagnostics.ipcEnabled` is `true` in `config.json`, so records-only is the normal case on a shipped install, not a fault. On Linux it is always records-only: live health lives in the Windows tray, which that binary has no path to.

**Behavior of remote sessions:**

- Clicking one switches to its publisher's mapped desktop and stops there — there is no terminal window on this machine to focus. A WSL distro on *this* box is recognized as the same machine and gets ordinary local focusing.
- When a publisher's link drops, its session icons get a **dashed ring** and shrink slightly — a distinct treatment from the aging dim, so you can tell "stale" from "unreachable" at a glance.
  A publisher reached over TCP is unreachable the moment its connection drops. A WSL publisher writing through `/mnt/c` has no connection, so it writes a small heartbeat file into `~/.imrdy/heartbeats/` every five seconds instead; stopping the distro stops the heartbeat, and about twenty seconds later its sessions pick up the same treatment. A publisher that never writes a heartbeat is simply never marked unreachable — imrdy will not guess from silence.
- Sessions from a disconnected publisher are never removed automatically. Use **Clear sessions** in the Connections window when you want them gone.
- The tray tooltip reads `project@machine: session-name …` and the hover dashboard shows a machine chip beside the desktop chip.

`imrdy config set` does not cover the `network.*` keys — edit `~/.imrdy/config.json` directly.

**Publisher records are managed from the Connections window.** Hand-editing `~/.imrdy/publishers.json` is not a supported registration path: nothing watches that file, so a record you add by hand does not start dialing when you save it. The tray notices it only the next time one of this machine's own sessions changes, which may be minutes away or never. `Add…` / `Edit…` / `Remove` in the Connections window take effect immediately; on a headless Linux publisher, where there is no window, the daemon likewise picks a hand-edit up on its next local session event.

## Configuration

**File paths:**
| File | Location |
|------|----------|
| Config | `~/.imrdy/config.json` |
| Session state files | `~/.imrdy/sessions/*.json` |
| Workspace config | `~/.imrdy/workspaces.json` |
| Cross-machine links | `~/.imrdy/publishers.json` |
| Sound packs | `~/.imrdy/sounds/packs/` |
| Graphics packs | `~/.imrdy/graphics/packs/` |
| Logs | `~/.imrdy/logs/monitor.log` |
| Daemon log (Linux) | `~/.imrdy/logs/daemon_*.log` |
| Daemon lock (Linux) | `~/.imrdy/daemon.lock` + `daemon.pid` |

**Config schema (`~/.imrdy/config.json`):**
```json
{
  "tray": { "enabled": true, "iconStyle": "dots" },
  "sound": { "enabled": true, "defaultPack": "random", "disabledPacks": [] },
  "overlay": { "enabled": false, "position": "bottom-right", "size": 64, "spacing": 8, "monitor": 0, "locked": false, "offsetX": null, "offsetY": null },
  "diagnostics": { "ipcEnabled": null },
  "network": { "machineName": null, "authKey": null, "listenPort": 47600, "listenEnabled": false }
}
```

`network.machineName` is the name this machine publishes under; `null` resolves to the hostname at runtime (or `<hostname>-<distro>` inside WSL). `network.authKey` is a shared secret both ends must agree on. `network.listenPort` is clamped to 1–65535. See [Cross-Machine Sessions](#cross-machine-sessions).

`diagnostics.ipcEnabled` is a three-state `bool?`. `null` (default — omit from config) means the IPC server starts only when the `~/.imrdy/.dev-build` dev marker exists. Set `true` to enable in production; set `false` to disable even in dev.

**Environment variables:**
| Variable | Purpose |
|----------|---------|
| `IMRDY_HOME` | Override config/data directory (default: `~/.imrdy/`) |
| `IMRDY_NO_TRAY` | Set to `1` to suppress tray auto-spawn (headless CI, containers, SSH) |
| `IMRDY_LOG` | Set to `1` to enable debug logging |

Run `imrdy config path` to see full paths on your system.

## Building from Source

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (preview).

```bash
dotnet build
dotnet test
```

### Local Development

Build, deploy to `~/.local/bin/`, and restart the tray app in one step:

```bash
./build-dev.sh
```

This publishes the binary, copies it to `~/.local/bin/imrdy.exe`, and signals the running tray to stop. The next Claude Code hook event auto-spawns the updated binary.

On Linux the same script builds `Imrdy.Linux` to `~/.local/bin/imrdy`, stops a running publisher daemon, swaps the binary, and relaunches the daemon only if one was already running.

For a publish-only build without local deploy:

```bash
dotnet publish src/Imrdy.Windows/Imrdy.Windows.csproj -c Release
```

## Architecture

- **Imrdy.Core** — Platform-independent: state files, sound system, workspace management, menu models (Build/Apply pattern), validation, cross-machine publishing (sinks, wire protocol, daemon host), DI
- **Imrdy.Windows** — WinForms tray app (session icons + controller icon), menu rendering, COM virtual desktop interop, CLI commands, hook command
- **Imrdy.Linux** — Linux binary: hook, `imrdy daemon` (publisher), `imrdy links`. No UI.

Single executable via PublishSingleFile + SelfContained (no IL trimming — WinForms/COM incompatibility).

## License

MIT
