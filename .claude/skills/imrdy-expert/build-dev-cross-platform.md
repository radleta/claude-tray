---
tags: [imrdy-expert/build, imrdy-expert/linux]
summary: "build-dev.sh OS-detects and publishes a Linux binary to ~/.local/bin/imrdy — atomic swap via temp-in-same-dir + mv, plus a SIGTERM/relaunch-if-it-was-running cycle for the publisher daemon"
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

On Linux it publishes `src/Imrdy.Linux/Imrdy.Linux.csproj` to `~/.local/bin/imrdy` (no `.exe`). The
binary swap uses a different pattern than Windows' rename-old-then-copy:
`install -m 0755 "$PUBLISH_BIN" "${DEST}.new.$$"` then `mv -f` the temp file over the destination —
an atomic same-directory swap chosen specifically to avoid `ETXTBSY` if a concurrent hook process is
mid-exec on the old binary (a hazard that does not apply on Windows, where a locked `.exe` fails to
overwrite instead).

There *is* now a long-lived Linux process to cycle: the publisher daemon. The Linux branch reads
`~/.imrdy/daemon.pid` (written beside `daemon.lock`, and cleared by `DaemonLock` on a clean exit, so
a live PID there means a daemon was up), sends `SIGTERM` — not `KILL`, so the lock is released
through `Dispose` rather than dropped by the kernel — polls up to 2s for the process to go, swaps the
binary, and **relaunches only if one was already running**. That asymmetry with the Windows branch
(which always respawns the tray) is deliberate: a box with no registered links has nothing to
publish, and the hook spawns the daemon on the next event once links exist, so an unconditional
launch would leave an idle process on every machine the script has ever run on.

Both branches drop the same `~/.imrdy/.dev-build` marker file (containing the repo root path) to
enable Debug logging — this part is platform-agnostic and already shared.

**Impact:** Anything added to Core that the daemon consumes has to be checked against both branches —
a Windows-only `./build-dev.sh` run proves nothing about the Linux path. CLAUDE.md's `## Build & Test`
section now documents the branching and the daemon cycle, so it is no longer a CLAUDE.md blind spot;
this page carries the *why* behind each choice (`ETXTBSY`, `SIGTERM` over `KILL`, conditional
relaunch).

**Source:** [build-dev.sh](../../../build-dev.sh)

**Discovered:** brainstorming/research — cross-machine session publishing layer investigation
**Impact:** Designers of any Linux-side imrdy component should extend build-dev.sh's existing Linux
branch rather than adding new OS-detection logic.
