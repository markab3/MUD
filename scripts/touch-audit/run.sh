#!/usr/bin/env bash
# Builds the stand-in core and the site, starts both on loopback, runs audit.js, stops them.
# Usage: scripts/touch-audit/run.sh [outDir]
# Needs dotnet 8, node, the playwright npm package (PLAYWRIGHT_MODULE=/path/to/playwright if not resolvable) and chromium.
set -euo pipefail
here="$(cd "$(dirname "$0")" && pwd)"; root="$(cd "$here/../.." && pwd)"
out="${1:-$root/touch-audit-out}"; tmp="$(mktemp -d)"
export MUD_RUN_DIRECTORY="$tmp/run" MUD_FRONTENDS_SOCKET="$tmp/run/frontends.sock" MUD_WEB_URL="http://127.0.0.1:5281"
mkdir -p "$MUD_RUN_DIRECTORY"
dotnet build "$here/StandinCore" -o "$tmp/core" -v q >/dev/null
dotnet build "$root/MUD.Web" -o "$tmp/web" -v q >/dev/null
pids=(); trap 'kill "${pids[@]}" 2>/dev/null || true; rm -rf "$tmp"' EXIT
dotnet "$tmp/core/StandinCore.dll" >"$tmp/core.log" 2>&1 & pids+=($!)
for i in $(seq 30); do [ -S "$MUD_FRONTENDS_SOCKET" ] && break; sleep 0.5; done
(cd "$root/MUD.Web" && exec dotnet "$tmp/web/MUD.Web.dll" >"$tmp/web.log" 2>&1) & pids+=($!)
for i in $(seq 60); do curl -fs -o /dev/null "$MUD_WEB_URL/login" && break; sleep 0.5; done
node "$here/audit.js" "$out" "$MUD_WEB_URL"
