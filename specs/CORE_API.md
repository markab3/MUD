# Core API: events, requests, gRPC over Unix domain sockets

Part of [ARCHITECTURE_V2.md](ARCHITECTURE_V2.md). Status: DRAFT. Docs only: the `.proto` text below is a sketch inside
markdown; no `.proto` file, project or package reference is added to the repo. Q-numbers refer to
[OPEN_QUESTIONS_V2.md](OPEN_QUESTIONS_V2.md).

Contents: 1 Threading rule, 2 Event model, 3 Operations modules and front ends may request, 4 Ownership and overlays,
5 Quotas, 6 Raw line to executed command, 7 gRPC design and .proto sketches, 8 .NET 8 specifics.

## 1. The one rule: every request becomes game-thread work, and the game thread never waits for a process

GAME_LOOP.md fixes one game thread that executes all world mutation and takes no locks. V2 adds new producers of work
(gRPC handlers) but keeps the consumer:

```
 gRPC handler thread (Kestrel pool)                 game thread (1)
 ---------------------------------                  -------------------------------------------
 1 decode protobuf
 2 authenticate stream (module/front end identity)
 3 check capability (static table lookup)
 4 check quota (token bucket, per module)
 5 check size/shape limits
 6 InboundItem.ForRequest(module, request,    --->  7 re-check preconditions against live world
                          TaskCompletionSource)      8 mutate entities, queue persistence snapshots
 9 await completion (with deadline)           <---  10 complete the TCS with Response
11 write Response to the stream (via the
   stream's single writer queue)
```

Steps 1-5 may run on any thread because they touch only immutable config and per-module counters (interlocked / a
lock local to the bucket). Steps 7-8 are the only ones that touch `World`. The existing mechanism is `GameLoop.Post`
with `InboundKind.WorldEvent` and `Action` (`MUD.Core/GameLoop.cs`); V2 adds a typed item kind rather than closures
so that the loop can account for them per module (this is a design statement, not code).

Fairness: `GameLoop.MaxItemsPerPass = 100` is shared today by players' lines and persistence completions. Module
requests are put on a **separate bounded queue** drained with a smaller per-pass share (recommended: at most 25 of
the 100 per pass), so a chatty module cannot starve player input. Both queues are visible in metrics (7.6 of
ARCHITECTURE_V2). Whether to make the share configurable is a detail, not a question.

The game thread never blocks on a module. It sends an event into that module's outbound queue (non-blocking, bounded) and
continues. When a module's answer is needed (pre-events), the *action* is parked (2.2), not the loop.

## 2. Event model

### 2.1 Concepts

- **Event**: a named, versioned message core emits at a defined point, with a subject entity, a payload, a
  `trace_id`, a core-assigned `event_id` and a per-module sequence number `seq`.
- **Subscription**: a module's declaration "send me events named N matching filter F, as pre or post, with priority P".
  Declared in the manifest (MODULES.md 1); core evaluates filters, so uninterested modules never see the event.
- **Handler**: code in the module reacting to an event. Pre-event handlers return a **verdict**; post-event handlers
  return nothing (an ack).
- **Pre-event**: emitted *before* the action commits. The action is parked until the chain finishes. Handlers may
  ALLOW, DENY, or MODIFY. Has a deadline.
- **Post-event**: emitted *after* the action committed and was queued for persistence. Notification only. Cannot undo
  the action (a module that wants to react does so by requesting a new mutation).
- A **command-shaped** event: `command.invoke` is a pre-event-shaped request/response sent only to the one module
  that owns a verb (2.4).

Naming: pre-events use the present form (`entity.move`), the matching post-event the past form (`entity.moved`).

### 2.2 Pre-events: parking the action instead of blocking the thread

State machine (game thread owns it; replies arrive as inbound items):

```
 player command "north"
   |
   v
 CommandQueue runs MoveCommand.DoCommand on the game thread
   |  builds PendingAction{ action_id, kind=entity.move, actor, from, to, exit, deadline = now + budget }
   |  chain = subscribers(entity.move, pre) filtered, sorted by (priority asc, module name asc)
   |  if chain empty -> commit immediately (fast path: identical to today's MoveToRoom, zero IPC)
   v
 actor.Parked = true   (further input from this actor is queued by CommandQueue; others unaffected)
 send EVENT(entity.move, is_pre, event_id, deadline_ms) to chain[0]'s outbound queue
   |                                                    ^
   |            game thread carries on with other work  |  module replies on its stream thread
   v                                                    |
 [later] inbound item PreEventReply(action_id, module, verdict) posted by the stream reader
   |
   +-- DENY    -> abort action: nothing changed; send deny message to actor; actor.Parked = false
   +-- MODIFY  -> apply patch to PendingAction (e.g. to = pit_room), continue chain with the *modified* action
   +-- ALLOW / ABSTAIN -> next in chain
   +-- no reply by deadline (checked by a core timer; tick granularity 100 ms) -> apply the subscription's fail policy (2.5)
   v
 chain done -> RE-VALIDATE (actor still exists, still in `from`, destination still exists, exit still present)
   -> commit (mutate world, persist, emit post-events) or abort with "Something prevents you."
   -> apply `on_commit` effects from verdicts, atomically with the commit
   -> actor.Parked = false, drain actor's queued commands
```

Why this works: no blocking, no locks. Cost: between emit and commit, other commands ran, so the world may have changed
(someone closed the exit, the destination was deleted). Hence the mandatory re-validation step. That cost is real
and is the main complexity of the whole design; it is the price of keeping the loop responsive (decision D5).

What "Parked" means in code terms today: `Living.NextActionAt` plus `CommandQueue` already express "this living may not
act yet" (`MUD.Core/Commands/CommandQueue.cs`; `DelayThen` sets `NextActionAt`). Parking reuses that idea with an
unknown end time. A parked actor still receives output (say/room messages) and can be attacked; whether a parked actor
may be targeted by other pre-events is a corner case to define in implementation (listed in the failure table).

Latency on the critical path: a command with an active pre-event chain costs at least
`serialise + UDS write + module scheduling + handler + UDS write + deserialise` per module in the chain, **sequentially**
(because MODIFY makes later handlers depend on earlier ones). Measured numbers do not exist; expected order is
sub-millisecond to a few ms per hop on a local UDS with an idle module **(unverified; to be measured in phase 4)**.
Recommended budget (Q2, Q16): per handler 50 ms, whole chain 150 ms, at most 3 pre-subscribers per event name. A telnet
player perceives anything under ~100 ms as instant. A slow module therefore makes *its own* commands slow, not the
server.

Parallel option: veto-only subscriptions (`modify: false` in the manifest) can be asked in parallel, which cuts the
chain to the slowest handler instead of the sum. Not in the first cut; noted for later.

### 2.3 Draft event catalogue

All events carry: `event_id`, `seq`, `trace_id`, `name`, `time` (core clock), subject ids. "Pre" column: P = a pre-event
exists (can veto/modify), - = post only.

| Event (post form) | Pre form | Subject | Payload | Notes |
|---|---|---|---|---|
| `entity.moved` | P `entity.move` | mover | from, to, via_exit, reason (walk, trans, spawn, login) | Replaces `Room.ExitRoom/EnterRoom` hooks. Filters: kind, area of from/to, component |
| `room.entered` / `room.left` | - | room | entity, other-room | Convenience projections of `entity.moved`, so a room-scoped module subscribes to a room/area, not to every move |
| `item.moved` | P `item.move` | item | from container, to container, actor, reason (get, drop, give, spawn) | get/drop commands |
| `entity.created` / `entity.destroyed` | P `entity.destroy` | entity | kind, definition key | Includes spawns by modules and `build` |
| `command.executed` | P `command.before` | actor | verb, args, success | `command.before` lets a module block a verb in a context (e.g. "no fighting here"). High volume: filter by verb |
| `command.invoke` | (request/response, 2.4) | actor | verb, args, room | Only to the module that registered the verb |
| `entity.describe` | P (modify-only) | described entity | viewer, text parts | Lets modules append/replace description text for a viewer (replaces virtual `Examine(Living)` overrides) |
| `session.login` / `session.logout` | - | player | session id, front end | Login veto stays in core (accounts, retirement are core rules) |
| `component.changed` | - | entity | component name, version, old/new | Only for components in the subscriber's namespace or explicitly allowed by capability |
| `tick.heartbeat` | - | (none) | tick number | Every 2 s (the existing heartbeat: `HeartbeatEvery = 20` ticks of 100 ms). Opt-in, filterable by area; **not** per 100 ms tick (flood) |
| `timer.fired` | - | module | timer id, scheduled time, payload | Timers a module requested (3.1) |
| `area.loaded` / `area.unloaded` | - | area | prefix, room count | Drives module start/stop (MODULES 2) |
| `module.started` / `module.stopping` | - | module | name | Lets modules coordinate |
| `world.shutdown` | - | world | deadline | Sent to all modules on core shutdown (graceful stop, MODULES 5.2) |
| `room.changed` | - | room | changed fields, source (creator, web, module) | Emitted after web/creator/module edits; this is also what front ends use to refresh their view of a room |

Which events may be pre is a decision (Q13): the table is the smallest set that supports the worked examples
(`entity.move` for traps, `entity.describe` for sandstorm text, `command.invoke` for the lever). Everything else is post only.

Events deliberately not offered: raw per-tick hooks per entity (replaced by `timer.fired` and `tick.heartbeat`), raw
output interception (would let a module read private tells; privacy and flood), login pre-event.

### 2.4 `command.invoke` and `entity.describe`

`command.invoke`: when a verb resolves to a module-contributed command, core sends one request to that module with
the parsed arguments, actor, room and a deadline. The module replies with a normal verdict-like `CommandResult`:
output messages, an `ApplyBatch` of mutations (applied atomically by core after re-validation), and `handled:
true/false` (false lets core continue to the next candidate or "Command not recognized").

`entity.describe`: pre-event, modify-only, fan-in to a pure function: core builds the base description, then offers it to
subscribers in priority order, each returning `append`, `prepend`, or `replace` text. The budget is tighter (20 ms
per handler, 3 handlers max) because `look` is the most common command. Alternative that avoids IPC on `look`
entirely: **overlays** (4), where the module pushes description changes ahead of time and core composes them locally. Overlays are
the recommended way for the sandstorm; `entity.describe` is only for text that depends on the viewer. This is Q2/Q13.

### 2.5 Timeouts and failure policy per hook

Each subscription declares `on_timeout` (and implicitly `on_unavailable` when the module is down): `allow` (fail
open) or `deny` (fail closed). Core default by event class, overridable by the manifest but **bounded by a core
policy file**:

| Event class | Default | Why |
|---|---|---|
| Atmosphere / flavour (`entity.describe`, trap damage, weather) | fail **open** | A dead desert module must not strand players |
| Rule enforcement (`command.before` for "no fighting", access control, payments) | fail **closed** | Failing open would let players bypass the rule |
| `command.invoke` | the command **fails** with a clear message ("That does not seem to work right now.") | Neither open nor closed makes sense for a module-owned verb |

A timeout also counts a strike against the module (MODULES 3); N strikes in a window flips it to "unavailable"
(skip pre-events without waiting) until it passes a health check, so a hung module does not add 50 ms to every move.

Answering after the deadline: the late reply is ignored (core checks `event_id` against pending actions; unknown ids are
dropped and counted). The module is not told off per message, but `late_replies` is a metric.

The trap example (MODULES 8) is fail open for a reason spelled out there; the owner may disagree per module (Q11).

### 2.6 Ordering and priority between modules

- Within one pre-event chain: ascending `priority` (integer, default 100, lower first), ties by module name (stable,
  deterministic). Priority is declared in the manifest and bounded by core's grants (a module cannot give itself
  priority 0 if the grants file says 50+).
- Post-events: core emits to all subscribers independently; **no ordering guarantee across modules**. Per module the
  order is the order of `seq`.
- Event order for one game-thread action: pre chain -> commit -> post-events in a fixed order: `item.moved` /
  `entity.moved`, then `room.left`, `room.entered`, then `command.executed`.
- Cross-module conflict (two modules MODIFY the same destination): later priority sees the earlier modification and may modify
  again; the last wins; every MODIFY is logged with module name. The owner may prefer "first MODIFY wins" or
  "conflict = DENY" (Q12).

### 2.7 Delivery guarantees and idempotency

| Property | Design |
|---|---|
| Delivery | **At-most-once per connection, with sequence numbers and resume.** Core assigns `seq` per module. On reconnect the module sends `last_seq`; core replays from a bounded in-memory ring (default 1000 events, 60 s) if it still has them, else sends `ResyncRequired` and the module re-reads the state it cares about with queries |
| Why not at-least-once | It needs persisted queues in core, ack bookkeeping and dedupe in every module. A learner-friendly alternative: "events are hints; state is authoritative and queryable". Post-events are therefore **hints**; modules must be able to rebuild from queries |
| Pre-events | Never replayed (the action has already timed out) |
| Idempotent mutations | Every `Request` carries `request_id` (module-chosen, unique per connection run). Core keeps the last N=1000 results per module and returns the stored result on a repeat. Mutations also support `expected_version` preconditions (3.3) for optimistic concurrency |
| Duplicate event handling | Handlers should be idempotent anyway (replay overlap); events carry `event_id`; the SDK helper (MODULES 11) offers a "seen window" |
| Poisoned event | A handler that crashes the module on one event: core sees the module restart immediately after delivering event E repeatedly. After 2 consecutive restarts "within one event" core quarantines that `(module, event name, subject)` pattern for 10 minutes and logs it (MODULES 10) |

Open: whether at-least-once with persisted queues is worth it for post-events (Q14).

## 3. Operations modules and front ends may request

### 3.1 Module -> core operations

| Operation | Purpose | Capability (examples) | Notes |
|---|---|---|---|
| `GetEntity(id)` | Read an entity's core components and visible module components | `entity.read` (scoped by `area=` or `kind=`) | Returns a snapshot with component versions |
| `QueryEntities(filter, page)` | By kind, area prefix, location, component present, simple equality on top-level component fields | `entity.read` | Read from the **live in-memory world on the game thread**, not Mongo. Bounded page size (default 200). Large scans are quota-charged |
| `GetAreaGraph(prefix)` | Rooms of an area with exits and X/Y/Z, as one call | `entity.read` | How the desert module "learns which rooms exist" (MODULES 7); see also `area.loaded` |
| `ApplyBatch(mutations, preconditions)` | Atomic list of: `SetComponent`, `PatchComponent`, `RemoveComponent`, `SetOverlay`, `ClearOverlay`, `MoveEntity`, `SpawnEntity`, `DespawnEntity` | per mutation type and scope | All-or-nothing on the game thread; size limited (default 100 ops) |
| `SendMessage(target, text, channel)` | Output to an entity, a room (with exclusions), or an area | `message.send` scoped to `room`/`area` | Core renders per recipient session (semantic markup, 7.2 of the main doc). Rate limited |
| `ScheduleTimer(id, delay or at or every)` / `CancelTimer(id)` | Make core call back later with `timer.fired` | `timer.schedule` | Timers are **owned by core** but scoped to the module's connection: cancelled when the module stops. A module re-creates them from its persisted state when it starts. Minimum interval 1 s, max 100 timers (Q21) |
| `GetModuleState(key)` / `PutModuleState(key, doc, expected_version)` | The module's private persisted state (4.3) | implicit for own namespace | Size-limited (default 256 KB per key, 1 MB per module) |
| `Subscribe/Unsubscribe` | Narrow or widen filters at runtime within the manifest's declared scope | implicit | Example: desert subscribes to `room.entered` only for rooms with an active storm |
| `Log(level, text)` | Structured log line into core's log with the module and trace id | implicit | Rate limited |

Explicitly **not** offered: arbitrary Mongo operations, writing core components other than those a capability names
(`component.write` on a *core* component such as `Description` is not grantable to modules in v1; they use
overlays), `DeleteRoom`, role/account changes, shutdown, direct session access (no reading raw input, passwords).

Spawn/despawn: `SpawnEntity` creates an entity from a definition key or an inline kind+components, with `owner =
module`, location and optional `lease`. `DespawnEntity` only for entities the module owns (an `owner` field is set at spawn).

### 3.2 Authorization: capabilities per module

- Capabilities are strings with optional scopes: `entity.read:area=desert.*`, `overlay.write:kind=room,area=desert.*`,
  `component.write:ns=desert`, `message.send:scope=room`, `timer.schedule`, `entity.spawn:kind=npc,max=20`,
  `event.pre:entity.move`, `event.post:room.entered`.
- **Requested** in the module manifest, **granted** by core in a file the module cannot edit
  (`~/.config/mud/module-grants.yaml`, mode 600, owner-edited). A module whose manifest requests more than is granted is
  started with the granted subset and a warning (or refused: Q31). The effective set is returned in `Welcome` and shown
  by a core admin command (`modules`).
- Checked at step 3 of section 1 on every request, and again at step 7 for scope that depends on live state (e.g. "is
  this room in my area?").
- Area scope uses the room's `Area` prefix (`Room.Area`, ROOM_FILES.md 10) as today.

### 3.3 Preconditions and consistency

A module's picture of the world can be stale (events are hints). Therefore mutations can carry:

- `expected_version` on a component (core increments `version` on every write): fail with `ABORTED` if changed
  (the module re-reads and retries);
- `require_present(entity_id)`: fail if the entity no longer exists or moved out of an expected container.

On `ABORTED`/`NOT_FOUND`, the response carries the current state so the module does not need another round trip.

### 3.4 Front-end -> core operations (separate service)

Session service (telnet and any future text client), bidirectional stream per front end (7.2):

- front end -> core: `Hello(frontend, token, caps)`, `OpenSession(client info: remote address string, terminal,
  width, height, supports_echo_control)`, `Authenticate(name, password)`, `CreatePlayer(name, password)`,
  `Line(session_id, text)`, `Prompt reply` (just a `Line` while a prompt is active), `CloseSession(session_id,
  reason)`, `ClientInfoChanged`.
- core -> front end: `SessionOpened(session_id)`, `AuthResult(ok, reason, message)`, `Output(session_id, channel,
  text, flush)`, `SetEcho(session_id, bool)`, `SetPrompt(session_id, text)`, `CloseSession(session_id, farewell, flush)`,
  `Ping`.

The throttle for passwords moves with the pre-login logic: **brute-force throttling stays in core** (it must see all
front ends together: a web login and a telnet login should count against the same account), while connection-level caps (per IP concurrent
connections, idle timeout, line length, input rate) stay in the front end (`MUD.Telnet/TelnetLimits.cs`,
`ConnectionTracker.cs`, `RateLimiter.cs`). Core still has `MaxQueuedLinesPerClient` and the drop counter as a second line of defence (`World.EnqueueLine`).

Admin/web service, unary RPCs (the actor is the authenticated player on whose behalf the call is made):
`Authenticate`, `GetAccount`, `ChangePassword`, `GetRoom`, `ListRooms(filter)`, `CreateRoom`, `UpdateRoom(allow-list, expected_rev?)`,
`DeleteRoom`, `GetRoomMap(area)`, `ListPlayers`, `GetPlayer`, `UpdatePlayer(allow-list)`, `SetRetired`, `ListAudit`.
Semantics are those of WEB_EDITING.md section 15 and the validation in `MUD.Data/RoomRules.cs` / `PlayerFields.cs`, now
enforced in core. How the web proves *who the actor is* on each call is Q23: (a) trust the web process's
assertion of `actor_player_id` (simple; the web is a trusted same-user process), or (b) core issues an opaque session
token at `Authenticate` which the web stores in its cookie and sends with each call (core re-checks role on every call).

## 4. Ownership of mutated data, overlays and restoring after a crash

### 4.1 The problem

Example A has a module changing room descriptions as a storm passes. If the module dies, who puts the rooms back? If
the module *rewrote* `LongDescription` the answer is "nobody knows the original". So V2 never lets a module rewrite
authored definition fields.

### 4.2 Overlays (recommended)

An **overlay** is a core-managed, module-owned layer on an entity, expressed as a component named
`overlay.<module>.<overlay_id>`:

```
Overlay {
  owner_module   // set by core, not by the module
  overlay_id     // module chosen, e.g. "sandstorm-17"
  layer          // integer ordering among overlays (default 100)
  patch {        // restricted, typed vocabulary - NOT arbitrary field writes
    description_append / description_prepend / description_replace_long : string (semantic markup)
    exits_blocked: [exit names]  // optional, only if capability allows
    features_hidden: [feature short]  // optional
    extra_handles: [...]
  }
  lease_expires_at   // absolute time; core removes the overlay when it passes
  version
}
```

Core composes the effective description from definition + overlays in layer order when someone looks at the room. The
authored definition (`Short`, `Long`, `Exits`, `Features`) is untouched, so import (ROOM_FILES 6), web edit and export
(`AreaExporter`) never see overlay content; "definition versus state" (DESIGN_PRINCIPLES 2) stays true: an overlay is
**state**. Overlays are persisted with the entity (state), so a core restart does not forget them.

Restoration: three layers, any of which suffices:

1. **Module-side**: normal path - module clears its overlays when the storm leaves a room (`ClearOverlay`).
2. **Lease**: every overlay has a lease (default 5 minutes, set by the module up to a manifest maximum). The module renews
   while healthy. If the module crashes, rooms revert automatically when the lease passes, without the module. Cost: a
   crash leaves a storm frozen on rooms for up to a lease length (default 5 min; configurable per overlay).
3. **Core-side on lifecycle**: when a module's stream ends for any reason, core marks that module's overlays
   `orphaned`. Policy per module (manifest `on_disconnect`, bounded by grants): `keep_until_lease` (default), `clear_now`, or
   `freeze` (keep indefinitely until module returns; dangerous). On a *graceful unload* (MODULES 5.2) the default is
   `clear_now`. On reconnect the module receives its overlay list in `Welcome` (`owned_overlays`) and either adopts, renews or clears each
   (reconciliation).

An overlay on an entity that is deleted or moved is deleted with it.

Alternative considered: "snapshot and restore": module changes the room fields and core remembers the previous values to
restore. Rejected: restoring requires knowing whether anything else changed those fields meanwhile (web edit, another module,
import), which is exactly the multi-writer conflict V2 is trying to remove. Overlays make "undo" a delete of one layer.

### 4.3 Module component data and module-private state

- **Component data** on entities (`temple.lever` on the lever): `SetComponent/PatchComponent` within the module's
  namespace. Stored on the entity document under `components.<module>.<name>` with `version`. Persisted via the
  normal snapshot path (the entity document is snapshot like today's Room/Player).
- **Private state** (`ModuleState`): key -> document with `version`, stored in a `module_state` collection by core.
  Not attached to an entity (the sandstorm schedule, next spawn time). Core does not interpret it.
- **On unload**: both are **kept** by default (MODULES 5.4). Deleting data on unload would destroy the world when someone
  merely restarts the module for an upgrade; a separate explicit admin operation (`purge-module <name>`) removes it.
- Core must be able to load entities whose components belong to a module that is not installed: they are carried as
  opaque BSON ("unknown component preserved"), so uninstalling and reinstalling never loses data.
- Auditing: see ARCHITECTURE_V2 7.4.

## 5. Quotas and rate limits

Per module, enforced at steps 4-5 of section 1 (before the game thread sees the work). Numbers are starting
proposals for the owner to tune (config file), not measured:

| Limit | Default | On exceed |
|---|---|---|
| Requests per second (token bucket) | 50 sustained, burst 100 | `RESOURCE_EXHAUSTED` with `retry_after_ms` |
| Mutations per `ApplyBatch` | 100 | `INVALID_ARGUMENT` |
| Message size on the stream | 256 KB (gRPC `MaxReceiveMessageSize`) | stream error; counts as strike |
| `SendMessage` per second | 20 sustained | rejected |
| Outstanding unanswered requests | 64 | rejected |
| Active timers | 100, min period 1 s | rejected |
| Overlays | 500 per module, 10 per entity | rejected |
| Spawned entities | `max` in capability (default 50) | rejected |
| Queries: entities scanned per second | 5000 | rejected |
| Outbound event queue to the module (core -> module) | 1000 events | **drop post-events** for that module, send `ResyncRequired` later; pre-events past the cap are treated as timeouts (2.5) |

Persistent abuse (e.g. >50% rejected for 60 s) -> core logs, flips the module to `quarantined` (stream closed, not
restarted until an admin clears it; supervisor treats it as a crash loop).

Rate numbers for Example A: a storm moving every 10 s across ≤ 5 rooms changes ~1 mutation/s; far inside the budget.

## 6. From a raw line to an executed command

```
 telnet: "get sword"                                  core
   |                                                    |
   | FrontEndToCore.Line{session=17, "get sword"}  ---> | stream reader thread: validate session, enqueue InboundItem.ForLine
   |                                                    | game thread:
   |                                                    |  1 session 17 -> Player P (World._sessions equivalent)
   |                                                    |  2 if P.HasPrompt: send line to prompt handler (password) and return
   |                                                    |  3 ResolveCommand: room exits -> module verbs (in manifest order) -> known -> role -> default
   |                                                    |  4 built-in: ParseCommand now, CommandQueue.AddCommand (as today)
   |                                                    |     module verb: emit command.invoke to owning module, park actor (2.2)
   |                                                    |  5 DoCommand: mutate world, queue persistence
   |                                                    |  6 P.ReceiveMessage(text) -> Output to session 17's outbound queue
   | <--- CoreToFrontEnd.Output{session=17, GAME, "You get a sword."} (single writer per stream)
   | render %^Token%^ -> ANSI per the session's terminal, write to the socket
```

Rules:

- The front end sends **lines**, never parsed commands; core is the only place that knows verbs.
- `Redact` for `ISensitiveInputCommand` (today `Player.DataReceivedHandler`) stays in core. Logging of inbound lines on
  the core and front end must never include a line sent while a prompt with echo-off is active.
- The `stop` command clears the actor's queue and cancels a parked action (a parked action cannot be cancelled once
  it is committing; define in implementation).
- Output is **semantic**: `text` is markup (7.2 of the main doc, option A) + `channel`. The front end never parses game
  meaning.

## 7. gRPC design

### 7.1 Unary vs streaming for modules: evaluation

| Option | How it works | For | Against |
|---|---|---|---|
| **A. One bidirectional stream per module (recommended)** | Module dials core, opens `Connect`, both sides send typed envelopes. Events, replies, requests, responses, heartbeats multiplex over one stream | Single connection to supervise: stream alive = module alive. Core can push without the module hosting a server. Ordered delivery per module for free. Easy to log line by line. Module needs only a gRPC *client* | Hand-written correlation (`request_id`, `event_id`); one slow reader blocks the stream (head-of-line); a single `WriteAsync` at a time (needs a writer queue); no per-call deadlines (use fields) |
| B. Per-call unary both ways | Core calls the module's server for each event; module calls core's unary RPCs | Per-call deadlines/status for free; ordinary gRPC; parallelism | Every module must host a gRPC *server* on its own socket (core must discover, dial, supervise it; two sockets per module); no ordering guarantee; health must be inferred separately; more files and permissions |
| C. Module-streams-in, core calls out with unary | Mixed | Request-response ergonomics for module->core | Still needs a module server for events |
| D. Server-stream (events) + unary (requests) from module | Module opens `Subscribe` (server stream) and calls unary RPCs for requests | Per-call deadlines/status on module requests; no manual request ids for them; simple mental model | Two things to keep alive; pre-event replies would be unary `Reply(event_id, verdict)` calls: fine. Heartbeat must be separate. Ordering between a reply and subsequent events is not tied together. **Strong second choice** |

Recommendation: A for modules (lifecycle = connection, minimal surface, best for learning the pattern: you can see the
whole conversation in one log), with B/D noted as the alternative if hand-rolled correlation proves tedious (Q18).
Front ends use A for **sessions** (a stream per front end process) and unary RPCs for web admin calls.

Pitfalls to design around (all standard gRPC behaviour; the .NET specifics are in section 8):
- Only one writer at a time per stream: all sends go through one outbound `Channel<T>` per stream drained by a single task (the same pattern as `MUD.Telnet/OutboundQueue.cs` and GAME_LOOP.md decision 4).
- Slow reader: HTTP/2 flow control eventually blocks the writer; the bounded queue turns that into the drop/resync policy of section 5 instead of unbounded memory.
- Half-open connections on a UDS are rare (same kernel); application-level heartbeat (every 5 s, 3 misses = dead) still catches a hung but connected module, which transport keepalive would not.

### 7.2 Services (sketch)

All text below is design sketch; field numbers are illustrative and must be reserved and never reused once real.

```proto
// ---- mud/core/v1/module.proto (SKETCH) ----
syntax = "proto3";
package mud.core.v1;
import "google/protobuf/struct.proto";

service ModuleHost {
  // Module dials core. One stream per module instance for its lifetime.
  rpc Connect(stream ModuleToCore) returns (stream CoreToModule);
}

message ModuleToCore {
  string trace_id = 1;                     // optional; echoes the trace of the event/request this answers
  oneof body {
    Hello       hello     = 10;            // first message, exactly once
    Ready       ready     = 11;            // after the module has set up its subscriptions/timers
    HandlerReply reply    = 12;            // answer to a pre-event / command.invoke
    Request     request   = 13;            // module -> core operation
    Heartbeat   heartbeat = 14;
    Goodbye     goodbye   = 15;            // graceful stop acknowledged, safe to kill
    Ack         ack       = 16;            // for post-events: highest processed seq (batched)
  }
}

message CoreToModule {
  oneof body {
    Welcome  welcome   = 10;
    Event    event     = 11;
    Response response  = 12;               // answer to a Request
    Heartbeat heartbeat = 13;
    Stop     stop      = 14;               // graceful: finish up, clear overlays, send Goodbye
    ResyncRequired resync = 15;            // some post-events were dropped; re-read state
  }
}

message Hello {
  string module = 1;                       // must match the manifest the supervisor started
  string instance_token = 2;               // secret passed via environment at spawn; proves it is the spawned child
  uint32 api_version = 3;                  // highest mud.core.vN the module speaks
  string module_version = 4;               // semver of the module build
  string manifest_hash = 5;                // detects "manifest on disk changed since core read it"
  uint64 last_seq = 6;                     // resume point after reconnect (0 = fresh)
}

message Welcome {
  uint32 api_version = 1;                  // version both sides will use
  string core_version = 2;
  repeated string granted_capabilities = 3;
  repeated Subscription accepted_subscriptions = 4;
  repeated string rejected = 5;            // human-readable reasons
  Limits limits = 6;                       // quotas the module should respect
  uint64 resume_from_seq = 7;
  repeated OverlayRef owned_overlays = 8;  // reconciliation after restart (4.2)
}

message Subscription {
  string event = 1;                        // e.g. "entity.move"
  bool   pre = 2;
  int32  priority = 3;                     // lower first
  bool   can_modify = 4;
  FailPolicy on_timeout = 5;               // ALLOW or DENY
  Filter filter = 6;
}
enum FailPolicy { FAIL_POLICY_UNSPECIFIED = 0; FAIL_OPEN = 1; FAIL_CLOSED = 2; }
message Filter {
  repeated string area_prefixes = 1;
  repeated string entity_kinds = 2;
  repeated string has_components = 3;
  repeated string room_ids = 4;
  repeated string verbs = 5;               // command.* events
}

message Event {
  uint64 seq = 1;                          // per-module, gap-free while connected
  string event_id = 2;                     // unique; the key replies refer to
  string name = 3;
  bool   is_pre = 4;
  uint32 deadline_ms = 5;                  // relative to send time; 0 for post-events
  string trace_id = 6;
  string subject_id = 7;
  oneof payload {
    EntityMove entity_move = 20;           // for both entity.move and entity.moved
    ItemMove   item_move   = 21;
    CommandInvoke command_invoke = 22;
    Describe   describe    = 23;
    SessionEvent session   = 24;
    ComponentChanged component_changed = 25;
    Tick       tick        = 26;
    TimerFired timer_fired = 27;
    AreaEvent  area        = 28;
    google.protobuf.Struct generic = 29;   // forward-compat escape for new events
  }
}
message EntityMove {
  string entity_id = 1; string from_room = 2; string to_room = 3;
  string via_exit = 4;  string reason = 5;           // walk | trans | login | spawn ...
  string actor_id = 6;
}

message HandlerReply {
  string event_id = 1;                     // matches Event.event_id
  Verdict verdict = 2;
  CommandResult command_result = 3;        // for command.invoke
}
message Verdict {
  enum Decision { DECISION_UNSPECIFIED = 0; ALLOW = 1; DENY = 2; MODIFY = 3; ABSTAIN = 4; }
  Decision decision = 1;
  string   deny_message_to_actor = 2;
  string   deny_message_to_room = 3;
  EntityMove modified_move = 4;            // for MODIFY on entity.move: only to_room/via_exit honoured
  string   text_append = 5;                // for entity.describe
  repeated Mutation on_commit = 6;         // applied atomically with the action if it commits
  repeated Message  messages = 7;          // delivered if the action commits
}

message Request {
  string request_id = 1;                   // idempotency key per connection run
  oneof op {
    GetEntity get_entity = 10;
    QueryEntities query = 11;
    GetAreaGraph area_graph = 12;
    ApplyBatch apply_batch = 13;
    SendMessage send_message = 14;
    ScheduleTimer schedule_timer = 15;
    CancelTimer cancel_timer = 16;
    GetModuleState get_state = 17;
    PutModuleState put_state = 18;
    UpdateSubscriptions update_subs = 19;
    LogLine log = 20;
  }
}
message Response {
  string request_id = 1;
  Status status = 2;
  oneof result { /* matching result messages */ google.protobuf.Struct generic = 20; }
}
message Status { int32 code = 1; string message = 2; uint32 retry_after_ms = 3; } // gRPC-style codes
message Mutation {
  oneof m {
    SetComponent set_component = 1;        // entity_id, name, Struct data, optional expected_version
    PatchComponent patch_component = 2;
    RemoveComponent remove_component = 3;
    SetOverlay set_overlay = 4;            // entity_id, overlay_id, layer, patch, lease_ms
    ClearOverlay clear_overlay = 5;
    MoveEntity move_entity = 6;
    SpawnEntity spawn = 7;
    DespawnEntity despawn = 8;
  }
}
message ApplyBatch { repeated Mutation mutations = 1; repeated Precondition preconditions = 2; bool dry_run = 3; }
message Heartbeat { uint64 n = 1; uint64 last_seq_processed = 2; }
message Stop { uint32 grace_ms = 1; string reason = 2; }
message Goodbye { string reason = 1; }
```

```proto
// ---- mud/core/v1/frontend.proto (SKETCH) ----
service FrontEnd {
  rpc Session(stream FrontEndToCore) returns (stream CoreToFrontEnd);   // text clients
}
message FrontEndToCore {
  oneof body {
    FrontEndHello hello = 10;          // name ("telnet"), token, api_version, capabilities (ansi, echo_control...)
    OpenSession open = 11;             // local_conn_id, remote (opaque string), term type, width, height
    Authenticate auth = 12;            // local_conn_id, name, password   (plaintext for now)
    CreatePlayer create = 13;
    Line line = 14;                    // session_id, text
    CloseSession close = 15;           // session_id, reason (client_closed, idle, flood, ...)
    ClientInfo info = 16;
    Heartbeat heartbeat = 17;
  }
}
message CoreToFrontEnd {
  oneof body {
    FrontEndWelcome welcome = 10;
    SessionOpened opened = 11;         // local_conn_id -> session_id
    AuthResult auth_result = 12;       // ok / bad credentials / retired / throttled / already online (takeover)
    Output output = 13;                // session_id, channel (GAME|SYSTEM|CHAT), text (markup), flush
    SetEcho set_echo = 14;
    SetPrompt set_prompt = 15;
    CloseFromCore close = 16;          // session_id, farewell, flush
    Heartbeat heartbeat = 17;
  }
}

// ---- mud/core/v1/admin.proto (SKETCH) ---- unary, used by the web front end and the core-admin CLI
service Admin {
  rpc Authenticate(AuthRequest) returns (AuthReply);
  rpc ChangePassword(ChangePasswordRequest) returns (ChangePasswordReply);
  rpc GetRoom(GetRoomRequest) returns (RoomDetail);
  rpc ListRooms(ListRoomsRequest) returns (ListRoomsReply);
  rpc CreateRoom(CreateRoomRequest) returns (RoomDetail);
  rpc UpdateRoom(UpdateRoomRequest) returns (UpdateRoomReply);     // allow-listed fields, actor, expected_rev optional
  rpc DeleteRoom(DeleteRoomRequest) returns (DeleteRoomReply);
  rpc GetRoomMap(GetRoomMapRequest) returns (RoomMapReply);
  rpc ListPlayers(ListPlayersRequest) returns (ListPlayersReply);
  rpc UpdatePlayer(UpdatePlayerRequest) returns (UpdatePlayerReply);
  rpc SetRetired(SetRetiredRequest) returns (SetRetiredReply);
  rpc ListAudit(ListAuditRequest) returns (ListAuditReply);
  rpc GetStats(Empty) returns (StatsReply);                         // metrics snapshot
  rpc Flush(Empty) returns (FlushReply);                            // drain persistence for backups
}
```

Mapping between today's web types and these RPCs is intentional: `RoomDetail`, `RoomEdit`, `RoomSaveResult`,
`RoomFieldError`, `AuditEntry` in `MUD.Web/Services/RoomEditing.cs` already model the same request/response shapes.

### 7.3 Socket files, permissions, identity

| Item | Design |
|---|---|
| Directory | `$XDG_RUNTIME_DIR/mud/` = `/run/user/1000/mud/` on this host (`/run/user/1000` exists, mode `drwx------`, verified). Created by core with mode 0700. Falls back to `~/.local/share/mud/run/` when `XDG_RUNTIME_DIR` is unset (e.g. cron) |
| Files | `modules.sock` (ModuleHost service) and `frontends.sock` (FrontEnd + Admin services): two sockets so that a module cannot even reach the front end/admin surface (defence in depth against module bugs; not against a malicious same-user process). One socket is simpler; Q19 |
| Permissions | File mode 0600 inside the 0700 directory. The **directory** mode is the control that is certain; the socket file mode depends on the umask at bind time and Kestrel's behaviour **(unverified)**, so core `chmod`s after bind and treats failure as fatal |
| Stale socket | A socket file survives a crash. On start core takes an exclusive `flock` on `core.lock` in that directory (second core fails fast: duplicate core), then deletes any stale `*.sock`, then binds. Never delete a socket without the lock |
| Module identity | Core spawns the child with `MUD_MODULE_NAME`, `MUD_MODULE_TOKEN` (random 32 bytes per spawn), `MUD_CORE_SOCKET` in its environment and nothing else secret. The `Hello` must carry the token. A module started by hand for development (MODULES 4) uses a dev token minted by `core-admin issue-dev-token <module>` with an expiry, accepted only if the supervisor is not already running that module |
| Front-end identity | A token read from `~/.config/mud/frontend.<name>.token` (mode 600), or minted by the supervisor if core starts the front end too (Q20) |
| Peer check | Optional: compare `SO_PEERCRED` uid with core's uid and pid with the spawned child's pid. **Whether .NET exposes this directly is unverified**; it may need a `getsockopt` P/Invoke. Because every process is the same uid, this only detects accidents |
| Duplicate instance | Second `Hello` for a module name that is already connected: reject with `ALREADY_EXISTS` unless it carries the token of a newer spawn that core itself issued (restart overlap) |

### 7.4 Versioning and compatibility

- Package `mud.core.v1`. **Additive-only** within v1: new optional fields, new oneof members, new events and new
  enum values; never renumber or reuse field numbers (use `reserved`); never change a type.
- Unknown fields are ignored by protobuf, so older modules tolerate newer cores; modules must ignore events/enum values
  they do not know (generic `Event.payload` falls back to `generic`).
- `Hello.api_version`/`Welcome.api_version` negotiate; core supports current and previous major (`v1` and `v2` side by side as
  separate packages, separate services) for a defined window.
- Manifest field `api_version` + `min_core` lets the supervisor refuse to start an incompatible module with a clear log
  rather than a failed handshake.
- Module data schema version is the module's business (stored in its components).
- Contract tests (ARCHITECTURE_V2 7.6) fence this.

### 7.5 Deadlines, backpressure, reconnect

- Unary RPCs (Admin): standard gRPC deadline from the client (default 5 s, 30 s for imports); core cancels work on
  `RpcException`/cancellation when it can, but a request already on the game thread is allowed to finish (no half-applied
  state; the response is simply discarded).
- Streams: deadlines are fields (`deadline_ms` on pre-events; request timeouts are client-side). The stream itself has
  no deadline.
- Backpressure: bounded outbound queue per stream (1000 messages) with the drop policy in section 5; bounded inbound
  queue to the game thread (separate, small).
- Reconnect: module reconnects with exponential backoff (200 ms doubling to 5 s, jitter) only if the *process* is still
  alive and core is not stopping it; core restarts mean the child is killed with core (D12), so a surviving module
  simply fails to connect and exits after N tries (the supervisor spawns it afresh). Front ends reconnect with the same backoff forever, because
  they are long-lived and systemd-managed.
- Core restart while modules are children: children get `Stop` then SIGTERM; core start brings them up again (MODULES 5).

## 8. .NET 8 specifics (what was verified and what was not)

Verified in this task (by fetching public sources on 2026-10-19; **no code was built or run**):

- NuGet flat-container listing shows `Grpc.AspNetCore` 2.84.0 (latest stable seen; 2.83.0 also present), `Grpc.Net.Client`
  2.84.0, `Grpc.Tools` 2.84.0, `Google.Protobuf` 3.36.2 (stable latest seen). The `Grpc.AspNetCore` 2.84.0 nuspec has a `net8.0`
  dependency group (depends on `Grpc.AspNetCore.Server.ClientFactory`, `Google.Protobuf` 3.35.1, `Grpc.Tools` 2.84.0) and the `Grpc.Net.Client`
  2.84.0 nuspec has a `net8.0` group. So both support `net8.0`, which is what every project here targets (`MUD.*.csproj`).
  Pin exact versions in implementation and re-check at that time.
- The Microsoft Learn page "gRPC interprocess communication with Unix domain sockets" (source:
  `dotnet/AspNetCore.Docs`, `aspnetcore/grpc/interprocess-uds.md`) states: requires .NET 5 or later; **server**:
  `serverOptions.ListenUnixSocket(socketPath, listenOptions => listenOptions.Protocols = HttpProtocols.Http2)` in Kestrel
  configuration; **client**: `GrpcChannel` with a `SocketsHttpHandler` whose `ConnectCallback` connects a `Socket`
  (`AddressFamily.Unix`) to a `UnixDomainSocketEndPoint` and returns a `NetworkStream`, with `GrpcChannel.ForAddress("http://localhost", ...)`.
- The SDK installed on this host is 8.0.128 with `Microsoft.AspNetCore.App` 8.0.28 runtime in `/usr/share/dotnet`
  (`dotnet --list-sdks/--list-runtimes`). No gRPC package is in the local NuGet cache (`~/.nuget/packages` had none), so
  first restore needs network access to nuget.org.

Not verified (mark in implementation cards as spikes):

- Unencrypted HTTP/2 ("h2c", no TLS) over UDS needs `Http2` only protocols on the Kestrel endpoint; the doc above shows this,
  but we have not confirmed behaviour with the combination of two endpoints (modules.sock + frontends.sock) in one Kestrel host.
- Socket file mode on bind and automatic deletion of a stale socket file by Kestrel: unverified. Design above does not depend on either.
- `MUD.Main` is a console host, not a web host (`MUD.Main/Program.cs`); adding Kestrel means referencing the ASP.NET Core
  shared framework (`Microsoft.NET.Sdk.Web` or `FrameworkReference Microsoft.AspNetCore.App`). Worth a spike on how that
  interacts with the existing `Assembly.LoadFrom` scan and the DI container in `World.LoadTypes()`.
- Module projects need only `Grpc.Net.Client`, `Google.Protobuf`, `Grpc.Tools` (codegen at build); they do **not**
  need the ASP.NET Core framework. CI runs `dotnet restore/build/test MUD.sln` on ubuntu-latest with .NET 8.0.x
  (`.github/workflows/ci.yml`); `Grpc.Tools` bundles a native `protoc` per OS, so codegen at build time in CI is expected to work but is unverified here.
- Performance numbers (UDS round trip, protobuf cost) are not measured. Phase 4 should include a small benchmark before the
  latency budgets in 2.2 are fixed.
- Non-.NET modules: gRPC has generators for many languages, so the wire contract is language-neutral; whether to support
  that is Q3. The module SDK helpers (MODULES 11) would exist only for C#.
