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
`~/.imrdy/daemon.lock` and decides liveness from the **flock itself** — `flock -n <lock> true`
failing means a live holder — never from `~/.imrdy/daemon.pid`, and never from `kill -0` on that pid.
`kill -0` answers only "some process this user may signal holds that pid", which is a different
question; testing the flock is the same authority `DaemonLock.IsRunning` uses, so both sides agree on
one source of truth and the PID file stays what it always was, a convenience for addressing the
signal. It is validated against `^[1-9][0-9]*$` before reaching `kill`, because a leading `-` in
`kill`'s target position is a selector: `-1` would signal every process the invoking user owns.
Why the pid is not trustworthy here — measured 2026-09-11: on `SIGTERM` the .NET runtime raises `ProcessExit`
and then terminates the process at exit 143 **without unwinding `Main`**, so `DaemonLock.Dispose`
does not run, the kernel drops the `flock`, and `daemon.pid` is left behind naming a dead pid.
Harmless, because the lock is what the next daemon tests — but it means a stale `daemon.pid` is the
**routine** state after every SIGTERM stop, and a `wsl --terminate` plus distro restart replays the
pid namespace from 1 against a rootfs that kept the file, so that pid can be reused by an unrelated
same-user process. (`SIGINT` is the path that *does* unwind: `CancelKeyPress` sets
`e.Cancel = true`, so the process survives the signal.) It then polls up to 2s for the **lock** to be
released — a clean `SIGTERM` exit measured 203-208 ms, iteration 2 of 10 — swaps the binary, and
**relaunches only if one was already running**. `flock` (util-linux) is a hard requirement: absent,
the script exits 1 rather than falling back to `kill -0`, which would reinstate the guard it replaced. That asymmetry with the Windows branch
(which always respawns the tray) is deliberate: a box with no registered links has nothing to
publish, and the hook spawns the daemon on the next event once links exist, so an unconditional
launch would leave an idle process on every machine the script has ever run on.

Both branches drop the same `~/.imrdy/.dev-build` marker file (containing the repo root path) to
enable Debug logging — this part is platform-agnostic and already shared.

**Impact:** Anything added to Core that the daemon consumes has to be checked against both branches —
a Windows-only `./build-dev.sh` run proves nothing about the Linux path. CLAUDE.md's `## Build & Test`
section now documents the branching and the daemon cycle, so it is no longer a CLAUDE.md blind spot;
this page carries the *why* behind each choice (`ETXTBSY`, `SIGTERM` over `KILL`, conditional
relaunch, and the flock over both the PID file and `kill -0`).

**Source:** [build-dev.sh](../../../build-dev.sh)

**Discovered:** brainstorming/research — cross-machine session publishing layer investigation
**Impact:** Designers of any Linux-side imrdy component should extend build-dev.sh's existing Linux
branch rather than adding new OS-detection logic.
