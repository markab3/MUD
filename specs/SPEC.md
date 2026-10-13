# Planar Realms — Project Spec

**Status:** early-stage / experimental (banner reads "Planar Realms 0.2")
**Last spec pass:** 2026-09-21, from codebase analysis of commit `ce8f7b9`

## 1. Purpose

A C# telnet MUD server. Clients connect over raw TCP, create or log in to a
character, and interact with rooms, items, and (eventually) other players and
NPCs. World state is persisted in MongoDB.

## 2. Scope (current)

| Capability | State |
|---|---|
| Telnet connection, line I/O, terminal-type subneg (partial) | Working |
| Pre-login gate: `connect`, `create`, `who`, `quit` | Working |
| Rooms, exits, `look`, movement | Working |
| Items: `get`, `drop`, `inventory`, `wear`/`remove`, `wield`/`lower` | Working |
| Object matching incl. plurals/ordinals ("2 swords") | Partial (see README checklist) |
| Chat: `say`, `tell`, `who` | Working |
| Creator mode: build/clone/dest/edit/terraform/proto/apply | Working, rough |
| NPCs | Entity exists (`NPC : Living`), no AI |
| Skills, visibility/spatial system, dynamic room descriptions | Not started |

## 3. Non-goals / not-yet

- No web layer (SignalR/web interconnect is a README idea only).
- No TLS on the telnet socket.
- No test suite of any kind.
- No logging framework (console writes only).

## 4. Tech stack

- C# on **.NET 8** (`net8.0`; upgraded from the EOL `netcoreapp3.1`).
- MongoDB (driver 2.30.0, server 4.4 compatible; Atlas `mongodb+srv` in the current deployment).
- Scrutor 3.2.2 for assembly-scanned DI.
- Custom raw-socket telnet layer (`MUD.Telnet`, no packages).
- `System.CommandLine` / DragonFruit referenced but not actually used for
  argument parsing.

## 5. Known issues (must-fix list)

1. ~~.NET Core 3.1 is EOL~~ — resolved: all projects target net8.0.
2. **Secrets committed to the repo** — Mongo Atlas password in
   `MUD.Main/appsettings.json` and `planar-realms.service`; public IP in config.
3. **Plaintext passwords** stored in Mongo and compared in `World.DoLogin`.
4. **Thread-safety gaps** — `Repository<T>` cache, `Server._clients`,
   `World.Players`, `Room._occupants` are plain `List`/`Dictionary` touched
   from the command loop, heartbeat timer, and telnet callbacks.
5. **Busy loop** — `World.Start` has a `while(_isRunning)` loop with no sleep.
6. **`appsettings.json` is dead config** — `Program.Main` takes CLI args and
   never loads `IConfiguration`.
7. **Suspect code** — `Living.DoHeartbeat` checks
   `GetType().IsSubclassOf(typeof(IHeartbeat))` (interface test that fails
   for most implementers); `Client.Send(string)` discards its
   `Replace` result (line endings not normalized); Room exit line contains
   literal `"/r/n"` instead of `\r\n`; `World.LoadTypes` joins paths with `\`
   on Linux.
8. **Command gaps** — `AdminCommands` source is built but never consulted in
   `Living.ResolveCommand`; the `Learned` category is only reachable through
   `Living.KnownCommands`, which no visible command ever populates.
9. **Content lives only in MongoDB** — one code-defined demo room
   (`TowerOfAellioc_1`); no seed script in the repo.
10. **No CI test step** — AppVeyor builds only (test script commented out).

## 6. Open design questions (from README + code)

- Runtime reload of creator-written code through DI (partially scaffolded).
- Plural/ordinal object-matching polish.
- Doors/locks on `Exit` (commented in `Exit.cs`).
- Multi-word name resolution for `tell`.
- Skills redesign.

## 7. Repository workflow rules (standing)

- **All changes land on branches other than `master`.** Never commit to or
  push to `master`.
- `hermes` is the integration branch for agent work; feature branches are cut from it, have no slashes in their names, and commit messages carry no attribution. See [DESIGN_PRINCIPLES.md](DESIGN_PRINCIPLES.md) §6.
- Spec drafts: `specs/initial-draft` branch (this document set).
- Future work items get their own feature branches off the latest state;
  integration to `master` (if it ever happens) is a deliberate, separate
  act owned by the repository maintainer.
- Full policy, scope, and enforcement requirements: [BRANCHING_POLICY.md](BRANCHING_POLICY.md).
