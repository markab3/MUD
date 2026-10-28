# Planar Realms — Configuration

## 1. Runtime configuration

Configuration comes from the **environment** (secrets and per-host values)
and `MUD.Main/appsettings.json` (non-secret defaults, copied next to the
executable). Nothing secret is committed.

| Setting | Source | Default | Notes |
|---|---|---|---|
| Mongo connection string | env `MUD_MONGO_URI` | none, **required** | `MUD.Main` prints a clear error and exits 1 if unset or blank |
| Database name | `WorldSettings:DatabaseName` | `testmud` | used by `Program`, `World` and the repositories |
| Bind address | `--ip`, else env `MUD_BIND_ADDRESS`, else `TelnetSettings:IPAddress` | all interfaces | an empty or unparsable value means all interfaces |
| Port | `--port`, else env `MUD_PORT`, else `TelnetSettings:Port` | `7680` | |
| Start room | `WorldSettings:StartRoomKey` | required | see ROOM_FILES.md |
| Content directory | `WorldSettings:ContentDirectory` | `Content` | relative to the executable |
| Import on startup | `WorldSettings:ImportOnStartup` | `true` | |

Which project reads what: `MUD.Main` reads every setting above and is the only place
that does. The telnet limits (`MUD_*` variables read by `TelnetLimits.FromEnvironment`
in `MUD.Telnet`) configure the telnet server; `MUD.Main` passes the login-throttle
numbers from them to the throttle in `MUD.Core`, so telnet and any later front end
share one throttle. Bind address and port belong to the telnet front end only.

The old `--db` command-line option is gone: the connection string is never
accepted on the command line (it would show up in `ps` output).

Local run:

```bash
MUD_MONGO_URI="mongodb://localhost:27017" dotnet run --project MUD.Main -- --ip 127.0.0.1 --port 7680
```

### The service environment files

The systemd unit reads two optional/required files in the service user's
`~/.config/mud/`:

- `mongo.env` (required, mode 600, created by the maintainer's install
  script): `MUD_MONGO_URI=mongodb://user:password@localhost:27017/?authSource=admin`
- `server.env` (optional): `MUD_BIND_ADDRESS=...` and/or `MUD_PORT=...`

Neither file is in the repository. Never commit a connection string.

## 2. Build

- `build.ps1`:

  ```powershell
  dotnet publish ./MUD.sln --self-contained -r linux-x64
  ```

- CI: AppVeyor (`appveyor.yml`) — master branch only; restore +
  `build.ps1`; zips publish output. The test step is commented out
  (no tests exist).
- Target framework `net8.0` in all projects.
- **Content assemblies must land next to the executable:** `World`
  `Assembly.LoadFrom`s `MUD.Core.dll` and `MUD.PlanarRealms.dll` from
  the output directory. `MUD.PlanarRealms` is not a csproj reference of
  `MUD.Main` — publish must include its DLL or the content scan misses it.

## 3. Deployment

See [DEPLOYMENT.md](DEPLOYMENT.md): `planar-realms.service` (systemd user
unit) and `scripts/deploy.sh` (versioned releases, `current` symlink,
rollback).

- Listens on TCP 7680 by default (`MUD_PORT`). No Dockerfile. No TLS.
  Console stdout is the only log output; journald captures it.

## 4. Operational notes

- **Stop/shutdown in-game:** admin command `shutdown` (see
  `MUD.Core/Commands/Admin/`); host-console `q` also quits the process.
- **Pre-login protocol:** `connect <user> <pass>`, `create <user> <pass>`,
  `who`, `quit` — handled outside the command system in `Program.cs`.
- **Host-console debug keys** (main thread): `b` broadcast a line,
  `c` color test, `t` terminal-type subnegotiation, `q` quit.
- **World content is in MongoDB, not code.** There is no seed script in
  the repo; a fresh database has no rooms. The single code-defined room
  (`TowerOfAellioc_1`) must be inserted into Mongo by other means.

## 5. Local development quick start

A reachable MongoDB is required at boot (the client is constructed first
and the boot sequence issues a read against the configured database).
The boot check against the `players` collection is a no-op read
(`FirstOrDefault`, no failure path).

```bash
MUD_MONGO_URI="mongodb://localhost:27017" dotnet run --project MUD.Main -- --ip 127.0.0.1 --port 7680
```

Note a collection-name discrepancy: the boot check reads collection
`players` while `PlayerRepository` uses `player`. The `players` name is
vestigial (seed-code era); treat `player` as the real player collection.

## 6. Collection names (current)

| Collection | Used by |
|---|---|
| `player` | `PlayerRepository` (configured database, default `testmud`) |
| `room` | `RoomRepository` (configured database, default `testmud`) |
| `players` | boot-time no-op read only — not a real collection |
