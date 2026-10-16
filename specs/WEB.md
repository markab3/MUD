# Web site (MUD.Web)

ASP.NET Core (Razor Pages, net8.0) site for logging in with the same character
accounts the telnet game uses. It does not start the World or the telnet
server and does not reference MUD.Core; it reads the `player` collection
directly through the Mongo driver.

## Configuration

| Setting | Source | Default |
|---|---|---|
| Mongo connection string | env `MUD_MONGO_URI` (required; the process exits with a message if unset; never logged) | none |
| Database name | config `WorldSettings:DatabaseName` | `testmud` |
| Listen URL | env `MUD_WEB_URL` | `http://127.0.0.1:5080` |

Run: `MUD_MONGO_URI=... dotnet run --project MUD.Web`.

## Deployment

`scripts/deploy.sh` publishes the site into `web/` of each release and the systemd user unit
`planar-realms-web.service` runs it, reading `~/.config/mud/mongo.env` and the optional
`~/.config/mud/web.env` (`MUD_WEB_URL`). Default bind is `127.0.0.1:5080`. Install steps and
rollback behaviour are in [DEPLOYMENT.md](DEPLOYMENT.md).

## Routes

| Route | Access | Behaviour |
|---|---|---|
| `/` | anyone | redirects to `/character` if signed in, else `/login` |
| `/login` | anyone | name + password form, antiforgery protected |
| `/logout` | POST only | antiforgery protected sign-out; GET just redirects |
| `/character` | signed in | `Hello (<PlayerName>)!`, HTML-encoded |

The nav shows a `/creator` link only when the `CreatorOrAdmin` policy passes.

## Creator page (stub)

`/creator` is protected by the `CreatorOrAdmin` policy (admin implies creator). Anonymous users are
redirected to `/login`; signed-in players get a 403 page at `/access-denied` (no redirect loop).

- The form has fields named after `specs/ROOM_FILES.md`: `Key` (local room key), `Short`, `Long`,
  `Area` (area prefix) and up to 4 exit rows `Dir` + `To` (target room key). Exit rows are static
  for now; there is no add/remove-row script.
- POST validates server side (required, length limits, key patterns from ROOM_FILES.md section 3,
  half-filled exit rows rejected) with antiforgery, then calls `IRoomCreationService.Create`.
- `StubRoomCreationService` (registered in DI) returns `NotImplemented` and writes nothing. The page
  then shows "Room creation is not implemented yet." and echoes none of the submitted values.
  The real implementation replaces the DI registration; no Mongo access exists in this path.

## Auth model

- `IAccountService` (`Authenticate`, `GetByName`) lives in `MUD.Web/Services`, backed by
  `IAccountStore` (`MongoAccountStore` in production, fakes in tests), so it can be split out later.
- Lookup is an exact match on `NameKey = name.Trim().ToLowerInvariant()`, then the password is
  compared in constant time (both sides SHA-256 hashed, then `FixedTimeEquals`). Same rules as
  `PlayerAccounts.FindByName/Authenticate`.
- Role comes from the `_t` discriminators in `ExtendedProperties`: `AdminProperty` > `CreatorProperty` > Player.
- Cookie auth: `mud.auth`, HttpOnly, SameSite=Lax, Secure policy SameAsRequest (plain HTTP locally), 8h expiry.
  Claims: `Name` = PlayerName, `Role` = Player/Creator/Admin. The role is fixed at login time.
- Policy `CreatorOrAdmin` requires role Creator or Admin.
- Failed logins show one generic message. Throttling: 500 ms delay per failure and, per client IP,
  a lockout after 5 failures in 5 minutes (in memory; behind a proxy all clients share the proxy IP).

## Caveat: plaintext passwords

Player passwords are stored and compared in plaintext by the game today, and the web login
inherits that. Over plain HTTP the password also crosses the network unencrypted, so keep the
site bound to localhost until HTTPS/a reverse proxy exists. Password hashing needs a migration that
also changes the telnet game; it is out of scope here.

## Tests

`MUD.Web.Tests` (xunit) uses a fake `IAccountStore`; no Mongo needed. CI builds and tests
`MUD.sln`, which includes both projects.

## Seeding

`scripts/seed-admin.sh` creates (or repairs) an admin character in the local MongoDB so the same
login works for telnet and the web. Defaults: name `maurlorn`, password `moocow`.

    scripts/seed-admin.sh
    MUD_SEED_NAME=someone MUD_SEED_PASSWORD=secret scripts/seed-admin.sh

- Connection: `MUD_MONGO_URI` from the environment, else from `~/.config/mud/mongo.env`. It is never
  printed. Uses `mongosh` if installed, otherwise the legacy `mongo` shell.
- Other overrides: `MUD_SEED_DATABASE` (default `testmud`), `MUD_SEED_START_ROOM` (default
  `WorldSettings:StartRoomKey` from `MUD.Main/appsettings.json`; only used for a new character).
- Idempotent. If no player with that `NameKey` exists, a full `Player` document is inserted (string `_id`,
  `PlayerName`, `NameKey` = lowercase name, `Password`, `Race` human, `CurrentLocationId`, `ExtendedProperties`
  with `CreatorProperty` and `AdminProperty` and their `_t` discriminators). If it exists, only the password
  and any missing Creator/Admin property are ensured; nothing else is reset.
- It ensures the unique index `NameKey_unique` on `player` (same definition as the game's
  `CreateUniqueStringIndex`: unique, partial on string `NameKey`).
- MongoDB is schemaless: the `player` collection and that index are all the website needs. No other
  collections are required at this stage.
- The password is stored in plaintext because the game stores and compares plaintext today.
