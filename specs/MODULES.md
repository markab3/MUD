# Modules: manifest, lifecycle, deployment, worked examples, failure modes, testing

Part of [ARCHITECTURE_V2.md](ARCHITECTURE_V2.md). Message names refer to the .proto sketches in
[CORE_API.md](CORE_API.md) section 7. Q-numbers refer to [OPEN_QUESTIONS_V2.md](OPEN_QUESTIONS_V2.md). Status: DRAFT; docs only.

Contents: 1 Manifest, 2 Discovery and start order, 3 Health and supervision, 4 Development workflow, 5 Load, unload,
upgrade, 6 Packaging and systemd, 7 Example A desert, 8 Example B temple, 9 systemd vs child processes, 10 Failure
modes, 11 Testing and the module SDK.

## 1. Manifest

One `module.yaml` per module version, read by core (never trusted for authority: grants come from core's own file,
CORE_API 3.2). Sketch:

```yaml
name: desert                    # unique; also the component/overlay namespace ("desert.*")
version: 0.1.0                  # semver of this build
api_version: 1                  # mud.core.v1
min_core: 2.0.0                 # refuse to start on older core
exec:                           # how core spawns it
  command: dotnet
  args: ["Desert.dll"]
  cwd: .
start:
  mode: on_area_load            # on_area_load | eager | manual (dev)
  order: 100                    # lower first among modules started together
  depends_on: []                # module names that must be Ready first
  ready_timeout_ms: 10000
areas: [desert]                 # areas this module owns; the area's YAML header names the module too (Q9)
components:                     # namespaces and optional schemas for module-owned component data
  - name: desert.exposure
    schema: schemas/exposure.schema.json    # optional JSON Schema; core validates writes if present
verbs: []                       # commands this module contributes (temple: ["pull"])
subscriptions:
  - event: room.entered
    pre: false
    filter: { area_prefixes: [desert], has_components: [desert.exposure] }
  - event: timer.fired
capabilities:                   # REQUESTED; core grants a subset (module-grants.yaml)
  - entity.read:area=desert.*
  - overlay.write:kind=room,area=desert.*
  - component.write:ns=desert
  - message.send:scope=room
  - timer.schedule
limits:
  overlay_lease_max_ms: 300000
on_disconnect: keep_until_lease # keep_until_lease | clear_now | freeze  (CORE_API 4.2)
health:
  heartbeat_ms: 5000
  misses_allowed: 3
restart:
  policy: on_failure            # always | on_failure | never
  backoff_ms: [1000, 2000, 5000, 15000, 30000]
  max_restarts: 5
  window_s: 300
```

Fields that determine how risky a module is (subscribes to pre-events, requests write capabilities, `freeze`) are
shown by `modules show <name>` to the admin when installing.

## 2. Discovery and start order

- Layout (mirrors the existing `releases/` + `current` convention of `scripts/deploy.sh`):
  `~/.local/share/mud/modules/<name>/<version>/{module.yaml, binaries...}` and `modules/<name>/current -> <version>`.
  Core scans `modules/*/current/module.yaml` at startup and on the admin command `modules reload`.
  Grants live in `~/.config/mud/module-grants.yaml` (owner-edited, never written by modules or deploy tooling).
- Start order of core: lock + sockets up -> persistence worker, spool replay -> area import and world load (as today) ->
  module supervisor starts -> `mode: eager` modules in `order`/`depends_on` order -> front ends are allowed to open
  sessions -> `mode: on_area_load` modules start as `area.loaded` is emitted for their area (at world load for areas with
  rooms, or when the first creator imports/loads the area). Core does **not** wait for non-required modules before opening
  the game. A `required: true` flag (waiting for Ready before opening sessions) is possible; recommendation: no (Q30).
- A module that comes up after players are already in its area runs a **reconcile** step: `Ready` is sent after it has
  read `GetAreaGraph` / its `ModuleState` and compared with world state. Events that happened before `Ready` are not replayed
  (they are hints, CORE_API 2.7); the module derives current truth from queries.

## 3. Health and supervision

Supervisor is a small, hand-written loop inside core (not a framework), one state machine per module:

```
  STOPPED --start--> STARTING --Hello ok + Ready--> RUNNING --Stop--> STOPPING --Goodbye / exit--> STOPPED
     ^                  | no Hello within ready_timeout            |
     |                  v                                          | process exit or stream lost,
     |             BACKOFF <----------------------------------------+ heartbeat misses, strike limit
     |                  | wait backoff[n], n++, if n > max_restarts in window -> FAILED
     +-- admin `modules start` clears FAILED / QUARANTINED
```

- Liveness: the stream is open AND heartbeats arrive (`Heartbeat` every `heartbeat_ms`, both directions);
  `misses_allowed` consecutive misses = hung -> forced stop and restart. `Heartbeat.last_seq_processed` also reveals a
  module that is alive but not consuming events (lag metric).
- Process exit detected via `Process.Exited`; exit code logged; stdout/stderr captured and re-logged with the module name.
- Restart policy: backoff list above (reset after 10 minutes stable); `max_restarts` within `window_s` -> state `FAILED`,
  loud log line, **no further restarts** until an admin acts. This is the crash-loop guard.
- Strikes: pre-event timeouts, protocol violations, quota breaches add strikes (CORE_API 2.5, 5). A hung-but-connected
  module is skipped for pre-events once it exceeds the strike threshold and probed by heartbeat.
- Poisoned event: see CORE_API 2.7 (quarantine pattern after repeated crash-on-same-event).
- Core's own health to modules: core `Heartbeat` messages; a module that sees none for 3x the interval exits
  (so a hung core does not leave orphans running forever).

## 4. Development workflow (run a module by hand against a core)

1. Start a scratch core with its own socket directory and database: `MUD_RUN_DIR=/tmp/mudtest MUD_MONGO_URI=... (scratch DB)`;
   config `modules: { desert: { mode: manual } }` means "do not spawn, accept a hand-started instance".
2. Mint a dev token: `core-admin issue-dev-token desert --ttl 1h` (prints once; stored hashed in memory only).
3. Run the module from the IDE or `dotnet run` with `MUD_MODULE_NAME=desert MUD_MODULE_TOKEN=<token>
   MUD_CORE_SOCKET=/tmp/mudtest/modules.sock`. Debugger attaches normally because it is an ordinary process.
4. Edit, rebuild, kill, rerun: core sees stream loss -> overlays kept until lease (or cleared if configured `clear_now` for dev) -> new `Hello`
   reconciles. Fast loop without restarting core.
5. Tail core log filtered by `trace_id` to see the full conversation of a command.
6. No core available: use the test harness (11) which hosts the handler against a fake core.

Mongo is not available to agents in the sandbox, so none of this was exercised; it is the intended workflow.

## 5. Load, unload, upgrade

### 5.1 Load

`modules start <name>` (or area-triggered): validate manifest -> compute granted capabilities -> spawn with cleaned
environment (`PATH`, `HOME`, `XDG_RUNTIME_DIR`, `DOTNET_ROOT`, plus `MUD_MODULE_*`; **no** `MUD_MONGO_URI`) -> expect `Hello` within
`ready_timeout_ms` -> `Welcome` -> module sets subscriptions -> `Ready`. Core emits `module.started`.

### 5.2 Unload

Graceful: core sends `Stop{grace_ms}`; the module finishes in-flight handlers, clears its overlays (or leaves them to core), cancels
nothing (timers are core's and are cancelled by core), sends `Goodbye`, exits. After `grace_ms` core SIGTERMs; 5 s later SIGKILLs.
Forced (crash, kill, quarantine): no cooperation assumed. In both cases **core** performs the same cleanup, because the
module may be dead:

| Thing | On intentional unload (`modules stop`) | On crash / unexpected loss | On uninstall (`modules purge`) |
|---|---|---|---|
| Overlays | cleared immediately by core | per `on_disconnect`: default keep until lease, then expire | cleared |
| Timers | cancelled | cancelled (module re-creates from persisted state) | cancelled |
| Subscriptions, verbs | removed; verb entry replaced by "unavailable" stub while module installed | subscriptions inactive; verbs "unavailable" | removed |
| Pending pre-events/commands to it | resolved by fail policy | resolved by fail policy | n/a |
| Module component data on entities | **kept** (opaque if module absent) | kept | deleted only by explicit purge (admin confirm, audited) |
| `ModuleState` | kept | kept | deleted by purge |
| Entities it spawned (`owner = module`) | despawned by default, or kept inert if manifest says `spawn_policy: keep` | kept until `lease` (spawn lease optional), then despawned | despawned |
| Players inside its area | unaffected, they see the base description | same | same |

### 5.3 Upgrade

`modules upgrade <name> <version>`: install new directory, flip `current`, graceful stop of the old instance (overlays kept to
lease because this is expected to be seconds), start new instance, `Hello{module_version}` -> reconcile. Downgrade is the same operation
in reverse; component `schema_version` rules are the module author's contract: a version must either read all older data or refuse to run
(exit non-zero, supervisor goes to FAILED) rather than corrupt it. Core records `module_version` per module and logs changes, and
**refuses to apply** a component write whose `schema_version` is lower than the highest ever stored for that component, unless the module sets
`allow_downgrade` (Q29). Hot reload without stopping is not offered.

### 5.4 Data lifetimes in one sentence

Module data belongs to the world, not to the process: unloading a module never deletes world data, only the module's
live effects (overlays, timers, spawned entities).

## 6. Packaging and systemd on this host

Facts: systemd 249 on this host; user lingering and user units are what the existing services use
(`planar-realms.service`, `planar-realms-web.service` under `~/.config/systemd/user/`); `/run/user/1000` exists (mode 0700); the
installed .NET runtime is 8.0.28 under `/usr/share/dotnet`. The existing units do not set `DOTNET_ROOT`; the apphost presumably finds
`/usr/share/dotnet` by default **(unverified)**, but the task fixes `DOTNET_ROOT=/usr/share/dotnet` so V2 sets it explicitly.

Core unit (changes from today's `planar-realms.service` shown as comments):

```ini
[Service]
Type=simple
WorkingDirectory=%h/.local/share/mud/current
ExecStart=%h/.local/share/mud/current/MUD.Main
Environment=DOTNET_ROOT=/usr/share/dotnet           # new: also forwarded by the supervisor to children
Environment=MUD_RUN_DIR=%t/mud                      # %t = $XDG_RUNTIME_DIR for user units (unverified spelling for v249; documented as %t)
EnvironmentFile=%h/.config/mud/mongo.env            # ONLY the core unit reads this
KillMode=control-group                              # default: stop core => all module children are signalled too
TimeoutStopSec=60                                   # allow graceful module stop + persistence drain (ShutdownTimeout is 30 s today)
Restart=on-failure
```

Front-end units (phase 7): `planar-realms-telnet.service` (After/Requires core socket via `ExecStartPre` waiting for `frontends.sock`,
or simply retry with backoff; no ordering dependency needed because the front end reconnects) and the existing `planar-realms-web.service`
without `mongo.env` once phase 6 lands.

Module build/deploy: extend `scripts/deploy.sh` (later) to publish each module to `releases/<id>/modules/<name>/` and write
`module.yaml`; `modules reload` picks up the new release via the `current` symlink. Modules are framework-dependent publishes
using the same runtime as core (`DOTNET_ROOT`); self-contained would be bigger and is not needed. Non-.NET modules (Q3) would bring their own runtime.

Is the 8-core host a constraint? No: each module is one idle process, tens of MB (**estimate, unverified**).

## 7. Worked example A: the desert and its sandstorm

Content: area file `desert.yaml` with header `modules: [desert]`; 30 rooms; exterior rooms carry a module component
`desert.exposure { exterior: true }` (in YAML under the room's `components:`; ARCHITECTURE_V2 5.5, Q9). Interior rooms (the temple, example B)
do not.

### 7.1 Module state and keys

| Data | Where | Why |
|---|---|---|
| `desert.exposure` on rooms | room component (core-persisted, authored config) | which rooms a storm can enter |
| Storm record `{ storm_id, path:[room ids], index, started_at, next_step_at }` | `ModuleState` key `storms` (core-persisted) | the module owns the storm; core persists it (Q5, Q7) so a restart can resume |
| The visible effect | overlay `overlay.desert.storm-<id>` on the current room(s) | the only thing core renders; restorable by core |
| Spawn schedule | core timer `storm-spawn` + `ModuleState.next_spawn_at` | cadence survives restarts |

### 7.2 Sequence: start, learn the world, spawn a storm, drift, end

```
Actor            Core (game thread)                          desert module (own process)
-----            ------------------                          ---------------------------
(boot) area import done, rooms loaded
                 AreaLoaded(desert) -> supervisor.start("desert")
                 spawn child: env MUD_MODULE_NAME=desert MUD_MODULE_TOKEN=T MUD_CORE_SOCKET=.../modules.sock
                                                              process starts
                 <------------------- Connect() opens bidi stream
                 <------------------- ModuleToCore.Hello{module=desert, instance_token=T, api_version=1,
                                                           module_version=0.1.0, manifest_hash=H, last_seq=0}
                 check token, manifest hash, capabilities
                 -------------------> CoreToModule.Welcome{api_version=1, granted_capabilities=[entity.read:area=desert.*,
                                       overlay.write:kind=room,area=desert.*, component.write:ns=desert, message.send:scope=room,
                                       timer.schedule], accepted_subscriptions=[room.entered{filter area desert, has desert.exposure},
                                       timer.fired], limits{...}, resume_from_seq=0, owned_overlays=[]}
                 <------------------- Request{request_id=r1, GetAreaGraph{prefix=desert}}
                 -------------------> Response{request_id=r1, status OK, rooms:[{id, exits[], x,y,z, components{desert.exposure}}...]}
                                      module builds adjacency of exterior rooms (the sandstorm graph)
                 <------------------- Request{r2, GetModuleState{key=storms}}
                 -------------------> Response{r2, OK, not found (first run)}
                 <------------------- Request{r3, ScheduleTimer{id=storm-spawn, every=30m, jitter=5m}}
                 -------------------> Response{r3, OK}
                 <------------------- Ready{}
                 emit post-event module.started

 ...30 minutes later...
                 timer fires on game thread (core clock)
                 -------------------> Event{seq=1, name=timer.fired, payload TimerFired{id=storm-spawn}}
                                      module picks a start room and a drift path (random walk over exterior rooms, e.g. 12 steps),
                                      creates storm record s17
                 <------------------- Request{r4, ApplyBatch{
                                         mutations:[ SetOverlay{entity=desert.dune_3, overlay_id=storm-17, layer=100,
                                                      patch{description_append="A roaring wall of sand sweeps across the dunes.",
                                                            extra_handles:[sand,storm]},
                                                      lease_ms=60000, announce="A sandstorm rolls in." } ],
                                         preconditions:[ require_present(desert.dune_3) ] }}
                 game thread: re-check capability/scope (room in area desert), quota, preconditions OK
                              apply: attach overlay component, bump version, queue room snapshot to persistence worker
                              emit post room.changed{room=desert.dune_3, source=module:desert}
                              render announce to occupants of dune_3: Output{session=…, GAME, "A sandstorm rolls in."} per player
                 -------------------> Response{r4, OK, versions}
                 <------------------- Request{r5, PutModuleState{key=storms, doc=s17, expected_version=0}}
                 -------------------> Response{r5, OK, version=1}
                 <------------------- Request{r6, ScheduleTimer{id=storm-17-step, every=10s}}
                 -------------------> Response{r6, OK}

 ...every 10 s...
                 -------------------> Event{timer.fired storm-17-step}
                                      step: from dune_3 to dune_4
                 <------------------- Request{rN, ApplyBatch{ mutations:[ ClearOverlay{dune_3, storm-17},
                                         SetOverlay{dune_4, storm-17, lease 60s, ...},
                                         ], preconditions:[ require_present(dune_4) ] }}   // atomic: storm is never in two places or none
                 game thread applies atomically; Output to occupants of dune_3 "The storm moves on." (clear announce)
                                                  and of dune_4 "A sandstorm rolls in."
                 -------------------> Response{rN, OK}
                 <------------------- Request{rN+1, PutModuleState{storms, index=1, expected_version=1}}   (every step; batched into ApplyBatch
                                                                        is NOT possible in v1 - separate request; see Q7 for folding state writes into ApplyBatch)

 ...end of path...
                 <------------------- ApplyBatch{ClearOverlay last room}, CancelTimer{storm-17-step}, PutModuleState{storms: remove s17}
```

Notes:
- Request/response correlation is by `request_id` over the one stream (CORE_API 7.1). Every line above is a real message in the sketch.
- If an exterior room has been **deleted** between steps (web delete, WEB_EDITING 15), `require_present` fails with `NOT_FOUND`; the module re-reads the graph and re-routes.

### 7.3 How the module reacts to players entering

Post-event subscription only (not pre): a storm does not veto movement.

```
 player moves into dune_4 (storm there)
 Core: MoveCommand -> entity.move has no pre-subscribers for this destination (desert has none) -> commits immediately (zero IPC)
       post: Event{seq=41, name=entity.moved, subject=player17, EntityMove{from=dune_3,to=dune_4,reason=walk}}
             core also emits room.entered{room=dune_4, entity=player17}
 filter: desert's subscription room.entered{area desert, has desert.exposure} matches -> Event delivered
 desert: if a storm overlay exists on dune_4 (it knows: its own storm record) ->
         Request{r20, SendMessage{target=entity player17, text="Stinging sand scours your face.", channel=GAME}}
         -> Response OK (rate limit: 20 msg/s per module)
 optionally: ApplyBatch{ SetComponent{player17, "desert.sand_blind", {until: t+30s}} } -> persisted as module component data
```

The room *description* a player sees on `look` needs no IPC: core composes definition + overlays locally (CORE_API 4.2). A
player who is already standing in dune_4 when the storm arrives is told by the overlay's `announce`, which core renders to the
current occupants of that room via the same session output path as `TellRoom` today (`Room.TellRoom` -> `ReceiveMessage`, then front end).

### 7.4 What happens on a crash or unload mid-storm

**Crash (SIGSEGV, exception, OOM):**

```
 t0  stream error / process exit seen by core (Process.Exited + gRPC stream completion)
 t0  supervisor: RUNNING -> BACKOFF. overlays storm-17 on dune_4 marked orphaned. policy keep_until_lease: leases (60 s, last renewed by step at t0-4s) remain.
     timers storm-spawn, storm-17-step cancelled (core-owned, scoped to connection)
     pending pre-events to desert: none (it has none)
 t0+1s  restart #1 (backoff_ms[0]=1000): spawn child with a NEW token
 t0+2s  Hello{last_seq=<last seen>} -> Welcome{..., owned_overlays:[{dune_4, storm-17, expires t0+56s}]}
        desert: GetModuleState{storms} -> s17 {index=2 ...}; compares with owned_overlays:
          adopt: re-attach to its own record; ApplyBatch{SetOverlay renew lease}; ScheduleTimer(storm-17-step) again
        (the storm is delayed by the downtime, not lost)
 if the module never returns (FAILED after max_restarts): leases expire -> core removes overlays -> rooms back to normal; ModuleState kept
```

Who restores the rooms? **Core**, by lease expiry (module crashed) or by clearing overlays (unload). The module
never rewrote the room definition, so there is nothing to "restore" in authored data. Worst-case visible damage: a frozen
storm for up to `overlay_lease_max_ms` (5 min cap).

**Core crash:** overlays are persisted with the room (state) with absolute `lease_expires_at`; on restart core loads rooms, drops any expired
overlay at load, restarts the module, which reconciles as above. Because the lease is an absolute time against core's clock, downtime counts against
the lease (a storm that was due to expire will have expired).

**Unload (admin `modules stop desert`):** `Stop{grace_ms=5000}` -> desert: ApplyBatch{ClearOverlay each}, CancelTimer(s), PutModuleState (state kept) -> `Goodbye`
-> exit. If it does not comply, core clears all overlays owned by module `desert` itself at `STOPPED`. Result is the same: rooms normal, state kept, storm resumes on next load or is dropped by the module if stale.

### 7.5 Rate limits for this module

A storm step = 1 `ApplyBatch` (2 mutations) + 1 `PutModuleState` per 10 s per active storm; announce messages bounded by
occupants. With, say, 3 simultaneous storms: ~0.6 requests/s versus a budget of 50/s sustained. A bug that loops (a step per
millisecond) hits the token bucket after the burst of 100 and gets `RESOURCE_EXHAUSTED{retry_after_ms}`; sustained breach
quarantines the module (CORE_API 5) and overlays expire by lease.

## 8. Worked example B: the temple, its lever and traps

Content: temple rooms in the same desert area (or a separate area `temple` owned by module `temple`). Trap rooms carry
`temple.trap { armed: true, effect: "pit", pit_room: "temple.pit", group: "main" }`. The lever is a **feature** (or item) in
`temple.antechamber` carrying `temple.lever { group: "main", rearm_after_s: 600 }`. Module `temple` owns verb `pull`.
Declared in its manifest: subscription `entity.move` **pre**, priority 50, `can_modify: true`, `on_timeout: FAIL_OPEN`,
filter `has_components: [temple.trap]` evaluated against the **destination room**, and `include_components: [temple.trap]` so the event carries the
trap's component data (no extra round trip to read it). (`include_components` is a Subscription field not yet in the sketch.)

### 8.1 State, persistence and where it lives

| State | Location | Persisted by | Survives |
|---|---|---|---|
| `armed`, `group`, `effect` on each trap room | component `temple.trap` on the room entity | core (with the room document, as module component data) | core restart, module restart/unload/upgrade |
| `pulled`, `rearm_at` on the lever | component `temple.lever` on the lever entity | core | same |
| Timer to rearm | core timer, recreated by module from `rearm_at` when it connects | n/a (derived) | module/core restarts |

Because the state is component data on entities, **definition vs state** (DESIGN_PRINCIPLES 2; ROOM_FILES 5) is respected: the YAML sets
`armed: true` as configuration (definition `config`), runtime changes land in the component's `state` part which import never overwrites (ROOM_FILES 7.4 fix). Q5/Q9.

### 8.2 Sequence: walking into a trapped room (pre-event) - alter variant

```
Player            Telnet front end            Core (game thread)                                temple module
------            ----------------            ------------------                                -------------
"north"  ----TCP---> FrontEndToCore.Line{session=17, "north"}
                                              queue InboundItem.ForLine; game thread: ResolveCommand -> room exit "north"
                                              MoveCommand: build PendingAction a-901 (entity.move, from=temple.hall, to=temple.corridor_2, actor=p17)
                                              chain = [temple@50] (match: destination has temple.trap). state module=RUNNING
                                              actor p17 Parked; deadline = now + 50 ms
                                              ----> CoreToModule.Event{seq=88, event_id=e-5521, name=entity.move, is_pre=true, deadline_ms=50,
                                                      trace_id=T9, subject=p17, EntityMove{from=temple.hall,to=temple.corridor_2,via_exit=north,reason=walk}
                                                      + attached temple.trap{armed:true,effect:pit,pit_room:temple.pit,group:main,version:4}}
                                                                                                handler: trap armed => decide MODIFY
                                              <---- ModuleToCore.HandlerReply{event_id=e-5521, Verdict{decision=MODIFY,
                                                      modified_move{to_room=temple.pit, via_exit=north},
                                                      messages:[{target=actor, "The floor gives way!"}, {target=room_from, "{actor} vanishes through the floor!"}],
                                                      on_commit:[ PatchComponent{temple.corridor_2, temple.trap, {armed:false, fired_by:"p17"}, expected_version=4} ]}}
                                              inbound item PreEventReply(a-901, temple, MODIFY) on game thread:
                                              apply patch to PendingAction (to=temple.pit); no further pre-subscribers
                                              RE-VALIDATE: p17 still online & in temple.hall; temple.pit exists; exit still exists
                                              COMMIT: Living moved to temple.pit (ExitRoom/EnterRoom equivalent); on_commit mutations applied atomically
                                                      (expected_version=4 matches -> trap disarmed, version 5); queue snapshots (player, rooms)
                                              deliver messages; Output{session=17, GAME, "The floor gives way!"} ; p17 sees temple.pit description
                                              p17 un-Parked; queued commands drain
                                              post-events (fire-and-forget to subscribers):
                                              ----> Event{seq=89, name=entity.moved, EntityMove{from=temple.hall,to=temple.pit,reason=walk}}
                                                    (reason notes MODIFIED by temple; original_to=temple.corridor_2)
          <---- Output{...}                  <----  CoreToFrontEnd.Output{session=17, GAME, "The floor gives way!"}
 "The floor gives way!" rendered to ANSI
```

**Veto variant**: the verdict `DENY{deny_message_to_actor="A stone portcullis slams down in front of you."}`: core aborts, nothing moves,
actor un-parked, message delivered, no post `entity.moved`. 
**Post-event variant (no veto power)**: the module subscribes to `room.entered` post and *then* hurts the player: the player has
already arrived, the trap acts a moment later (`SendMessage` + a component write). Simpler, no parking, no re-validation, no latency on the move; but the
trap cannot *stop* or *redirect* the move, and between entering and the trap firing the player may have typed another command.

| | Pre-event (`entity.move`) | Post-event (`room.entered`) |
|---|---|---|
| Can veto / alter | yes | no (only react after) |
| Adds latency to the move | yes: one UDS round trip (budget 50 ms) only when the destination matches the filter | none |
| Complexity in core | parked actions, re-validation | none |
| Module down | per fail policy (below) | simply no reaction |
| Atomic with the move | yes (`on_commit`) | no (separate request, can fail/race) |

### 8.3 Latency on the critical path

- Only moves **into rooms carrying `temple.trap`** pay IPC (core evaluates the filter before emitting). The 30 desert rooms and
  every other command are unaffected: no subscribers -> fast path identical to today's `MoveToRoom`.
- Cost model: `serialise + UDS write + wake module + handler + UDS write + deserialise + one game-thread inbound hop (up to one loop pass)`.
  The game thread itself does no waiting. The inbound hop is bounded by `RunOnce` timing: replies are `InboundItem`s processed in the same
  pass as other inputs (`GameLoop.RunOnce` drains up to 100), so the reply is handled within one pass (<= ~100 ms worst case when the loop sleeps until the next tick; **in practice the loop wakes on any
  queue item** because it blocks in `TryTake` with a timeout, `GameLoop.cs`). 
- Budget: 50 ms per handler, 150 ms chain (Q2). A telnet player's own RTT is typically larger than this. Expect low single-digit ms on an idle module
  **(unverified; phase 4 benchmark)**.
- Concurrency hazard: two players enter the same trapped room at the same time. Each has its own parked action; both events are
  sent in `seq` order; the module answers the first with MODIFY (disarm via `on_commit` with `expected_version`), the second event, whose embedded
  component copy was taken before the first commit, would also say "armed". When the second commit applies its `on_commit` patch, `expected_version` no
  longer matches -> the **whole second action fails re-validation**: core re-emits the pre-event once with fresh component data (bounded to one retry) or aborts (Q15/Q16). One-shot semantics are therefore decided by core's
  version check, not by module memory.

### 8.4 The lever (module-owned verb, `command.invoke`)

```
"pull lever" -> Line{session=17,"pull lever"}
 Core: ResolveCommand: room exits -> MODULE VERBS: "pull" registered by temple -> command.invoke path
       actor parked; Event{seq=95, event_id=e-5600, name=command.invoke, is_pre=true, deadline_ms=100,
             CommandInvoke{verb=pull, args="lever", actor=p17, room=temple.antechamber, matched_entity=lever_1 (core resolved "lever" via MatchObjects),
                           attached temple.lever{group:main,pulled:false,version:2}}}
 temple: if pulled -> CommandResult{handled=true, output="The lever will not budge."}
         else HandlerReply{event_id=e-5600, CommandResult{handled=true, output:["You haul the lever. Somewhere deep in the stone, gears grind."],
              mutations (ApplyBatch):
                 PatchComponent{lever_1, temple.lever, {pulled:true, rearm_at:T+600s}, expected_version=2},
                 for each trap room in group main (module found them at Ready via QueryEntities{has_component=temple.trap, area=temple.*}):
                    PatchComponent{<room>, temple.trap, {armed:false}, expected_version=<its version>}
              messages: [{room, "{actor} pulls a lever. Gears grind in the walls."}] }}
 Core: re-validate, apply batch atomically (all traps disarmed or none), persist, deliver output.
 temple then Request{ScheduleTimer{id=rearm-main, at=rearm_at}} -> later Event{timer.fired rearm-main}
       -> Request{ApplyBatch[ PatchComponent trap armed:true x N, PatchComponent lever pulled:false ]}
```

Note that the lever does not need to be a *module* entity: it is a plain core `feature` with a module component; the module
supplies the verb and rules. Without the module, the lever still exists and is visible, just inert.

### 8.5 When the temple module is down: fail open or closed?

| Situation | Behaviour | Why |
|---|---|---|
| Move into trapped room; module down or struck out | **Fail open** (recommended default for this subscription): move proceeds, no trap, one log line per N seconds `temple pre-event skipped (module unavailable)` | A dead module must not make half the dungeon impassable; a trap is atmosphere |
| Same, owner wants "no trap = no entry" | Manifest `on_timeout: FAIL_CLOSED`: move denied with core text "Something blocks your way." | Appropriate only for rule enforcement; would lock players out when the module crashes |
| `pull lever` while down | Command fails: "That does not seem to work right now." Lever state unchanged | `command.invoke` has no sensible open/closed |
| Module restarts later | Reconcile on Ready: any lever with `rearm_at` in the past is re-armed immediately; traps keep their persisted `armed` value | State is persisted component data, not memory |
| Security-sensitive variant (a trap that guards a treasure vault door) | Use fail closed and accept lockout, **or** keep the check in core as a built-in (module not involved) | The owner decides per hook (Q11) |

Detection of "module down" is cheap and does not wait: the supervisor state is checked when the chain is built; `RUNNING` is the only state that
receives pre-events; others are skipped immediately by fail policy (no 50 ms penalty per move).

## 9. Child processes versus per-module systemd units (decision D12)

| | Core spawns children (recommended) | Core asks systemd to start `planar-realms-module@desert.service` |
|---|---|---|
| Satisfies "core starts them" | yes, directly | yes, indirectly (core runs `systemctl --user start`) |
| Cleanup when core dies | cgroup kill if under the core unit; outside systemd, modules self-exit when core heartbeat stops | modules would outlive core unless `PartOf=`/`BindsTo=` is set |
| Restart policy | hand-written (visible, testable, teachable) | systemd `Restart=`, `StartLimit*` (battle tested, less to write) |
| Resource limits / sandboxing | not per module (same cgroup) | per unit `MemoryMax`, `ProtectHome`, `PrivateTmp`, even `User=` |
| Logs | core re-logs child stdout | each unit has its own journal stream |
| Dev workflow | trivial | needs unit files per module and `daemon-reload` |
| Dependency on D-Bus/systemd | none | core depends on systemd being present |
| Fit with owner's learning goal | high: you write the supervisor | lower |

This is Q25. The manifest and the protocol do not change between the two; only who calls `fork/exec`.

## 10. Failure modes

| Failure | Detection | Behaviour | Recovery |
|---|---|---|---|
| **Core crash** (exception beyond the loop's catch, OOM, kill -9) | systemd sees exit (`Restart=on-failure`, RestartSec 5 in the existing unit); front ends and modules see stream closed | Sessions drop (as a server restart does today). Writes still queued in the persistence worker are lost unless spooled (kill -9 can lose them; documented in RUNTIME_STATE.md). Modules are signalled by cgroup stop and exit | Core restarts, replays spool, loads world, restarts modules; overlays whose lease expired are dropped; players log in again. A fatal loop error still runs the orderly shutdown with save (`GameLoop.OnFatal`) |
| **Module crash** | `Process.Exited` and stream error | Section 7.4. Pending pre-events resolved by fail policy; overlays per `on_disconnect` | Supervisor backoff restart; reconcile; `FAILED` after limit |
| **Module hang** (alive, not answering) | heartbeat misses; pre-event deadline misses (strikes) | Pre-events skipped by fail policy; after threshold the module is treated as unavailable | Forced stop (SIGTERM, then SIGKILL) and restart |
| **Module slow** (answers after the deadline) | `late_replies` metric; p99 handler latency | Late reply ignored; strikes; may be demoted | Owner fixes the module; budget raised in the manifest within core limits |
| **Front end crash** | stream loss; (web) HTTP errors | Sessions on that front end are disconnected = logout path (`Player.Quit`) unless linkdead grace is chosen (Q25). Other front ends and modules unaffected | systemd restarts the front end; players log in again |
| **Core restart with front end up** | front end's stream breaks | Front end closes or holds TCP connections per Q26; reconnects with backoff | Players re-login |
| **Stale socket file** | bind fails `EADDRINUSE`, or file exists without a live owner | Core holds the run-dir `flock`; if lock acquired the file is stale and is deleted and rebound | Automatic. If the lock is held: another core is running -> exit with a clear message |
| **Duplicate module instance** | second `Hello` for a connected module name | Rejected `ALREADY_EXISTS` unless it carries a newer core-issued token (restart overlap), in which case old one is told `Stop` | Supervisor kills the stray process; a hand-started dev module needs `mode: manual` |
| **Version mismatch** | `Hello.api_version`/`min_core`/`manifest_hash` | Refused with explanation in `Welcome.rejected` and in core's log; module not started | Upgrade module or core; unknown *events/fields* inside the same major are ignored |
| **Message flood from a module** | token bucket, outstanding request cap, message size cap | `RESOURCE_EXHAUSTED`; sustained -> quarantine (stream closed, no auto restart) | Admin clears after fix |
| **Message flood to a module** (core faster than module) | outbound queue 1000 | Drop post-events, send `ResyncRequired`; pre-events past cap resolved by fail policy | Module re-queries state |
| **Poisoned event** (always crashes the handler) | restart immediately after delivery of event E, twice | Quarantine `(module, event name, subject pattern)` for 10 min, log, continue delivering other events | Fix module; clear quarantine |
| **Mongo down** | persistence worker retries (existing behaviour) | Game continues from memory; snapshots queue/spool as today (`PersistenceWorker`, GAME_LOOP.md 8.3); module mutations behave like player mutations | Existing spool/replay |
| **Module mutation conflicts with web/creator edit** | `expected_version` / room re-validation | `ABORTED` to the module; web/creator edits go through the same single thread, so there is no lost update | Module re-reads and retries |
| **Parked actor disconnects** | session closed while action pending | Action cancelled; replies for its `action_id` discarded | none needed |
| **Handler reply for unknown/expired event** | no matching `event_id` | Ignored, counted | none |
| **Game thread slow (tick > 100 ms)** | existing warning in `GameLoop.Tick` | Pre-event deadlines are checked on ticks; a stalled loop extends parked time. Module work is a separate bounded share of each pass (CORE_API 1) | Same investigation as today |

## 11. Testing and the module SDK

- **Core with a fake module host**: an in-process `IModuleHost` double (same interface as the gRPC adapter, ARCHITECTURE_V2 4.3) that records emitted events and
  returns scripted verdicts, delays, drops or garbage. Tests drive core with `GameLoop` stepped by the fake `IClock` (the existing `GameLoopTests`
  pattern). Scenarios: trap MODIFY/DENY, timeout fail open and fail closed, two simultaneous entries, module death while an action is parked, late
  reply, quota rejection, overlay lease expiry and orphan reconciliation, unload cleanup.
- **Contract tests** for the .proto: golden files per message version; descriptor-diff test for breaking changes; a conformance suite that
  the in-process adapter and the gRPC adapter both pass (so the two cannot drift).
- **Module author without core**: a small test harness library (phase 4 deliverable): `FakeCore` implements the Core->Module side in memory
  (event injection, `Verdict` capture, request recording with simulated `Response`s, fake clock, overlay store). The module's handler class has no
  dependency on gRPC types beyond DTOs, so unit tests need no sockets.
- **Integration**: a test starting a real core against fake repositories (as `MUD.Tests` does, e.g. `ServerLifecycleTests`) with a real UDS in a temp dir and a trivial
  module over gRPC. UDS tests need no network but do need the gRPC packages restored.
- **Module SDK helpers** (C#, optional): connection loop with backoff, heartbeat, request-id generation, a seen-event window, a typed event router. Deliberately small and readable; the
  protocol can be spoken without it (Q3).
