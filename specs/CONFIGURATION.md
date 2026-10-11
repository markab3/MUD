# Planar Realms — Configuration & Deployment

## 1. Runtime configuration

Configuration is passed **entirely as CLI arguments**:

```
MUD.Main --ip <bind-ip> --port <port> --db <mongodb-connection-string>
```

- `MUD.Main/appsettings.json` exists (TelnetSettings,
  `ConnectionStrings:MongoDBConnection`) and is copied to output, but
  `Program.Main` never loads `IConfiguration` — **the file is currently
  dead config**. Do not edit it expecting an effect.
- The Mongo **database name is hardcoded to `testmud`** in
  `Program.cs` (collections `player`, `room`), independent of the
  connection string's database.
- New-player start room ID is hardcoded in `World`
  (`5f6e27f20c1fdd24b4b18b1a`).

### SECURITY WARNING

`appsettings.json` and `planar-realms.service` contain a **live Mongo
Atlas password and public IP committed to the repo**. Rotate the
credential and externalize it (env var / secret manager) before any
public hosting.

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

## 3. Deployment (current: systemd on Linux)

`planar-realms.service`:

```ini
[Unit]
Description=Planar Realms MUD server

[Service]
Type=simple
ExecStart=/usr/bin/planar-realms/MUD.Main --ip 34.123.49.245 --port 7680 --db mongodb+srv://...

[Install]
WantedBy=multi-user.target
```

- Listens on **TCP 7680**.
- Deployment steps (current practice):
  1. Build via AppVeyor (or local `build.ps1`), download zip.
  2. Extract to `/usr/bin/planar-realms/` (keep both `MUD.Core.dll` and
     `MUD.PlanarRealms.dll` present).
  3. `systemctl enable --now planar-realms`.
- No Dockerfile. No TLS. Console stdout is the only log output —
  journalctl captures it.

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
and the boot sequence issues a read against db `testmud`). The boot check
against the `players` collection is a no-op read (`FirstOrDefault`, no
failure path).

```bash
dotnet run --project MUD.Main -- --ip 127.0.0.1 --port 7680 --db "mongodb://localhost:27017"
```

Note a collection-name discrepancy: the boot check reads collection
`players` while `PlayerRepository` uses `player`. The `players` name is
vestigial (seed-code era); treat `player` as the real player collection.

## 6. Collection names (current)

| Collection | Used by |
|---|---|
| `player` | `PlayerRepository` (db `testmud`) |
| `room` | `RoomRepository` (db `testmud`) |
| `players` | boot-time no-op read only — not a real collection |
