#!/usr/bin/env bash
# Build, deploy to ~/.local/bin/, and restart the long-running process.
# Cross-platform: Windows (Git Bash / MSYS) builds Imrdy.Windows and restarts the
# tray; Linux builds Imrdy.Linux and restarts the publisher daemon.
# Usage: ./build-dev.sh [rid]
#   rid defaults to win-x64 on Windows and linux-x64 on Linux.
set -euo pipefail

case "$(uname -s)" in
    Linux*)               PLATFORM=linux ;;
    MINGW*|MSYS*|CYGWIN*) PLATFORM=windows ;;
    *) echo "ERROR: unsupported platform $(uname -s)" >&2; exit 1 ;;
esac

if [[ "$PLATFORM" == "windows" ]]; then
    PROJECT="src/Imrdy.Windows/Imrdy.Windows.csproj"
    RID="${1:-win-x64}"
    DEST="$HOME/.local/bin/imrdy.exe"
    PUBLISH_PATH_GLOB="*/${RID}/publish/imrdy.exe"
else
    PROJECT="src/Imrdy.Linux/Imrdy.Linux.csproj"
    RID="${1:-linux-x64}"
    DEST="$HOME/.local/bin/imrdy"
    PUBLISH_PATH_GLOB="*/${RID}/publish/imrdy"
fi

# 1. Publish.
dotnet publish "$PROJECT" -c Release -r "$RID"

# 2. Find the publish output (avoids hardcoding the TFM).
#    Pick the newest match — stale TFM dirs from prior builds can otherwise win
#    the alphabetical race (e.g. net10.0-windows vs net10.0-windows10.0.17763.0).
PUBLISH_BIN=$(find "$(dirname "$PROJECT")/bin/Release" -path "$PUBLISH_PATH_GLOB" -type f -printf '%T@ %p\n' \
    | sort -nr | head -1 | cut -d' ' -f2-)
if [[ -z "$PUBLISH_BIN" ]]; then
    echo "ERROR: published binary not found at $PUBLISH_PATH_GLOB" >&2
    exit 1
fi

mkdir -p "$(dirname "$DEST")"

if [[ "$PLATFORM" == "windows" ]]; then
    # 3. Stop gracefully, then force-kill any stragglers (hook respawns).
    "$DEST" stop 2>/dev/null || true
    taskkill //IM imrdy.exe //F > /dev/null 2>&1 || true

    # 4. Rename the old binary out of the way (works even if briefly locked),
    #    then copy the new one in. Hooks that fire during the gap fail harmlessly.
    mv "$DEST" "$DEST.old" 2>/dev/null || true
    cp "$PUBLISH_BIN" "$DEST"
    rm -f "$DEST.old"
else
    # 3. Stop a running daemon so the new binary is the one that comes back.
    #
    #    Liveness is decided by the LOCK, never by the PID file. The two answer different
    #    questions: the flock says "a daemon is up", while `kill -0 $pid` says only that
    #    *some* process this user may signal holds that pid. SIGTERM is now intercepted by a
    #    PosixSignalRegistration in RunDaemon, so a clean stop unwinds Main and
    #    DaemonLock.Dispose removes daemon.pid itself — but every abnormal exit still leaves
    #    that file behind naming a dead pid: SIGKILL, a `wsl --terminate`, a crash. The lock
    #    is the authority on both paths, which is why it is tested rather than the pid.
    #    After a `wsl --terminate` and a distro restart the pid namespace begins again at 1
    #    while the rootfs keeps that file, so the stale pid can be reused by an unrelated
    #    same-user process and `kill -0` would happily confirm it. Testing the flock is the
    #    same authority DaemonLock.IsRunning uses on the C# side, which is the point: one
    #    source of truth for liveness on both sides, with the PID file demoted to what it
    #    has always actually been — a convenience for addressing the signal.
    DAEMON_LOCK_FILE="$HOME/.imrdy/daemon.lock"
    DAEMON_PID_FILE="$HOME/.imrdy/daemon.pid"
    DAEMON_WAS_RUNNING=0

    if ! command -v flock > /dev/null 2>&1; then
        echo "ERROR: flock (util-linux) is required to tell a live daemon from a stale pid file." >&2
        echo "       Install it, or stop the daemon yourself before rerunning." >&2
        exit 1
    fi

    # Non-blocking acquire: it FAILS while a holder is alive, so failure is the liveness
    # signal. The -f test keeps flock from creating the lock file when none exists.
    daemon_lock_held() {
        [[ -f "$DAEMON_LOCK_FILE" ]] || return 1
        ! flock -n "$DAEMON_LOCK_FILE" true 2>/dev/null
    }

    # The ONE refusal path. Both ways a stop can fail end here: the daemon refusing to die,
    # and a held lock whose holder cannot be addressed. They differ only in the reason, so
    # they must not grow two exits — the property that matters is identical, and it is the
    # behaviour this script is held to: never report success it did not achieve. Called only
    # before the swap, so a refusal leaves no new binary on disk and prints no success line.
    refuse_deploy() {
        echo "ERROR: $1" >&2
        echo "       Refusing to deploy: swapping the binary now would leave the old daemon" >&2
        echo "       running while this script reported a successful deploy, and you would" >&2
        echo "       then be testing the new binary against the old one." >&2
        echo "       Find the holder of $DAEMON_LOCK_FILE and stop it, then rerun." >&2
        exit 1
    }

    if daemon_lock_held; then
        DAEMON_PID=$(cat "$DAEMON_PID_FILE" 2>/dev/null || true)
        # The pid crosses from a file straight into kill's target position, where a leading
        # "-" is a selector rather than a pid: "-1" means every process this user owns, and
        # "-1234" means process group 1234. Nothing writes such a value today — the only
        # writer is Environment.ProcessId — but the value is unvalidated input in an
        # argument position and the blast radius is the whole login session.
        if [[ "$DAEMON_PID" =~ ^[1-9][0-9]*$ ]]; then
            DAEMON_WAS_RUNNING=1
            kill -TERM "$DAEMON_PID" 2>/dev/null || true
            # Wait for the LOCK to be released, not for the pid to disappear — the lock is
            # what the replacement daemon will contend for. The one measurement available is
            # 203 ms, iteration 2 of 10 — but it was taken on the OLD exit-143 path, where the
            # kernel dropped the flock on process death. Release now runs through
            # DaemonLock.Dispose instead, behind TcpSink.Dispose's _dialLoop.Wait, and that
            # figure has not been re-measured there. So this budget is NOT validated against
            # the path it now guards: the escalation below is what covers the tail, and it is
            # load-bearing rather than belt-and-braces. Re-measure before trusting 2 s.
            for _ in 1 2 3 4 5 6 7 8 9 10; do
                daemon_lock_held || break
                sleep 0.2
            done

            # Escalate rather than assume. SIGTERM no longer kills the daemon by default:
            # RunDaemon intercepts it with a PosixSignalRegistration that sets
            # ctx.Cancel = true, so the process dies only if it manages to unwind itself,
            # and the unwind runs behind TcpSink.Dispose's _dialLoop.Wait(DisposeGrace) —
            # bounded at 2s per sink, which is this poll's entire budget. Without an
            # escalation the script would fall through to the swap and then print
            # "Daemon relaunched." while the OLD binary still held the lock: the relaunched
            # process hits DaemonLock.TryAcquire -> null -> ExitAlreadyRunning, which is
            # exit 0 logged to the daemon log rather than to this terminal, so the lie is
            # silent and the developer then tests the new binary against the old one.
            if daemon_lock_held; then
                echo "WARNING: daemon $DAEMON_PID did not release $DAEMON_LOCK_FILE within 2s" >&2
                echo "         of SIGTERM. Escalating to SIGKILL." >&2
                kill -KILL "$DAEMON_PID" 2>/dev/null || true
                for _ in 1 2 3 4 5 6 7 8 9 10; do
                    daemon_lock_held || break
                    sleep 0.2
                done
            fi

            # Abort BEFORE the swap: the DEPLOY is what stays atomic. Nothing has been written
            # to $DEST or to the marker at this point, so no new binary is left behind and no
            # relaunch line prints. The box is not otherwise untouched — the publish output
            # exists, and the daemon that reached this line has been TERMed and KILLed.
            if daemon_lock_held; then
                refuse_deploy "$DAEMON_LOCK_FILE is still held after SIGTERM and SIGKILL."
            fi
        else
            # The same refusal, reached by a different door. The script has just measured two
            # things — a daemon is up, and it cannot be addressed — so continuing would swap
            # the binary and then print "no daemon was running" to stdout, contradicting a
            # warning it wrote to stderr in the same run. A developer reads stdout. Lead
            # ruling, checkpoint 29: "never report success it did not achieve" covers the
            # whole script, not just the SIGTERM path, and an unlikely trigger is an argument
            # about reaching this branch rather than about what it does when reached.
            refuse_deploy "$DAEMON_PID_FILE does not hold a plain pid, so the daemon holding $DAEMON_LOCK_FILE cannot be signalled."
        fi
    fi

    # 4. Atomic swap via temp-in-same-dir + mv. Avoids ETXTBSY if a concurrent
    #    hook process is mid-exec on the old binary.
    TMP="${DEST}.new.$$"
    install -m 0755 "$PUBLISH_BIN" "$TMP"
    mv -f "$TMP" "$DEST"
fi

# 5. Drop a dev-build marker so the hook/tray defaults to Debug logging.
#    ServiceRegistration.AddSerilog reads ~/.imrdy/.dev-build to flip the level
#    without requiring IMRDY_LOG=1 in every shell that triggers a hook.
#    The file body contains the repo root so the tray's Manage → Dev menu
#    can enumerate fixtures from tests/fixtures/dashboards/.
#    Remove the file (rm ~/.imrdy/.dev-build) to test prod-like log levels.
mkdir -p "$HOME/.imrdy"
if [[ "$PLATFORM" == "windows" ]]; then
    # `pwd -W` yields the Windows-form path (D:/…) that .NET understands.
    # Plain $PWD is MSYS form (/d/…) which fails Directory.Exists on the tray side.
    REPO_ROOT=$(pwd -W 2>/dev/null || pwd)
else
    REPO_ROOT="$PWD"
fi
printf '%s\n' "$REPO_ROOT" > "$HOME/.imrdy/.dev-build"

# 6. On Windows, spawn the tray immediately so we don't wait for the next
#    Claude hook event to respawn it. `cmd //c start` detaches the process
#    from this shell's tree, so the tray survives after build-dev.sh exits
#    and is not tied to the Claude process that invoked the script.
#    On Linux there is no tray — the hook binary is invoked per event.
#    On Linux the daemon is relaunched only if one was already running. A box with no
#    registered links has nothing to publish, and the hook spawns the daemon on the next
#    event once links exist — so starting one unconditionally here would leave an idle
#    process on every machine this script has ever been run on.
if [[ "$PLATFORM" == "windows" ]]; then
    cmd //c start "" "$DEST" >/dev/null 2>&1
    echo "Deployed and relaunched. (Debug logging enabled via ~/.imrdy/.dev-build)"
elif [[ "$DAEMON_WAS_RUNNING" == "1" ]]; then
    "$DEST" daemon >/dev/null 2>&1 &
    disown 2>/dev/null || true
    echo "Deployed $DEST ($RID). Daemon relaunched. (Debug logging enabled via ~/.imrdy/.dev-build)"
else
    echo "Deployed $DEST ($RID). Hook ready; no daemon was running. (Debug logging enabled via ~/.imrdy/.dev-build)"
fi
