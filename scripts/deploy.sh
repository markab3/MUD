#!/usr/bin/env bash
# Deploy Planar Realms (MUD.Main) as a versioned release and restart the user service.
#
# Usage: scripts/deploy.sh [ref]          (default ref: hermes)
#        scripts/deploy.sh --rollback     (switch back to the previous release)
#
# Environment overrides:
#   MUD_DEPLOY_ROOT   install root            (default: $HOME/.local/share/mud)
#   MUD_SERVICE       systemd user unit name   (default: planar-realms)
#   MUD_KEEP_RELEASES releases to retain       (default: 5, minimum 2)
#   MUD_REPO_URL      remote to fetch from     (default: origin of the repo holding this script)
set -euo pipefail

DEPLOY_ROOT="${MUD_DEPLOY_ROOT:-$HOME/.local/share/mud}"
SERVICE="${MUD_SERVICE:-planar-realms}"
KEEP="${MUD_KEEP_RELEASES:-5}"
RELEASES="$DEPLOY_ROOT/releases"
CURRENT="$DEPLOY_ROOT/current"

die() { echo "deploy: $*" >&2; exit 1; }

# Atomically point $CURRENT at the given release directory.
switch_to() {
    ln -sfn "$1" "$CURRENT.new"
    mv -T "$CURRENT.new" "$CURRENT"
}

restart_service() {
    systemctl --user restart "$SERVICE"
}

print_rollback() {
    echo "To roll back:  $0 --rollback"
    echo "  (or manually: ln -sfn <previous release dir> $CURRENT && systemctl --user restart $SERVICE)"
}

rollback() {
    [ -L "$CURRENT" ] || die "no current release to roll back from"
    local cur prev
    cur="$(readlink -f "$CURRENT")"
    prev=""
    # Newest-first list of releases other than the current one.
    while IFS= read -r d; do
        if [ "$d" != "$cur" ]; then prev="$d"; break; fi
    done < <(find "$RELEASES" -mindepth 1 -maxdepth 1 -type d | sort -r)
    [ -n "$prev" ] || die "no previous release found in $RELEASES"
    echo "Rolling back: $cur -> $prev"
    switch_to "$prev"
    restart_service
    echo "Rolled back to $(basename "$prev"). Previous (bad) release kept at $cur."
}

if [ "${1:-}" = "--rollback" ]; then
    rollback
    exit 0
fi
if [ "${1:-}" = "-h" ] || [ "${1:-}" = "--help" ]; then
    sed -n '2,12p' "$0"
    exit 0
fi

REF="${1:-hermes}"
command -v dotnet >/dev/null 2>&1 || die "dotnet SDK not found in PATH"
command -v git >/dev/null 2>&1 || die "git not found in PATH"

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
SRC_REPO="$(git -C "$SCRIPT_DIR" rev-parse --show-toplevel)"
REPO_URL="${MUD_REPO_URL:-$(git -C "$SRC_REPO" remote get-url origin)}"

mkdir -p "$RELEASES"
BUILD_DIR="$(mktemp -d "$DEPLOY_ROOT/build.XXXXXX")"
trap 'rm -rf "$BUILD_DIR"' EXIT

echo "Fetching $REF from $REPO_URL into clean build directory"
git -C "$BUILD_DIR" init -q
git -C "$BUILD_DIR" fetch -q --depth 1 "$REPO_URL" "$REF"
git -C "$BUILD_DIR" checkout -q --detach FETCH_HEAD
SHA="$(git -C "$BUILD_DIR" rev-parse --short=10 HEAD)"
RELEASE_ID="$(date -u +%Y%m%d-%H%M%S)-$SHA"
RELEASE_DIR="$RELEASES/$RELEASE_ID"
[ ! -e "$RELEASE_DIR" ] || die "release $RELEASE_ID already exists"

echo "Publishing $SHA"
# MUD.PlanarRealms is not referenced by MUD.Main, so it is published too; its DLL must sit
# next to MUD.Main for the content scan (see specs/CONFIGURATION.md).
dotnet publish "$BUILD_DIR/MUD.Main/MUD.Main.csproj" -c Release -o "$RELEASE_DIR.partial"
dotnet publish "$BUILD_DIR/MUD.PlanarRealms/MUD.PlanarRealms.csproj" -c Release -o "$RELEASE_DIR.partial"
[ -x "$RELEASE_DIR.partial/MUD.Main" ] || [ -f "$RELEASE_DIR.partial/MUD.Main.dll" ] \
    || die "publish produced no MUD.Main"
[ -f "$RELEASE_DIR.partial/MUD.PlanarRealms.dll" ] || die "publish produced no MUD.PlanarRealms.dll"
echo "$REF $SHA" > "$RELEASE_DIR.partial/RELEASE"
mv "$RELEASE_DIR.partial" "$RELEASE_DIR"

PREVIOUS=""
if [ -L "$CURRENT" ]; then PREVIOUS="$(readlink -f "$CURRENT")"; fi

switch_to "$RELEASE_DIR"
echo "current -> $RELEASE_DIR"
if ! restart_service; then
    echo "deploy: service restart failed" >&2
    if [ -n "$PREVIOUS" ]; then
        echo "deploy: restoring previous release $PREVIOUS" >&2
        switch_to "$PREVIOUS"
        restart_service || echo "deploy: restart of previous release also failed; check journalctl --user -u $SERVICE" >&2
    fi
    exit 1
fi

# Prune old releases, always keeping the current and previous ones.
[ "$KEEP" -ge 2 ] || KEEP=2
CUR_REAL="$(readlink -f "$CURRENT")"
COUNT=0
while IFS= read -r d; do
    COUNT=$((COUNT + 1))
    if [ "$COUNT" -gt "$KEEP" ] && [ "$d" != "$CUR_REAL" ] && [ "$d" != "$PREVIOUS" ]; then
        rm -rf "$d"
    fi
done < <(find "$RELEASES" -mindepth 1 -maxdepth 1 -type d -name '2*' | sort -r)

echo "Deployed $RELEASE_ID (previous: ${PREVIOUS:-none})."
print_rollback
