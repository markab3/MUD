#!/usr/bin/env bash
# End-to-end verification of signed-in website circuits against a REAL core and a THROWAWAY Mongo (specs/WEB_BLAZOR.md, Appendix F).
#
# Starts: mongod (spare port, temp dbpath), MUD.Main (spare run directory, own database name), the website (spare port), seeds two
# creator accounts with scripts/seed-admin.sh and RANDOM passwords, generates a small land with the creator commands, then drives
# headless Chromium (Playwright) through scripts/e2e/driver.mjs. Never touches the live database, ports 27017/5080/7680, the
# live run directory or any service. Passwords exist only in this process's environment: they are never printed or written to a file.
#
# Needs: dotnet 8 SDK, mongod and the mongo/mongosh shell, python3, node and the 'playwright' npm package with a Chromium
# (npm i playwright && npx playwright install chromium). Set E2E_NODE_MODULES to the node_modules directory holding playwright.
#
# Environment (all optional):
#   E2E_OUT            screenshots and report directory   (default scripts/e2e/out, git-ignored)
#   E2E_MONGO_PORT     default 27391    E2E_WEB_PORT default 5391    E2E_PROXY_PORT default 5392    E2E_TELNET_PORT default 7691
#   E2E_SKIP_BUILD=1   reuse the publish output from a previous run with E2E_KEEP=1
#   E2E_KEEP=1         keep the work directory (published binaries, logs, db); mongod and the others are always stopped
#   E2E_PHASES         comma list of driver phases (main,roles,disconnect,coredown); default all
set -euo pipefail

HERE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO="$(cd "$HERE/../.." && pwd)"
die() { echo "e2e: $*" >&2; exit 1; }

MONGO_PORT="${E2E_MONGO_PORT:-27391}"
WEB_PORT="${E2E_WEB_PORT:-5391}"
PROXY_PORT="${E2E_PROXY_PORT:-5392}"
TELNET_PORT="${E2E_TELNET_PORT:-7691}"
case "$MONGO_PORT:$WEB_PORT:$PROXY_PORT:$TELNET_PORT" in
    *27017*|*:5080:*|*:5080|*7680*) die "refusing to use a live port (27017, 5080 or 7680)";;
esac
for port in "$MONGO_PORT" "$WEB_PORT" "$PROXY_PORT" "$TELNET_PORT"; do
    if (exec 3<>"/dev/tcp/127.0.0.1/$port") 2>/dev/null; then die "port $port is already in use"; fi
done

export OUT="${E2E_OUT:-$HERE/out}"
mkdir -p "$OUT"
WORK="${E2E_WORK:-${TMPDIR:-/tmp}/mud-e2e}"
if [ -z "${E2E_SKIP_BUILD:-}" ]; then rm -rf "$WORK"; fi
mkdir -p "$WORK/db" "$WORK/run"
chmod 700 "$WORK"

DBNAME="e2e_$(od -An -N4 -tx1 /dev/urandom | tr -d ' \n')"
export DOTNET_ROOT="${DOTNET_ROOT:-$(dirname "$(readlink -f "$(command -v dotnet)")")}"
rand() { head -c 24 /dev/urandom | base64 | tr -dc 'A-Za-z0-9' | head -c 16; }
export E2E_PW="$(rand)" E2E_PW2="$(rand)"          # environment only, never printed or stored
export E2E_USER=eetocreator E2E_USER2=eetolowered

MONGO_URI="mongodb://127.0.0.1:$MONGO_PORT"
SHELLBIN="$(command -v mongosh || command -v mongo || true)"
[ -n "$SHELLBIN" ] || die "no mongo shell (mongosh or mongo) on PATH"
command -v mongod >/dev/null || die "mongod not on PATH"

# Hook environment for core.sh (no secrets in it: the throwaway Mongo has no credentials and listens on 127.0.0.1 only).
cat > "$WORK/env" <<EOF
export MUD_MONGO_URI="$MONGO_URI"
export MUD_RUN_DIRECTORY="$WORK/run"
export MUD_FRONTEND_TOKEN_FILE="$WORK/run/token"
export DOTNET_ROOT="$DOTNET_ROOT"
export REL="$WORK/rel"
export WORK="$WORK"
EOF
export E2E_ENV_FILE="$WORK/env"

PIDS=()
cleanup() {
    set +e
    bash "$HERE/core.sh" stop >/dev/null 2>&1
    for pid in "${PIDS[@]}"; do kill "$pid" 2>/dev/null; done
    sleep 1
    for pid in "${PIDS[@]}"; do kill -9 "$pid" 2>/dev/null; done
    if [ -z "${E2E_KEEP:-}" ]; then rm -rf "$WORK"; else echo "e2e: kept $WORK"; fi
}
trap cleanup EXIT

# 1. Build (the same publish steps as scripts/deploy.sh, into the work directory).
if [ -z "${E2E_SKIP_BUILD:-}" ] || [ ! -f "$WORK/rel/MUD.Main.dll" ]; then
    echo "e2e: publishing"
    dotnet publish "$REPO/MUD.Main/MUD.Main.csproj" -c Release -o "$WORK/rel" >"$WORK/build.log" 2>&1 || { tail -20 "$WORK/build.log"; die "publish MUD.Main failed"; }
    dotnet publish "$REPO/MUD.PlanarRealms/MUD.PlanarRealms.csproj" -c Release -o "$WORK/rel" >>"$WORK/build.log" 2>&1 || die "publish PlanarRealms failed"
    dotnet publish "$REPO/MUD.Web/MUD.Web.csproj" -c Release -o "$WORK/rel/web" >>"$WORK/build.log" 2>&1 || die "publish MUD.Web failed"
    dotnet publish "$REPO/MUD.Telnet.Host/MUD.Telnet.Host.csproj" -c Release -o "$WORK/rel/telnet" >>"$WORK/build.log" 2>&1 || die "publish Telnet.Host failed"
    # Own database; import the sample content at start so there are rooms and a start room.
    sed -i "s/\"DatabaseName\": *\"[^\"]*\"/\"DatabaseName\": \"$DBNAME\"/; s/\"ImportOnStartup\": *false/\"ImportOnStartup\": true/" "$WORK/rel/appsettings.json"
fi
grep -q "$DBNAME" "$WORK/rel/appsettings.json" 2>/dev/null || sed -i "s/\"DatabaseName\": *\"[^\"]*\"/\"DatabaseName\": \"$DBNAME\"/" "$WORK/rel/appsettings.json"
echo "e2e: database name $DBNAME on port $MONGO_PORT"

# 2. Throwaway Mongo.
mongod --dbpath "$WORK/db" --port "$MONGO_PORT" --bind_ip 127.0.0.1 --logpath "$WORK/mongod.log" >/dev/null 2>&1 &
PIDS+=($!)
for _ in $(seq 1 40); do "$SHELLBIN" --port "$MONGO_PORT" --quiet --eval 'db.runCommand({ping:1}).ok' >/dev/null 2>&1 && break; sleep 0.5; done
"$SHELLBIN" --port "$MONGO_PORT" --quiet --eval 'db.runCommand({ping:1}).ok' >/dev/null 2>&1 || die "throwaway mongod did not start (see $WORK/mongod.log)"

# 3. Core.
bash "$HERE/core.sh" start || { tail -20 "$WORK/core.log"; die "core did not start"; }

# 4. Seed two creator accounts (random passwords, from the environment).
export MUD_MONGO_URI="$MONGO_URI" MUD_SEED_DATABASE="$DBNAME"
MUD_SEED_NAME="$E2E_USER" MUD_SEED_PASSWORD="$E2E_PW" bash "$REPO/scripts/seed-admin.sh"
MUD_SEED_NAME="$E2E_USER2" MUD_SEED_PASSWORD="$E2E_PW2" bash "$REPO/scripts/seed-admin.sh"
# The second account is a plain creator (not admin) so the first can lower it from the website.
"$SHELLBIN" --port "$MONGO_PORT" --quiet "$DBNAME" --eval 'db.player.updateOne({NameKey:"eetolowered"},{$set:{"roles.admin":false}})' >/dev/null
unset MUD_MONGO_URI MUD_SEED_DATABASE

# 5. Website.
( cd "$WORK/rel/web" && MUD_WEB_URL="http://127.0.0.1:$WEB_PORT" MUD_RUN_DIRECTORY="$WORK/run" exec ./MUD.Web >"$WORK/web.log" 2>&1 ) &
PIDS+=($!)
for _ in $(seq 1 60); do curl -fs -o /dev/null "http://127.0.0.1:$WEB_PORT/login" && break; sleep 0.5; done
curl -fs -o /dev/null "http://127.0.0.1:$WEB_PORT/login" || { tail -20 "$WORK/web.log"; die "website did not start"; }

# 6. Generated land (creator commands over a throwaway telnet host), so the regen/update/map steps have generated rooms.
( cd "$WORK/rel/telnet" && source "$WORK/env" && MUD_TELNET_BIND_ADDRESS=127.0.0.1 MUD_TELNET_PORT="$TELNET_PORT" exec ./MUD.Telnet.Host >"$WORK/telnet.log" 2>&1 ) &
TELNET_PID=$!
PIDS+=($TELNET_PID)
for _ in $(seq 1 40); do (exec 3<>"/dev/tcp/127.0.0.1/$TELNET_PORT") 2>/dev/null && break; sleep 0.5; done
python3 -I "$HERE/gen_rooms.py" "$TELNET_PORT" >"$WORK/gen.log" 2>&1 || { tail -20 "$WORK/gen.log"; die "could not generate rooms"; }
kill "$TELNET_PID" 2>/dev/null || true
GENERATED="$("$SHELLBIN" --port "$MONGO_PORT" --quiet "$DBNAME" --eval 'db.room.count({gen:{$exists:true}})')"
echo "e2e: generated rooms: $GENERATED"
[ "${GENERATED:-0}" -gt 0 ] || die "no generated rooms were created"

# 7. Driver.
if [ -z "${E2E_NODE_MODULES:-}" ]; then
    E2E_NODE_MODULES="$(ls -d "$HOME"/.npm/_npx/*/node_modules 2>/dev/null | while read -r d; do [ -d "$d/playwright" ] && echo "$d" && break; done || true)"
fi
[ -n "${E2E_NODE_MODULES:-}" ] || die "set E2E_NODE_MODULES to a node_modules directory containing playwright"
export E2E_NODE_MODULES E2E_BASE="http://127.0.0.1:$WEB_PORT" E2E_WEB_PORT="$WEB_PORT" E2E_PROXY_PORT="$PROXY_PORT"
export E2E_CORE_STOP="bash $HERE/core.sh stop" E2E_CORE_START="bash $HERE/core.sh start"
rc=0
node "$HERE/driver.mjs" || rc=$?
echo "e2e: driver exit code $rc; report in $OUT/report.md"
exit "$rc"
