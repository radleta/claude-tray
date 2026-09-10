---
tags: [imrdy-expert/build, imrdy-expert/linux]
summary: "build-dev.sh already OS-detects and publishes a Linux hook-only binary to ~/.local/bin/imrdy — no tray, atomic swap via temp-in-same-dir + mv"
---

## build-dev.sh Is Already Cross-Platform

`build-dev.sh` (repo root) detects the host OS via `uname -s` and branches the entire publish/deploy
sequence — this is not something a cross-machine publishing design needs to add from scratch.

```bash
case "$(uname -s)" in
    Linux*)               PLATFORM=linux ;;
    MINGW*|MSYS*|CYGWIN*) PLATFORM=windows ;;
esac
```

On Windows it publishes `src/Imrdy.Windows/Imrdy.Windows.csproj` to `~/.local/bin/imrdy.exe`, stops
the running tray (`imrdy stop` + `taskkill //IM imrdy.exe //F`), and relaunches it detached via
`cmd //c start`.

On Linux it publishes `src/Imrdy.Linux/Imrdy.Linux.csproj` to `~/.local/bin/imrdy` (no `.exe`, no
tray to stop/relaunch — the comment is explicit: "hook-only — no tray"). The binary swap uses a
different pattern than Windows' rename-old-then-copy: `install -m 0755 "$PUBLISH_BIN" "${DEST}.new.$$"`
then `mv -f` the temp file over the destination — an atomic same-directory swap chosen specifically
to avoid `ETXTBSY` if a concurrent hook process is mid-exec on the old binary (a hazard that does not
apply on Windows, where a locked `.exe` fails to overwrite instead).

Both branches drop the same `~/.imrdy/.dev-build` marker file (containing the repo root path) to
enable Debug logging — this part is platform-agnostic and already shared.

**Impact:** A design adding a Linux publisher daemon does not need to invent OS detection or a Linux
install path in `build-dev.sh` — both already exist and just need the new daemon's project file
added to the Linux branch's publish step. CLAUDE.md's `## Build & Test` section documents only the
single `./build-dev.sh` invocation and does not mention this cross-platform branching, so a reader
relying on CLAUDE.md alone would miss that Linux support already exists.

**Source:** [build-dev.sh](../../../build-dev.sh)

**Discovered:** brainstorming/research — cross-machine session publishing layer investigation
**Impact:** Designers of any Linux-side imrdy component should extend build-dev.sh's existing Linux
branch rather than adding new OS-detection logic.
