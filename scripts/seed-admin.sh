#!/usr/bin/env bash
# Seed (or repair) an admin character in the local MongoDB so the same login works for telnet and the web.
#
# Idempotent: creates the character if absent; if it exists, only the password and the Creator/Admin
# properties are ensured and everything else is left untouched. Also ensures the unique NameKey index
# (same name and partial filter as MUD.Data Repository.CreateUniqueStringIndex).
#
# Environment:
#   MUD_MONGO_URI        connection string (required; else read from ~/.config/mud/mongo.env). Never printed.
#   MUD_SEED_NAME        character name        (default: maurlorn; 3-16 letters)
#   MUD_SEED_PASSWORD    password              (default: moocow; stored in PLAINTEXT, like the game does today)
#   MUD_SEED_DATABASE    database name         (default: testmud)
#   MUD_SEED_START_ROOM  start room key for a NEW character (default: WorldSettings:StartRoomKey from
#                        MUD.Main/appsettings.json)
set -euo pipefail

die() { echo "seed-admin: $*" >&2; exit 1; }

if [ -z "${MUD_MONGO_URI:-}" ]; then
    envfile="$HOME/.config/mud/mongo.env"
    [ -r "$envfile" ] || die "MUD_MONGO_URI is not set and $envfile is not readable"
    # shellcheck disable=SC1090
    . "$envfile"
    [ -n "${MUD_MONGO_URI:-}" ] || die "MUD_MONGO_URI not found in $envfile"
fi

if command -v mongosh >/dev/null 2>&1; then SHELL_BIN=mongosh
elif command -v mongo >/dev/null 2>&1; then SHELL_BIN=mongo
else die "neither mongosh nor mongo is installed"; fi

NAME="${MUD_SEED_NAME:-maurlorn}"
PASSWORD="${MUD_SEED_PASSWORD:-moocow}"
DBNAME="${MUD_SEED_DATABASE:-testmud}"
START_ROOM="${MUD_SEED_START_ROOM:-}"
if [ -z "$START_ROOM" ]; then
    cfg="$(dirname "$0")/../MUD.Main/appsettings.json"
    if [ -r "$cfg" ]; then
        START_ROOM="$(sed -n 's/.*"StartRoomKey"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$cfg" | head -n1)"
    fi
fi
[ -n "$START_ROOM" ] || die "no start room: set MUD_SEED_START_ROOM"

# Same rules as PlayerAccounts.ValidateName / Normalize.
[[ "$NAME" =~ ^[A-Za-z]{3,16}$ ]] || die "name must be 3-16 letters A-Z"
NAMEKEY="$(printf '%s' "$NAME" | tr 'A-Z' 'a-z')"

# Values reach the JS through the environment, never interpolated into JS source.
export SEED_NAME="$NAME" SEED_NAMEKEY="$NAMEKEY" SEED_PASSWORD="$PASSWORD" SEED_START_ROOM="$START_ROOM" SEED_DB="$DBNAME"

JS='
var env = function (k) { return (typeof process !== "undefined" && process.env) ? process.env[k] : _getEnv(k); };
var name = env("SEED_NAME"), key = env("SEED_NAMEKEY"), pw = env("SEED_PASSWORD"), room = env("SEED_START_ROOM");
var c = db.getSiblingDB(env("SEED_DB")).getCollection("player");
function oid() { var o = new ObjectId(); return typeof o.toHexString === "function" ? o.toHexString() : o.str; }
function prop(type) {
    var d = { _t: ["Entity", "ExtendedProperty", type], _id: oid(), objectContext: null };
    if (type === "CreatorProperty") { d.CurrentDirectory = null; }
    return d;
}
function has(doc, type) {
    return (doc.ExtendedProperties || []).some(function (p) {
        var t = p && p._t; return Array.isArray(t) ? t.indexOf(type) >= 0 : t === type;
    });
}
c.createIndex({ NameKey: 1 }, { name: "NameKey_unique", unique: true, partialFilterExpression: { NameKey: { $type: "string" } } });
var doc = c.findOne({ NameKey: key });
if (!doc) {
    c.insertOne({
        _t: ["Entity", "GameObject", "Living", "Player"], _id: oid(),
        ShortDescription: name, LongDescription: null, Plurals: null, Adjectives: [], Handles: [],
        ExtendedProperties: [prop("CreatorProperty"), prop("AdminProperty")],
        Gender: null, Race: "human", CurrentLocationId: room, Class: null, KnownCommands: null,
        Inventory: [], WornItems: [], HeldItems: [],
        PlayerName: name, NameKey: key, Password: pw, SelectedTerm: "Default"
    });
    print("created " + key);
} else {
    var add = [];
    if (!has(doc, "CreatorProperty")) { add.push(prop("CreatorProperty")); }
    if (!has(doc, "AdminProperty")) { add.push(prop("AdminProperty")); }
    var upd = { $set: { Password: pw } };
    if (add.length) { upd.$push = { ExtendedProperties: { $each: add } }; }
    c.updateOne({ _id: doc._id }, upd);
    print("updated " + key + " (added " + add.length + " properties; password ensured)");
}
'

# The URI is passed to the shell binary only; any shell output is scrubbed of it before display.
rc=0
out="$("$SHELL_BIN" "$MUD_MONGO_URI" --quiet --eval "$JS" 2>&1)" || rc=$?
printf '%s\n' "${out//"$MUD_MONGO_URI"/<uri>}"
exit "$rc"
