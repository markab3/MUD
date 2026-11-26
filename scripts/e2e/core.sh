#!/usr/bin/env bash
# Start or stop the THROWAWAY core of the e2e run (called by run.sh and by the driver's core-down phase).
# Usage: core.sh start|stop     Needs E2E_ENV_FILE (written by run.sh). Not for the live service.
set -euo pipefail
[ -r "${E2E_ENV_FILE:-}" ] || { echo "core.sh: E2E_ENV_FILE not set" >&2; exit 1; }
# shellcheck disable=SC1090
. "$E2E_ENV_FILE"
PIDFILE="$WORK/core.pid"

case "${1:-}" in
start)
    rm -f "$WORK/run/frontends.sock"
    : > "$WORK/core.log"    # a stale "SERVER STARTED" from an earlier run must not count
    ( cd "$REL" && exec ./MUD.Main >>"$WORK/core.log" 2>&1 < /dev/null ) &
    echo $! > "$PIDFILE"
    for _ in $(seq 1 80); do
        grep -q "SERVER STARTED" "$WORK/core.log" 2>/dev/null && [ -S "$WORK/run/frontends.sock" ] && { echo "core up (pid $(cat "$PIDFILE"))"; exit 0; }
        kill -0 "$(cat "$PIDFILE")" 2>/dev/null || { echo "core exited early" >&2; exit 1; }
        sleep 0.5
    done
    echo "core did not report started" >&2; exit 1 ;;
stop)
    [ -f "$PIDFILE" ] || exit 0
    pid="$(cat "$PIDFILE")"
    if kill -0 "$pid" 2>/dev/null; then
        kill -TERM "$pid" 2>/dev/null || true
        for _ in $(seq 1 60); do kill -0 "$pid" 2>/dev/null || break; sleep 0.5; done
        kill -9 "$pid" 2>/dev/null || true
    fi
    rm -f "$PIDFILE"
    # A restart must see a fresh start marker.
    : > "$WORK/core.log"
    echo "core stopped" ;;
*) echo "usage: core.sh start|stop" >&2; exit 2 ;;
esac
