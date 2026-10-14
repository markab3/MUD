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

## Routes

| Route | Access | Behaviour |
|---|---|---|
| `/` | anyone | redirects to `/character` if signed in, else `/login` |
| `/login` | anyone | name + password form, antiforgery protected |
| `/logout` | POST only | antiforgery protected sign-out; GET just redirects |
| `/character` | signed in | `Hello (<PlayerName>)!`, HTML-encoded |

The nav shows a `/creator` link only when the `CreatorOrAdmin` policy passes.
The creator page itself is not part of this change (the link 404s until it exists).

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
