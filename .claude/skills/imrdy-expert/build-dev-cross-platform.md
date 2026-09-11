---
tags: [imrdy-expert/build, imrdy-expert/linux]
summary: "build-dev.sh OS-detects and publishes a Linux binary to ~/.local/bin/imrdy — atomic swap via temp-in-same-dir + mv, and a daemon stop sequence that escalates SIGTERM to SIGKILL and then refuses to deploy (exit 1) rather than relaunching over a daemon it could not stop"
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
Why the pid is not trustworthy here: `RunDaemon` intercepts `SIGINT` and `SIGTERM` with a
`PosixSignalRegistration` pair whose handler sets `ctx.Cancel = true`, so a clean stop unwinds `Main`
and `DaemonLock.Dispose` removes `daemon.pid` itself. Every other way out does not: `SIGKILL`, a
`wsl --terminate`, an unhandled crash, and signals outside that pair (`SIGHUP`, `SIGQUIT`) all
terminate without unwinding, leaving the kernel to drop the `flock` and `daemon.pid` behind naming a
dead pid. A `wsl --terminate` plus distro restart then replays the pid namespace from 1 against a
rootfs that kept the file, so that pid can be reused by an unrelated same-user process. The lock is
the authority on both paths, which is the whole reason it is what gets tested. (History, because it
shaped this page: before the signal registration landed, `SIGINT` did not stop the daemon at all —
`Console.CancelKeyPress` was measured not to dispatch on Linux — and `SIGTERM` exited at 143 without
unwinding, so a stale `daemon.pid` was the routine state after every stop.) It then polls up to 2s for the **lock** to be
released — 203-208 ms, iteration 2 of 10, but read that figure with its provenance: it was measured
on the **old** exit-143 path, where the kernel dropped the `flock` on process death. The signal
registration replaced that with a real unwind, so release now runs behind `SinkRegistry.Dispose` →
`TcpSink.Dispose`, whose `_dialLoop.Wait(DisposeGrace)` is bounded at 2 s *per sink* — the poll's
entire budget. The common case is still milliseconds, because both awaits in `DialLoopAsync` observe
the token; the tail is what the escalation described next exists for. Re-measure this number on the
new path before sizing anything against it.

**When the poll runs out, the script escalates and can refuse to deploy.** This is the most
consequential thing the stop sequence does, and hitting it is the designed outcome rather than a
regression: if the lock is still held after the ten iterations it warns, sends `SIGKILL`, and polls
ten more times; if it is *still* held it prints an error naming the lock file and `exit 1` **before**
the `install`/`mv` swap, so a refusal leaves nothing deployed and prints no relaunch line. Both
escalation stages sit inside the same `^[1-9][0-9]*$` pid guard as the original `kill -TERM`, so the
new kill inherits it, and all four liveness tests in the sequence are the same `flock` probe — the
`kill -0` this build removed did not come back.

**There are two doors into that refusal and exactly one refusal.** The second is the malformed-pid
case: the lock is held but `daemon.pid` does not hold a plain pid, so the script knows a daemon is up
*and* that it cannot address it. That branch used to warn on stderr, swap the binary anyway, and then
print `"Hook ready; no daemon was running."` to stdout — contradicting its own warning, in the run
that wrote it, on the stream a developer actually reads. It now calls the same `refuse_deploy` with a
different reason. Both paths share one exit rather than growing two, because the property being
enforced is identical: *the script must never report success it did not achieve*, and that covers the
whole script rather than the SIGTERM path alone. The realistic trigger is benign — `DaemonLock.Dispose`
deletes the PID file *before* releasing the lock, so a script starting inside that window reads an
empty pid against a still-held lock — but an unlikely trigger is an argument about reaching the
branch, not about what it does once reached.

Why the escalation had to exist at all: `RunDaemon` intercepts SIGTERM with `ctx.Cancel = true`,
which removes the kernel's default action, so `kill -TERM` no longer guarantees the daemon dies — it
dies only if it unwinds itself. Without an escalation the script fell through to the swap and printed
`"Daemon relaunched."` while the old binary still held the lock, because the relaunched process hits
`DaemonLock.TryAcquire` → null → `ExitAlreadyRunning`, which is exit **0** logged to the daemon log
rather than to the terminal. The lie was silent, and a developer then tested the new binary against
the old one. SIGKILL is safe to escalate to precisely because the kernel drops the advisory `flock`
on process death; a second poll that still fails therefore means a *different* holder, which is a
case for a human rather than one to paper over.

After a successful stop the script swaps the binary and
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
this page carries the *why* behind each choice (`ETXTBSY`, `SIGTERM` first and `SIGKILL` as an
escalation with a refuse-before-swap abort behind it, conditional relaunch, and the flock over both
the PID file and `kill -0`).

**Source:** [build-dev.sh](../../../build-dev.sh)

**Discovered:** brainstorming/research — cross-machine session publishing layer investigation
**Impact:** Designers of any Linux-side imrdy component should extend build-dev.sh's existing Linux
branch rather than adding new OS-detection logic.
