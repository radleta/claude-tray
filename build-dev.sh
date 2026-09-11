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
    #    *some* process this user may signal holds that pid. Those diverge routinely, not
    #    exceptionally — measured, on SIGTERM the .NET runtime runs the ProcessExit handler
    #    and then terminates the process at 143 without unwinding Main, so
    #    DaemonLock.Dispose never runs and daemon.pid is left behind naming a dead pid.
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
            # what the replacement daemon will contend for. Sized against a measurement, not
            # a guess: a clean SIGTERM exit released it in 203 ms, iteration 2 of 10.
            for _ in 1 2 3 4 5 6 7 8 9 10; do
                daemon_lock_held || break
                sleep 0.2
            done
        else
            echo "WARNING: a daemon holds $DAEMON_LOCK_FILE but $DAEMON_PID_FILE does not" >&2
            echo "         hold a plain pid, so it cannot be signalled. Leaving it running on" >&2
            echo "         the old binary; stop it yourself and rerun." >&2
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
