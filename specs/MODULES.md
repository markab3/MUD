# Modules: manifest, launch entries, lifecycle, deployment, worked examples, failure modes, testing

Part of [ARCHITECTURE_V2.md](ARCHITECTURE_V2.md). Message and event names refer to [CORE_API.md](CORE_API.md). D-numbers are the owner's
decisions ([OPEN_QUESTIONS_V2.md](OPEN_QUESTIONS_V2.md)); G-numbers are the "Gaps found while revising" in CORE_API.md section 9.
Status: REVISED to D1..D39. Docs only.

Contents: 1 Manifest, 2 Launch entries and grants, 3 Start-up, health and supervision, 4 Development workflow, 5 Load, unload, upgrade,
6 Packaging and systemd, 7 Example A (desert), 8 Example B (temple), 9 Child processes (decided), 10 Failure modes, 11 Testing.

Things this revision removed from the draft because a decision reverses them: manifest subscriptions and filters (D5/D6), overlay
leases and `on_disconnect` (D11), `start.mode` / `areas:` / area-header module binding (confirmed assumptions, D39), the single
bidirectional `Connect` stream (D16), and any `core-admin` CLI (D32).

## 1. Manifest

One `module.yaml` per module build. It **ships with the module** and is sent to core in the first call (`Hello`, D18); it is not compiled
into core and carries no authority (grants come from core's own file, section 2). The `required` flag of a module is **not** here:
it lives in the launch entry (core config). Sketch:

```yaml
name: temple                      # unique; also the component namespace ("temple.*")
version: 0.1.0                    # semver of this build (informational)
contract_version: 1               # the number sent in Hello; core demands strict equality (D24)
behaviors:                        # the (Type, hooks, readable SchemaVersions) this module can handle (D5, D10)
  - type: DesertTempleTrap
    hooks: [living.move]            # hook ids are G25
    schema_versions: [1]
  - type: TempleLeverRoom
    hooks: [command.entered]
    schema_versions: [1]
components:                       # namespaces and optional schemas for module-owned component data (G13)
  - name: temple.trap
    schema: schemas/trap.schema.json      # optional; whether core validates writes is not decided (G13)
  - name: temple.lever
capabilities:                     # REQUESTED; core grants a subset from module-grants.yaml (D18)
  - { name: "emit:component.set:ns=temple" }
  - { name: "emit:entity.move:kind=player",  required: true }     # core refuses to start the module if this is not granted
  - { name: "emit:message.send:scope=room" }
  - { name: "timer.schedule" }
  - { name: "state" }
```

No `exec`, no `restart`, no `health`, no `start` block: those are launch-entry settings (section 2). No `subscriptions`: routing is by the
behavior lists on entities (CORE_API 2.2). The capability vocabulary is G20. Fields that make a module risky (many requested
capabilities, ones flagged `required`) are shown by `modules diff` (optional helper, D18).

## 2. Launch entries and grants

Two files, both owned by the owner, neither writable by modules:

| File | Contents | Read |
|---|---|---|
| Launch entries (core config; path and format **not decided**, G26) | per module: executable, arguments, working directory, restart policy, `required` flag | re-read live (command `modules reload`) |
| `~/.config/mud/module-grants.yaml` (mode 600, read by core only; D18) | per module: granted capabilities | reloadable by an admin command without restarting core |

Illustrative launch entry (shape only; the numbers are **not set**):

```yaml
modules:
  - name: desert
    exec: dotnet
    args: ["Desert.dll"]
    required: false             # non-required modules start in the background (confirmed assumption)
    restart: { policy: on_failure }   # backoff list, crash-loop threshold: not set (open item)
  - name: temple
    exec: dotnet
    args: ["Temple.dll"]
    required: false
```

Every module in the launch entries always runs (confirmed assumption; there is no area trigger and no `manual`/`eager` mode). A hand-started
development module is a module with no launch entry that presents a dev token (section 4).

**Partial grant (D18).** The module starts with the granted subset and a logged warning. A manifest capability flagged `required: true`
that is not granted makes core refuse the start (logged, state `REFUSED`).

**Update flow (D18):** build the module, `modules reload <name>`; core diffs requested against granted and logs the difference; the owner edits the
grants file; `modules reload` again (or the grants reload command). Optional helpers: `modules diff`, `modules grant`. What exactly `reload`
does to a running process (restart, or only re-read config) is G28.

## 3. Start-up, health and supervision

Start order of core: lock + sockets up -> persistence worker, spool replay -> world load (as today) -> supervisor starts every module
in the launch entries (token in the child's environment, D17/D20) -> **front ends are allowed to open sessions once every `required: true`
module has reported `Ready`** (D21). Non-required modules start in the background; core does not wait for them. If a required module
is not Ready within the **start-up timeout (not set)**, core exits with a clear error and stops the modules it already started (D21); the systemd
user unit needs a restart limit so a permanently failing required module does not loop forever (D21; values not set).

Supervisor: a small hand-written loop inside core, one state machine per module:

```
  STOPPED --start--> STARTING --Hello ok--> CONNECTED --Ready--> READY --stop--> STOPPING --exit--> STOPPED
     ^                  | no Hello within the start-up timeout          |
     |                  v                                               | process exit, stream lost, Ping misses
     |             BACKOFF <--------------------------------------------+
     |                  | wait backoff[n]; crash-loop threshold exceeded -> FAILED          (numbers: not set)
     +-- admin `modules start` clears FAILED / QUARANTINED;   REFUSED = a required capability was not granted (section 2)
```

- Liveness: the event stream is open AND `Ping` answers. `Ping` is a separate unary call (D16) so a handler stuck in a long computation is distinguishable
  from a dead process; the miss threshold is **not set**.
- Process exit is detected via `Process.Exited`; exit code logged; stdout/stderr captured and re-logged with a `[module:name]` prefix.
- Restart with backoff and crash-loop handling (D20): the numbers and the crash-loop policy are **open items**; the state machine above shows where they plug in.
- Modules exit after a grace period if the connection to core is lost (D20), so a hung or dead core does not leave orphans.
- Core checks for stale module processes at start (D20); the mechanism is G27.
- Connection watch also feeds dormancy: while a module is not READY, its behaviors on entities are dormant (D10, section 5.2).

## 4. Development workflow (run a module by hand against a core)

1. Run a scratch core with its own run directory and scratch database (the run-directory override variable is a detail for the phase 4 cards).
2. Obtain a dev token for the module name (D17 requires "a dev tool"). The form is G28: recommended an admin game command `modules token <name>` that prints a
   one-time token, accepted only while the supervisor is not already running that module.
3. Start the module from the IDE or `dotnet run` with `MUD_MODULE_NAME`, `MUD_MODULE_TOKEN`, `MUD_CORE_SOCKET` set (the same three variables a spawned child receives).
4. Edit, rebuild, rerun: core sees the stream end; behaviors go dormant (flavor fail open); the new process says `Hello` and reconciles.
5. Tail core's log filtered by `trace_id` / `cause_id` to see the whole conversation of one command.
6. No core available: use the test harness (section 11).

Mongo is not available to agents in the sandbox, so none of this was run; it is the intended workflow.

## 5. Load, unload, upgrade

### 5.1 Load

Core reads the launch entry, spawns the child with a cleaned environment (`PATH`, `HOME`, `XDG_RUNTIME_DIR`, `DOTNET_ROOT`, plus the three `MUD_MODULE_*`
variables; **no** `MUD_MONGO_URI`), expects `Hello` (name, token, manifest, contract version) -> core checks token, strict contract equality (D24), the capability
grant (D18) -> `HelloReply` -> the module opens `Subscribe`, reads its state document with `GetState`, reconciles (idempotent, D13), schedules its timers, calls
`Ready`. Core logs `module.ready`.

### 5.2 Unload, crash, and dormant behaviors

Behavior entries and component data **stay on entities** whenever a module stops, for any reason (D10). Core's cleanup is small by design (D11: no overlays, no leases):

| Thing | Intentional stop (`modules stop`) | Crash / unexpected loss | Uninstall |
|---|---|---|---|
| Behavior entries on entities | stay; **dormant** | stay; dormant | stay; dormant until removed by a builder |
| Component data, module-state document | kept | kept | kept (purge of orphaned module data is a deferred item) |
| Timers | cancelled by core (scoped to the connection) | cancelled; the module recreates them at start (G16) | cancelled |
| Pending Before deliveries to it | resolved by the hook's fail policy | resolved by the fail policy | n/a |
| Effects the module applied (a storm component) | **stay** unless the module cleaned up in its grace period; core does not know they were temporary | stay until the module returns or someone removes them | stay |
| Players in its area | unaffected | unaffected | unaffected |

Dormancy detail (D10): an unresolved `(Module, Type)` is skipped under the hook's fail policy (flavor hooks fail open, rule hooks fail closed; per-behavior
override bounded by the core policy file); the skip is counted and logged once per interval; when the module returns it resumes from the stored `Config`; a module that cannot
read an entry's `SchemaVersion` refuses it and it counts as a miss.

### 5.3 Upgrade

Build, `modules reload <name>` (G28): the new process connects with a new manifest and the same contract number. Behavior entries written by an older build carry
their old `SchemaVersion`; the new build must read it or refuse it (a miss, dormant). Downgrade is the same operation in reverse. There is no hot reload of code; "hot swap"
in D5 means the `(Module, Type)` dictionary entry changes, not that a running process is patched.

### 5.4 Data lifetimes in one sentence

Module data belongs to the world, not to the process: stopping a module never deletes world data; the module's live effects simply stop being maintained.

## 6. Packaging and systemd on this host

Facts: systemd 249 on this host; user lingering and user units are what the existing services use (`planar-realms.service`, `planar-realms-web.service`);
`/run/user/1000` exists (mode 0700); the installed .NET runtime is 8.0.28 under `/usr/share/dotnet`. The existing units do not set `DOTNET_ROOT`; the apphost presumably
finds `/usr/share/dotnet` by default **(unverified)**.

Modules are C# projects in this repo (confirmed assumption), built and published with the same runtime as core (framework-dependent). Core unit (changes from
`planar-realms.service` shown as comments; limits are not set):

```ini
[Service]
Type=simple
WorkingDirectory=%h/.local/share/mud/current
ExecStart=%h/.local/share/mud/current/MUD.Main
Environment=DOTNET_ROOT=/usr/share/dotnet           # new: forwarded by the supervisor to children
EnvironmentFile=%h/.config/mud/mongo.env            # ONLY the core unit reads this
KillMode=control-group                              # stopping core signals every module child too
TimeoutStopSec=60                                   # graceful module stop + persistence drain (ShutdownTimeout is 30 s today)
Restart=on-failure
# StartLimitIntervalSec= / StartLimitBurst=         # required by D21 (a failing required module must not loop forever); values not set
```

Front-end units (phase 7): `planar-realms-telnet.service`; the web unit loses `mongo.env` when phase 6 lands. No systemd launcher for modules in the first cut (D20).
Publishing modules: extend `scripts/deploy.sh` (it publishes `MUD.Main`, `MUD.PlanarRealms` and `MUD.Web` at lines 100-104) in the phase 4/5 cards.

## 7. Worked example A: the desert and its sandstorm

Goal: a sandstorm appears now and then, drifts room to room, scours players who are in the room, and ends by itself. It uses only the decided model:
behavior lists, string-named events, After-phase handlers, a component plus a module-state document, and a self-timed effect (D11). **No overlay, no lease, no
reads of the world** (G11 option A): everything the module knows comes from delivery snapshots, its own state document and its own bundled config.

### 7.1 Content and behavior entries

Exterior rooms (`desert.dune_1` ... `desert.dune_30`), built by a builder with the creator tools (how module-needed content gets created is an open item). Each carries two entries:

| Entry | Value |
|---|---|
| 1 | `Hook: heartbeat, Module: desert, Type: DesertStormRoom, Priority: 100, SchemaVersion: 1, Config: { "next": ["desert.dune_4", "desert.dune_2"], "step_s": 10 }` |
| 2 | `Hook: living.move, Module: desert, Type: DesertStormExposure, Priority: 100, SchemaVersion: 1, Config: { "blind_s": 30 }` |

`next` is authored adjacency: the module cannot query exits (G11 A). (The room snapshot also carries the core `Exits` component, so a variant could derive `next`
from it; the explicit list was chosen so the example does not depend on that.) Interior rooms carry neither entry. Both hooks are flavor hooks (fail open: a dead desert
module must not strand anybody; G3).

State, split by D13:

| Data | Home | Why |
|---|---|---|
| `desert.storm` on the room holding the storm: `{ storm_id: "s17", step_at: <ms>, intensity: "moderate" }` | room **component** | player-visible, and other behaviors (and builders) read it from a snapshot |
| `desert.sand_blind` on a scoured player: `{ until: <ms>, storm_id: "s17" }` | player **component** | player-visible |
| key `storms`: `{ "s17": { "room": "desert.dune_3", "ends_at": <ms> } }`, key `spawner`: `{ "next_spawn_at": <ms> }`, key `blind`: `{ "p17": <ms> }` | **module-state document** | scheduling detail nobody else reads |
| list of start rooms, 12-step max, spawn interval | the module's own bundled settings file | module-private config, not world data |

### 7.2 Sequence: connect, spawn, drift, end

Names: calls `Hello`, `Subscribe`, `GetState`, `ScheduleTimer`, `Ready`, `SubmitVerdict`, `Emit`; stream message `CoreEvent`; events `timer.fired`, `heartbeat`,
`component.set`, `component.remove`, `state.set`, `timer.schedule`, `message.send`.

```
Core (game thread)                                              desert module (own process, started by core)
------------------                                              ---------------------------------------------
supervisor spawns the child: env MUD_MODULE_NAME=desert, MUD_MODULE_TOKEN=T, MUD_CORE_SOCKET=.../modules.sock
                                  <------------------------------ Hello{module=desert, token=T, contract_version=1, manifest}
check token, contract == 1, grants
                                  ------------------------------> HelloReply{ok, connection_id=c1, granted=[...], rejected=[]}
                                  <------------------------------ Subscribe{c1}            (server stream stays open)
                                  <------------------------------ GetState{c1, key=storms}   -> {found:false}  (first run)
                                  <------------------------------ GetState{c1, key=spawner}  -> {found:false}
                                  <------------------------------ ScheduleTimer{c1, "storm-spawn", at=now+30min}   (= event timer.schedule)
                                  <------------------------------ Emit{c1, [state.set{key=spawner,doc={next_spawn_at}}]}
                                  <------------------------------ Ready{c1}                  -> required=false: nobody waits for it
 ... 30 minutes ...
timer fires on the game thread    ------------------------------> CoreEvent{name=timer.fired, payload{timer_id="storm-spawn"}}
                                  module picks start room desert.dune_3 from its settings
                                  <------------------------------ Emit{c1, cause=timer, events:[
                                       component.set{target=desert.dune_3, name=desert.storm, data={storm_id:s17, step_at:now+10s, intensity:moderate}},
                                       message.send{target=room desert.dune_3, channel=room, text="A sandstorm rolls in."},
                                       state.set{key=storms, doc={s17:{room:desert.dune_3, ends_at:now+4min}}},
                                       state.set{key=spawner, doc={next_spawn_at:now+30min}},
                                       timer.schedule{id="storm-s17-end", at=now+4min},
                                       timer.schedule{id="storm-spawn", at=now+30min} ]}
core: capability/quota checks, apply the batch atomically on the game thread; queue the room snapshot for persistence

 ... every 2 s, core delivers heartbeat to every entity that carries a heartbeat entry (G8) ...
                                  ------------------------------> CoreEvent{name=heartbeat, phase=AFTER, hook=heartbeat, event_id=e-7001,
                                       behavior{type=DesertStormRoom, config{next:[...], step_s:10}}, parties:[{role=self, desert.dune_3, components{desert.storm{...}}}]}
module: the room has desert.storm, step_at <= now  -> choose next = desert.dune_4
                                  <------------------------------ SubmitVerdict{c1, e-7001, ALLOW, emitted:[
                                       component.remove{target=desert.dune_3, name=desert.storm},
                                       component.set{target=desert.dune_4, name=desert.storm, data={storm_id:s17, step_at:now+10s, ...}},
                                       message.send{room desert.dune_3, "The storm moves on."},
                                       message.send{room desert.dune_4, "A sandstorm rolls in."},
                                       state.set{key=storms, doc={s17:{room:desert.dune_4, ends_at:...}}} ]}
core applies the batch atomically: the storm is never in two rooms or none. (For an After delivery the ALLOW is ignored; SubmitVerdict is used so the batch stays atomic.)
 ... end ...
timer fires                       ------------------------------> CoreEvent{timer.fired, "storm-s17-end"}
                                  module: GetState result cached at start says s17 is in desert.dune_4
                                  <------------------------------ Emit{[ component.remove{desert.dune_4, desert.storm},
                                       message.send{room desert.dune_4, "The sandstorm dies away."}, state.set{storms, {}} ]}
```

Idempotence (D13): if `storm-s17-end` fires and `storms` has no `s17`, the handler does nothing. If a `heartbeat` arrives for a room carrying `desert.storm` whose `storm_id` is not in
the module's state document (an orphan after a crash, a lost state document, a hand-edit), the handler answers `SubmitVerdict{ALLOW, emitted:[component.remove{that room, desert.storm}]}`. That is the whole
reconciliation: no read is needed because heartbeats deliver the snapshots.

Cost to note: with 30 rooms carrying a `heartbeat` entry the module receives about 15 deliveries per second all day, almost all of which do nothing. Variant: the module emits `behavior.add` /
`behavior.remove` for `DesertStormRoom` on the room the storm is in, so only one room has the entry. That adds writes to the entity documents and widens the temporary-state problem (a stale entry is harmless but visible
to builders). Which is better depends on the unset per-tick budget (D9) and G8's index; not decided here.

### 7.3 Players entering a stormy room

```
player p17 types "east"; core creates Before entity.move(parties: p17 mover, dune_3 from, dune_4 to). dune_4's entry 2 is for hook living.move -> a delivery.
phase BEFORE: CoreEvent{name=entity.move, phase=BEFORE, hook=living.move, behavior=DesertStormExposure, deadline_ms, parties[...]}
   module (entry type DesertStormExposure) is an After-only behavior: it answers SubmitVerdict{ALLOW, emitted:[]}  (nothing to say before)
no Deny -> re-validate -> commit. After phase: notifications to p17, dune_3, dune_4:
phase AFTER, to dune_4's entry: CoreEvent{name=entity.move, phase=AFTER, event_id=e-7102, parties:[{role=mover,p17}, {role=from,dune_3}, {role=to,dune_4 components{desert.storm{...}}}]}
   module: role of this entry's entity = to, desert.storm present -> scour
   SubmitVerdict{e-7102, ALLOW, emitted:[
        message.send{target=p17, channel=combat, text="Stinging sand scours your face."},
        component.set{target=p17, name=desert.sand_blind, data={until:now+30s, storm_id:s17}},
        state.set{key=blind, doc={p17: now+30s}},
        timer.schedule{id="blind-p17", at=now+30s, payload{player:p17}} ]}
 ... 30 s later ...
   CoreEvent{timer.fired, "blind-p17", payload{player:p17}}: state `blind` says p17 until <= now ->
   Emit{[ component.remove{p17, desert.sand_blind}, state.set{blind, {}} ]}      (idempotent: a second scour just moves `until` forward and reschedules)
```

The module does not need a distinct Before handler; a behavior entry's hook is `living.move`, and a handler that has nothing to do before the move answers ALLOW. (Whether an entry may declare the phase it cares about, to save the
Before round trip, is a refinement for the phase 3 card; it is not decided. With the all-handlers-wait rule of D15 an unnecessary Before delivery costs latency on every move into a desert room.)

**Variant: a storm that blocks leaving.** A `DesertStormRoom`-style entry on hook `living.move` where the room's role is `from`, with `desert.storm.intensity == "severe"`, answers
`SubmitVerdict{DENY, message="The wind drives you back."}` with no emitted events. That is a rule-flavored use: a dead module fails open (G3), so a player is never trapped by an unloaded module.

### 7.4 Crash, unload and core restart mid-storm

**Module crash.** The stream ends and `Process.Exited` fires. The supervisor goes `READY -> BACKOFF`. Rooms keep the `desert.storm` component and players keep `desert.sand_blind`; nobody removes them (D11).
The `DesertStormRoom` and `DesertStormExposure` entries are dormant: heartbeat and move deliveries to them are skipped by the flavor fail-open policy, counted and logged once per interval. Visible effect: no more
messages; nobody is blocked. Restart (backoff, numbers not set): `Hello` -> `Subscribe` -> `GetState storms/spawner/blind` -> recreate timers -> `Ready`; for each storm in the state document the module either re-schedules `storm-sN-end` or,
if `ends_at` is past, emits `component.remove` for it; for each `blind` entry past due it emits `component.remove` on the player. Heartbeats then resume stepping or reconcile orphans (7.2).

**Module never returns.** Components stay until a builder removes them (creator command or web editor, whose editing surface for module components is an open item). This is the price of D11.

**Unload (`modules stop desert`).** Core sends `world.shutdown`-style stop (name `module.stopping`, deadline); the module may clean up in its grace period (emit `component.remove` for its storms) or may not. The behavior entries become dormant.

**Core restart.** Components and the state document were persisted (write-through, as for any entity change); core timers were not (G16). On start the module recreates them from its state document exactly as after a crash. Players were saved and disconnected
with "world is restarting" (D29).

### 7.5 Rate

One storm step = one `SubmitVerdict` per 10 s; entering a stormy room = one per entry; a heartbeat with nothing to do = one `SubmitVerdict` with an empty batch. Limits for the per-module emission quota are not set (D9); the spike and phase 5 supply real numbers.

## 8. Worked example B: the temple, its lever and traps

Goal: walking into the trapped corridor drops you into the pit and the trap is spent; a lever in the antechamber disarms all traps in a group and they rearm after a while. This example exercises Before-phase Deny, redirect as Deny plus a new
`entity.move`, a command-entered behavior, Allow with emitted events, and state split between a component and a state document.

### 8.1 Content, behavior entries and state

| Where | Entry or component | Notes |
|---|---|---|
| `temple.corridor_2` (room) | behavior `{Hook: living.move, Module: temple, Type: DesertTempleTrap, Priority: 50, SchemaVersion: 1, Config: {effect:"pit", pit_room:"temple.pit", group:"main", actor_msg:"The floor gives way!", room_msg:"vanishes through the floor!", rearm_after_s:600}}` | authored, per D5. `Priority` is stored but has no ordering effect (D7) |
| `temple.corridor_2` | component `temple.trap {armed:true, group:"main"}` | runtime flag: affects behavior and other behaviors (the lever) change it -> component (D13) |
| `temple.antechamber` (room) | behavior `{Hook: command.entered, Module: temple, Type: TempleLeverRoom, SchemaVersion: 1, Config: {verb:"pull", noun:"lever", group:"main", traps:["temple.corridor_2","temple.corridor_3"], rearm_after_s:600}}` | **room-level lever (D14)**: features are inert, so the lever is not a feature. The room's long description mentions an iron lever. Alternative: an immovable item `iron lever` carrying the entry; that needs `command.entered` parties to include items matched by the line (G7 option B) |
| `temple.antechamber` | component `temple.lever {pulled:false}` | player-visible flag |
| module-state document key `rearm` | `{ "main": {rearm_at:<ms>} }` | scheduling detail |
| module memory only | set of trap rooms already fired and not yet rearmed | serialises the two-players race (G6 b); lost on crash, then the component is the truth |

The two hooks are `living.move` (rule-like: a trap that does not fire when the module is down is flavor, so fail open is the default, G3) and `command.entered` (flavor: `pull` simply does nothing when the module is down).

### 8.2 Sequence: walking into the trapped room (Before, Deny + redirect)

```
Player p17        Telnet front end            Core (game thread)                                            temple module
------------      -----------------           ------------------                                            --------------
"north" ---TCP--> line(session=17,"north")
                                              enqueue; game thread: Player P resolves exit north -> MoveCommand
                                              Before entity.move E1 {parties: p17 role=mover, temple.hall role=from, temple.corridor_2 role=to}
                                              entries matching hook living.move: temple.corridor_2 -> DesertTempleTrap  (1 delivery, no other entries)
                                              p17.Parked = true; chain deadline = now + chain budget (not set)
                                              ------------------------------------------------------------> CoreEvent{seq=88, event_id=e-5521, name=entity.move,
                                                     phase=BEFORE, hook=living.move, deadline_ms, trace_id=T9, cause_id="",
                                                     behavior{type=DesertTempleTrap, config{...}, schema_version=1},
                                                     parties:[{mover p17}, {from temple.hall}, {to temple.corridor_2 components{temple.trap{armed:true,group:main}}}],
                                                     payload{from:temple.hall,to:temple.corridor_2,via_exit:north,reason:walk}}
                                                                                                         module: this entry's entity has role "to"; armed:true;
                                                                                                         in-memory fired-set does not contain corridor_2 -> mark fired
                                              <------------------------------------------------------------ SubmitVerdict{c1, e-5521, DENY,
                                                     message="The floor gives way!", emitted:[
                                                       entity.move{entity:p17, from:temple.hall, to:temple.pit, reason:"trap"},
                                                       component.set{target:temple.corridor_2, name:temple.trap, data:{armed:false, fired_by:p17}},
                                                       message.send{target:room temple.hall, channel:room, text:"<mover name taken from the snapshot> vanishes through the floor!"},
                                                       state.set{key:rearm, doc:{main:{rearm_at:now+600s}}},
                                                       timer.schedule{id:"rearm-corridor_2", at:now+600s} ]}
                                              all handlers replied (one) -> chain done; Deny -> original move E1 aborted, nothing moved;
                                              the first Deny message to arrive ("The floor gives way!") goes to p17:
          <---- output(session=17, channel=system, "The floor gives way!")
                                              emitted events apply although E1 was denied (D8), in emission order, as one atomic batch, cause_id = e-5521:
                                              1. entity.move{p17 -> temple.pit, reason=trap}: a NEW Before event with its own chain.
                                                 Parties: p17, temple.hall (from), temple.pit (to). No entries match living.move there -> fast path -> re-validate -> commit
                                                 (core-originated, so the actor stays parked until the chain of emitted events is drained; G6 a)
                                              2. component.set temple.trap armed:false   3. message.send to the hall   4. state.set rearm   5. timer.schedule
                                              p17.Parked = false; queued commands drain
                                              After phase of the redirect: entity.move AFTER to p17, temple.hall, temple.pit  (no entries on them -> nothing sent)
          <---- output(session=17, channel=room, "<pit description>")
```

Names: the original event is `entity.move` (Before, reason `walk`); the redirect is a second `entity.move` (reason `trap`, `cause_id` = the delivery e-5521). There is no `entity.moved` and no MODIFY; the player never "arrives" at the corridor, so there is no After phase for the first move.

**Two players, one trap (G6 b).** p17 and p18 both enter `temple.corridor_2` before either commit. Both deliveries carry `armed:true`. The module, a single process, handles them one at a time with a lock: the first marks
`corridor_2` fired in its in-memory set and returns the Deny + redirect above; the second finds `corridor_2` in the set and returns `SubmitVerdict{ALLOW, emitted:[]}`, so p18 walks in safely. No transaction in core is involved (D7/D8). After a module crash the in-memory set is gone and
the persisted component (`armed:false` once the first batch applied) is the truth.

**Variant: Allow with emitted events (a dart trap in `temple.corridor_3`).** Entry `TempleDartTrap` on `living.move`:
Before: `SubmitVerdict{ALLOW, emitted:[message.send{p17,"You hear a click underfoot."}]}`. That message plays even if another handler on the same move denies (D8). Effects that must depend on the move committing go to the After phase:
After (role `to`): `SubmitVerdict{ALLOW, emitted:[component.set{p17, temple.poisoned, {until:now+30s}}, state.set{...}, timer.schedule{id:"poison-p17", at:now+30s}]}`, and `timer.fired poison-p17` removes the component idempotently (D11 pattern).

**Why Deny plus a new move rather than "modify the destination" (D6).** A redirect changes where the mover ends up, so the new destination's own entries must get their say. Making the redirect a normal event means the pit room could itself veto, redirect or react, with no special rule in core.

### 8.3 The lever (a `command.entered` behavior on the room, D14)

```
"pull lever" -> line(session=17,"pull lever")
Core: P has no prompt handler; Before command.entered C1 {parties: p17 (actor), temple.antechamber (room)}   (G7 option A)
      entries matching hook command.entered: TempleLeverRoom on the antechamber.  p17 parked.
CoreEvent{name=command.entered, phase=BEFORE, event_id=e-5600, hook=command.entered, behavior{type=TempleLeverRoom, config{verb:pull,noun:lever,group:main,traps:[...]}},
          parties:[{actor p17},{room temple.antechamber components{temple.lever{pulled:false}}}], payload{raw:"pull lever", verb:"pull", args:"lever"}}
module: verb/noun do not match Config -> SubmitVerdict{ALLOW}   (core then resolves the line as usual; for other lines the module is just a no-op handler)
        match, temple.lever.pulled == true  -> SubmitVerdict{DENY, message="The lever will not budge."}
        match, not pulled -> SubmitVerdict{DENY, message="You haul the lever. Somewhere deep in the stone, gears grind.", emitted:[
              component.set{target:temple.antechamber, name:temple.lever, data:{pulled:true}},
              component.set{target:temple.corridor_2, name:temple.trap, data:{armed:false, group:main}},     // one per room in Config.traps
              component.set{target:temple.corridor_3, name:temple.trap, data:{armed:false, group:main}},
              message.send{target:room temple.antechamber, channel:room, text:"<actor name from the snapshot> pulls a lever. Gears grind in the walls."},
              state.set{key:rearm, doc:{main:{rearm_at:now+600s}}}, timer.schedule{id:"rearm-main", at:now+600s} ]}
core: all handlers replied; Deny = "consumed, show this" (G7 (i): Deny is overloaded to mean handled; flagged for the owner); the original command is not resolved;
      the Deny message is shown, the emitted batch applies atomically (D8), p17 un-parked.
 ... 600 s ...
CoreEvent{timer.fired, "rearm-main"} -> Emit{[ component.set{each trap room, temple.trap, {armed:true}}, component.set{antechamber, temple.lever, {pulled:false}}, state.set{rearm,{}} ]}
```

The module needed no read: the trap rooms are named in the lever entry's `Config.traps`. The lever's entry is authored by a builder (open item: the editing surface for behavior entries).

If the lever were an immovable item (`iron lever`, an `item` with the same entry), the line `pull lever` must reach the item as a party; G7 option B (parties include items matched by `MatchObjects`) is then required. The example uses the room-level form so it works with option A.

### 8.4 When the temple module is down

| Situation | Behavior | Why |
|---|---|---|
| Move into `corridor_2`, module not READY | the `DesertTempleTrap` entry is dormant; hook `living.move` is flavor -> fail open (D10): the move proceeds, no trap; one log line per interval | a dead module must not make half the dungeon impassable |
| Owner wants "no trap = no entry" | per-behavior override in the core policy file marks `(temple, DesertTempleTrap)` fail closed: moves into `corridor_2` are denied with a core message | the override is bounded by the policy file (D10) |
| `pull lever` | entry dormant; hook `command.entered` flavor -> fail open: nothing happens, core falls through to "Command not recognized." | acceptable for a flavor feature |
| Module returns | resumes with stored Config; on start it reads `rearm`, and for any past `rearm_at` emits the rearm batch at once; otherwise reschedules `rearm-main` (timers do not survive core restarts, G16); traps keep their persisted `armed` | state is persisted component data plus the state document, not memory |
| Entry written with `SchemaVersion: 2` read by a build that knows only 1 | the module refuses the entry (a miss, treated as dormant) and logs it | D10 |

### 8.5 Latency note

Only a move into a room that carries the entry pays IPC; every other command takes the fast path (no matching entries). Because the chain waits for all handlers (D15), a move into `corridor_2` costs the one handler's round trip. Numbers are
unmeasured (the spike, V2_PHASES.md); the draft's 50 ms / 150 ms figures are proposals only.

## 9. Child processes (decided, D20)

Core spawns and supervises modules as child processes (token in env, connection watch, restart with backoff, crash-loop handling). There is no systemd launcher in the first cut. The draft's comparison with per-module systemd units
(`planar-realms-module@desert.service`) was an input to that decision and is not repeated; per-module systemd units remain a possible later hardening step, and neither the manifest nor the protocol would change.

## 10. Failure modes

| Failure | Detection | Behavior | Recovery |
|---|---|---|---|
| **Core crash** (exception beyond the loop's catch, OOM, kill -9) | systemd sees exit; front ends and modules see the connection end | Sessions drop (as a restart does today). Queued writes can be lost unless spooled (RUNTIME_STATE.md); modules are signalled by the cgroup stop and exit after the grace period | Core restarts, replays the spool, loads the world, starts modules; modules recreate timers from state; players log in again (D29) |
| **Module crash** | `Process.Exited` and stream end | Behaviors dormant (section 5.2); pending Before deliveries resolved by the hook's fail policy; components stay | Supervisor backoff restart (numbers not set); reconcile; FAILED after the crash-loop threshold (not set) |
| **Module hang** (alive, not answering) | `Ping` misses; Before deadline misses | Missing verdicts resolved by fail policy | Forced stop (SIGTERM, then SIGKILL) and restart |
| **Module slow** | late `SubmitVerdict` (event expired) | Late verdict matched by `event_id` and dropped (D16); counted | Owner fixes the module |
| **Required module not Ready** | start-up timeout (not set) | Core exits with a clear error and stops started modules (D21) | Owner fixes; the systemd restart limit prevents a loop |
| **Partial grant** | grants diff at `Hello` | Module starts with the granted subset and a warning; a `required: true` capability not granted -> REFUSED, not started (D18) | Owner edits the grants file, `modules reload` |
| **Contract mismatch** | `Hello.contract_version != core's` | Refused with a log naming both versions (D24, strict equality) | Rebuild the module against the current `MUD.Contracts` |
| **Dormant behavior** (module absent, unreadable SchemaVersion) | unresolved `(Module, Type)` at call time | Skipped under the hook's fail policy; counted; logged once per interval (D10) | Start the module or remove the entry |
| **Emission flood / cascade** | quota, cascade depth, per-tick budget, re-entrancy guard (numbers not set; D9) | The event is rejected and logged with module, event, targets, trace id, cause id; sustained -> quarantine | Admin clears after the fix |
| **State document too big / written too fast** | size and rate limit (not set; D12) | write rejected | Module fixes |
| **Stale socket file** | bind fails or file exists without an owner | Core holds the run-dir `flock`; if acquired the file is stale and is deleted and rebound | Automatic; if the lock is held, another core is running -> exit with a clear message |
| **Stale module process** at core start | core's start-up check (D20, mechanism G27) | The stale process is stopped | Automatic |
| **Duplicate module instance** | second `Hello` for a connected name | Refused unless it carries the token of a newer spawn issued by core | Supervisor kills the stray |
| **Front end crash** | connection loss; (web) HTTP errors | Sessions on it are logged out (`Player.Quit` path) until link-dead grace is decided (open item) | systemd restarts the front end; players log in again |
| **Core restart with front end up** | front end's connection breaks | Front end closes connections with "world is restarting" and clients reconnect (D29) | Players log in |
| **Mongo down** | persistence worker retries (existing) | Game continues from memory; snapshots queue/spool as today; module changes behave like player changes | Existing spool/replay |
| **Web edit vs module change** | none needed | Both are events on the one game thread; last write wins (D30); the web edit is audited per field, the module change only logged (D34) | n/a |
| **Parked actor disconnects** | session closed while the chain is pending | Chain cancelled; verdicts for its events discarded | none |
| **Game thread slow (tick > 100 ms)** | existing warning in `GameLoop.Tick` | Chain deadlines are checked on ticks; module work is a separate bounded share of each pass (CORE_API 1) | Same investigation as today |

## 11. Testing

- **Core with a fake module host**: an in-process double that records deliveries and returns scripted verdicts, delays, drops or garbage, driven by the existing fake `IClock` (the `GameLoopTests` pattern).
  Scenarios: trap Deny + redirect, redirect chain, timeout under both fail policies, two simultaneous entries, module death while an actor is parked, late verdict, dormant behavior skipped and logged once,
  SchemaVersion refusal, cascade/re-entrancy rejection, emitted events applying after a Deny (D8), required-module gate (D21), partial grant.
- **Module author without core**: a `ModuleTestHarness` library (phase 4 card): a fake core that feeds deliveries and timer events, captures `SubmitVerdict` / `Emit` batches, and offers a fake clock and an in-memory state document. A module's handler classes depend on
  contract DTOs only, so unit tests need no sockets.
- **Integration**: a test starting a real core against fake repositories (as `ServerLifecycleTests` does for telnet) with a real UDS in a temp dir and the trivial module of phase 4 (needs the gRPC packages restored).
- **No contract tests (D26)**: no golden files, descriptor diffs or `buf` until the contract settles after phases 3-5. **No conformance suite (D25).**
- **Module SDK helpers** (C#, optional, G1): connection loop, `Ping` loop, dev-token handling, a typed event router, a context wrapper (an object a behavior uses to call core: named methods that build event records). Deliberately small; the protocol can be spoken without it.
