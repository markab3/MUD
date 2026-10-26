# Core API: events, behaviors, calls, gRPC over Unix domain sockets

Part of [ARCHITECTURE_V2.md](ARCHITECTURE_V2.md). Status: REVISED to owner decisions D1..D39
([OPEN_QUESTIONS_V2.md](OPEN_QUESTIONS_V2.md) is now the decisions record). Docs only: the message text below is a sketch inside
markdown; no `.proto` file, project or package reference is added to the repo. Numbers shown are unmeasured proposals unless
the decisions fix them; items the owner has not decided are marked **(not set)**. Items an implementer would have to guess are
listed in section 9 as G1..G31.

Contents: 1 Threading rule, 2 Event model, 3 What modules may call, 4 State: components, module-state document,
self-timed effects, 5 Limits and quotas, 6 From a raw line to an executed command (the front-end API), 7 gRPC design and
message sketches, 8 .NET 8 specifics, 9 Gaps found while revising.

## 1. The one rule: every request becomes game-thread work, and the game thread never waits for a process

GAME_LOOP.md fixes one game thread that executes all world mutation and takes no locks (D9 keeps this). V2 adds producers of
work (gRPC handlers, module calls) but keeps the consumer:

```
 gRPC handler thread (Kestrel pool)                 game thread (1)
 ---------------------------------                  -------------------------------------------
 1 decode protobuf
 2 authenticate the call (module token / session token)
 3 check capability (table lookup)
 4 check quota (token bucket, per module)
 5 check size/shape limits
 6 enqueue an inbound item (with a                  7 re-check preconditions against the live world
   TaskCompletionSource when a reply is due) --->   8 process events, queue persistence snapshots
 9 await completion (with deadline)           <---  10 complete the TCS with the response
11 write the response
```

Steps 1-5 touch only immutable config and per-module counters. Steps 7-8 are the only ones that touch `World`. The existing
mechanism is `GameLoop.Post` with `InboundKind.WorldEvent` and `Action` (`MUD.Core/GameLoop.cs`); V2 adds typed item kinds so the
loop can account for them per module (design statement, not code).

Fairness: `GameLoop.MaxItemsPerPass = 100` is shared today by players' lines and persistence completions. Module calls go on a
**separate bounded queue** with a smaller per-pass share (**not set**), so a chatty module cannot starve player input. This is an
instance of the per-tick event budget required by D9.

The game thread never blocks on a module: it puts the delivery on that module's outbound queue (non-blocking, bounded) and
continues. When an answer is needed (Before chain), the *acting entity* is parked (2.3), not the loop.

## 2. Event model

### 2.1 Concepts

- **Event**: a generic record with a string **name** (`entity.move`), a **phase** (`Before` or `After`; a field, not a separate type;
  D4), a core-assigned `event_id`, a `trace_id`, a `cause_id` (the event whose processing produced this one; empty for
  player/timer-originated events), subject ids, and a payload.
- **Parties**: the entities involved. A multi-party action lets the living, the item and the room/container each carry behavior
  (D3). For `entity.move` the parties are the mover, the room it leaves and the room it enters.
- **BehaviorRef**: an entry in an entity's behavior list: `Hook`, `Module`, `Type`, `Priority`, `Config`, `SchemaVersion` (D5);
  persistence shape is G2.
- **Handler**: module (or, in phase 3, in-process) code for a `(Module, Type)`. Receives an immutable snapshot and returns a verdict (D6).
- **Verdict**: `ALLOW` or `DENY(message)`, each with optional **emitted events**. No MODIFY (D6).
- **Before** phase: runs before the action commits; the action waits for the chain. **After** phase: notification once the
  action committed; the verdict is ignored except for its emitted events.

### 2.2 How an event finds its handlers (no subscription filters)

There are no module subscription filters and no priority-ordered chain (D5, D6, D7). Routing is by the entities themselves:

```
 action (move, drop, get, typed line, heartbeat)  -> core creates Before event E, hook H, parties P1..Pn
 for each party Pi:   entries = Pi.Behaviors.Where(b => b.Hook == H)            // entity filters its own list
 for each entry:      handler  = Resolve(entry.Module, entry.Type)              // dictionary lookup at call time
                      if handler is null  -> dormant: apply the hook's fail policy, count, log once per interval (D10)
                      else                -> send a delivery (event_id, snapshots of the parties, entry.Config) to the module
 no entries anywhere -> fast path: commit immediately (D9)
```

Hot swap edits the dictionary only. Entities are never scanned or rewritten (D5). A module declares in its manifest the
`(Type, hooks, schema versions)` it can handle (MODULES.md 1); that is checked at connect, it is not a routing filter.
Events that belong to no entity (`timer.fired`, `module.ready`) are sent directly to the module they concern (G18).

### 2.3 The Before chain: parking the acting entity instead of blocking the thread (D15)

State machine (game thread owns it; verdicts arrive as inbound items):

```
 player command "north"
   v
 MoveCommand creates Before entity.move(E1) with parties mover, from-room, to-room
   |  deliveries = behavior entries matching the "living enters/exits room" hook on the three parties
   |  none -> commit now (fast path)
   v
 mover.Parked = true      (further input from the mover is queued by CommandQueue; other entities unaffected)
 send one delivery per entry to its module (outbound queues, non-blocking); one chain deadline = now + chain budget
   |   game thread carries on                       module -> SubmitVerdict(event_id, ALLOW|DENY, message, emitted[])
   v
 inbound item VerdictArrived(event_id, ...) per delivery, possibly in any order, possibly in parallel
   +-- records the verdict; its emitted events are queued for processing (D8: they apply even if the action is denied)
   +-- waits until EVERY handler replied or the SINGLE chain deadline passes; no short-circuit on the first Deny
   +-- a missing reply at the deadline -> the hook's fail policy (2.6)
   v
 chain done:  any Deny?  yes -> abort; show the first Deny message to arrive (D7); mover un-parked
              no       -> RE-VALIDATE (mover still online and in from-room, destination exists, exit present) -> commit
   v
 commit: mutate world, queue snapshots, run the After phase: notifications to the mover and both rooms
         (events emitted by After handlers are processed as new events)
 mover.Parked = false, drain queued commands
```

Cost: between emit and commit other commands ran, so the world may have changed. Hence the mandatory re-validation. This is the
main complexity of the design and the price of keeping the loop responsive.

`Living.NextActionAt` plus `CommandQueue` already express "this living may not act yet" (`MUD.Core/Commands/CommandQueue.cs`);
parking reuses that idea with an unknown end time. What a parked entity may do or suffer is G6.

Latency: measured numbers do not exist. The draft's proposal was 50 ms per handler, 150 ms per chain, at most 3 remote handlers;
D15 records these as unmeasured proposals pending the spike (V2_PHASES.md "pre-phase-4 spike"). Because the chain waits for all
handlers (D15), chain latency is the slowest handler, not the sum.

### 2.4 Event catalogue

The five initial hooks (D3) are the only points where a behavior list is consulted at first. Events are generic string-named
records (D4). "Named" means a decision text uses that name; "proposed" means this document proposes it (see G20).

| Event name | Hook (D3) | Parties | Payload | Origin |
|---|---|---|---|---|
| `entity.move` | living enters/exits room | mover, from-room (role `from`), to-room (role `to`) | from, to, via_exit, reason (walk, trans, spawn, login, trap, ...) | named (D4). One event covers exit and enter; separate After notifications to mover and both rooms |
| `item.move` | item dropped; item taken | item, source holder, destination holder, room | from, to, reason (walk/drop/get/give/spawn), actor | named (D4) |
| `command.entered` | command entered | actor, actor's room (see G7) | raw line, verb, args | hook named (D3); event name proposed |
| `heartbeat` | heartbeat | the entity itself | tick number | hook named (D3); recipients G8 |
| `component.set`, `component.remove` | hookable, no D3 hook yet (G18) | target entity | component name, data | `component.set` named (D9); remove proposed |
| `behavior.add`, `behavior.remove` | hookable, no D3 hook yet (G18) | target entity | BehaviorRef | `behavior.add` named (D9); remove proposed |
| `timer.schedule`, `timer.cancel` | none | issuing module | timer id, at/delay, payload | `timer.schedule` named (D11); cancel proposed |
| `timer.fired` | none | issuing module | timer id, payload | proposed (G16) |
| `state.set` | none | issuing module | key, document | named (D12) |
| `message.send` | none | target entity / room | text (markup), channel | proposed: how a module speaks to players, using existing channels (D28) |
| `module.ready`, `module.stopping` | none | module | name | proposed (lifecycle, D21) |
| `world.shutdown` | none | all modules | deadline | proposed (D29) |

"Every event is hookable" (D9) is a requirement on the machinery; which hook names the change events use is G18. Not offered: raw
output interception (privacy, flood) and a login pre-event (login rules stay in core).

### 2.5 Verdicts and emitted events

```
Verdict { event_id, decision: ALLOW | DENY, message (for DENY), emitted: [Event] }
```

- `ALLOW` and `DENY` only. Any Deny vetoes (D6). A handler that wants to redirect returns `DENY(message)` and emits a new
  `entity.move{entity: mover, from, to: <elsewhere>, reason}`; that event runs its own chain.
- Emitted events apply whether or not the action is denied (D8): there is no transactional discard. Anything that must depend on
  the action committing is emitted from the After phase instead.
- Cause chain: every emitted event carries the `cause_id` of the delivery that produced it; the re-entrancy guard (2.8) reads it.
- One `SubmitVerdict` carries its whole emitted batch and is applied atomically on the game thread (assumed in D16; G5 asks what
  happens when one emitted event is rejected by a limit or capability).
- A late verdict (after the chain deadline, or for an expired `event_id`) is matched by `event_id`, dropped and counted.

### 2.6 Fail policy per hook (D10, D15)

An unresolved behavior (module not running, or an entry's SchemaVersion not readable) and a handler that misses the deadline are
treated the same way: by the **hook's fail policy**. Flavor hooks fail open (treated as ALLOW with no events); rule hooks fail
closed (treated as DENY with a core message). A per-behavior override is bounded by a core policy file. Which hooks are flavor
and which rule is G3. The skip is counted per `(Module, Type, hook)` and logged once per interval (interval **not set**).

### 2.7 Ordering and delivery

- Handler order within a Before chain is not significant (D7): handlers may run in any order or in parallel; if several Deny, the
  player sees the first Deny message to arrive. `Priority` on a BehaviorRef is stored (D5) but carries no ordering guarantee. A
  module must not depend on running before or after another module's handler for the same event.
- After notifications go to the parties independently; per module, delivery order is the order of `seq` on the stream.
- Delivery to a module is **at most once per connection**: a module that is disconnected misses events; its behaviors are dormant
  (D10) and on return it reconciles from its state document and entity components (D13). Before events are never replayed.

### 2.8 Limits that every-event-is-hookable requires (D9)

| Limit | Purpose | Value |
|---|---|---|
| Cascade depth | stop an emitted-event chain that never ends | **not set** |
| Per-tick event budget | keep the tick inside its 100 ms budget; excess events carry to the next tick | **not set** |
| Per-module emission quota | a module cannot flood events (token bucket on `SubmitVerdict`/`Emit`) | **not set** |
| Re-entrancy guard | reject an event whose cause chain already contains the same behavior, entity and event name | rule fixed by D9 |
| Fast path | an entity with no matching behaviors costs nothing | rule fixed by D9 |
| Module-state document size and write rate | protect the database | **not set** (D12 says a limit exists) |

Rejected events are counted and logged with module, event name, targets, `trace_id`, `cause_id` (D34's record).

## 3. What modules may call (unary, module -> core)

All calls are unary RPCs on `modules.sock`, plus one server stream the module opens to receive deliveries (7.1). Every call passes
the authentication and capability checks of section 1.

| Call | Purpose | Notes |
|---|---|---|
| `Hello` | First call: module name, per-spawn token (D17), manifest, contract version number (D24) | Core refuses a version mismatch (strict equality) with a log line naming both versions; refuses an unknown or already connected name |
| `Subscribe` | Opens the server stream of deliveries | Not a filter: the module receives what its behaviors and its own timers produce |
| `Ready` | The module has finished reconciling | Releases the front-end gate if the launch entry says `required` (D21) |
| `Ping` | Hung-module detection (D16) | Separate from the stream so a blocked handler is distinguishable from a dead process |
| `SubmitVerdict` | Answers a Before delivery: `event_id`, `ALLOW`/`DENY`, message, emitted events | Matched by `event_id`; late ones dropped (D16) |
| `Emit` | Emit events outside a verdict (After-phase work, timer-driven work): `component.set`, `message.send`, `entity.move`, ... | Capability-checked per event name and target scope (G20) |
| `SetState` / `GetState` | Module-state document (D12): key -> document; read on start | `SetState` is the wire form of a `state.set` event (G1) |
| `ScheduleTimer` / `CancelTimer` | Core calls back later with `timer.fired` | Wire form of `timer.schedule` (D11); minimum interval and count **not set** |
| `Log` | Structured line into core's log with module and trace id | Rate limit **not set** |

Reads of world state beyond the snapshot a delivery carries (`GetEntity`, area queries) are **not decided** (G11): D16 lists
"verdicts, state, timers, emit" only. The worked examples therefore use only what deliveries carry plus the module's own state
document, and name where a read would help.

### 3.1 Capabilities (D18, D19)

- Capabilities are strings with optional scopes, for example `emit:component.set:ns=desert`, `emit:message.send:scope=room`,
  `emit:entity.move:kind=player`, `timer.schedule`, `behavior:DesertSandstorm:heartbeat`. Names are illustrative; the vocabulary is G20.
- **Requested** in the manifest, which ships with the module and is sent to core in `Hello` (not compiled into core). **Granted** by
  core from `~/.config/mud/module-grants.yaml` (mode 600, read by core only), reloadable by an admin command without restarting core.
- **Partial grant:** the module starts with the granted subset and a logged warning; a manifest capability flagged `required: true`
  makes core refuse the start when missing.
- Update flow: build, `modules reload <name>`, core diffs requested vs granted, the owner edits the grants file and reloads.
  Optional helpers: `modules diff`, `modules grant`.
- These checks are a seatbelt against bugs, not a defense against a hostile same-user process (D19).

## 4. State: components, the module-state document, and self-timed temporary effects

### 4.1 Where state lives (D12, D13)

| Kind | Home | Writer | Example |
|---|---|---|---|
| Affects core behavior, is player-visible, or another behavior must read it from a snapshot | Component on the entity, `<module>.<name>` | module via `component.set` / `component.remove` events | `desert.storm` on a room, `desert.sand_blind` on a player, `temple.trap` on a room |
| Module-private scheduling detail | Module-state document, key -> document, owned by the module, persisted by core | module via `state.set`; read with `GetState` | the storm schedule, next-spawn time, trap-group membership |

Both are persisted by core and kept when the module unloads (purge policy is a deferred item). Startup reconciliation by the module
must be idempotent (D13). Mongo shape of component data and of the state document: G13, G15.

### 4.2 Temporary effects time themselves out (D11)

There is **no overlay, no undo structure, no TTL field, no lease** in core (the draft's CORE_API section 4 is dropped). The pattern:

1. The module emits `component.set` to apply the effect and writes its own `state.set` record (what it applied, when it ends).
2. The module emits `timer.schedule{id, at}` for the end of the effect.
3. When `timer.fired` arrives the module removes the effect with `component.remove`, guarded to be **idempotent** (remove only if
   the component is present and still carries this effect's id).
4. On start the module reads its state document, compares with entity components (G11 bears on how), and for each recorded effect
   either reschedules the timer (still live) or removes the component (expired): the idempotent reconciliation of D13.

Core does not know which changes are temporary. Consequence, plainly: if the module crashes and never returns, its components
stay on the entities until the module returns or someone removes them by hand (creator command or web editor). A dormant
behavior removes nothing (D10). Whether timers survive a core restart, and who recreates them, is G16.

## 5. Limits and quotas

All numeric limits are **not set** (OPEN_QUESTIONS_V2.md "Still open or deferred": latency numbers; quotas, cascade depth and per-tick
budgets; start-up timeout; crash-loop policy). The mechanisms that need a number are: chain deadline and per-handler deadline (2.3),
cascade depth, per-tick event budget, per-module emission quota and state-document size and write rate (2.8), outstanding unanswered
calls, active timers and minimum timer period, outbound queue length per module, start-up timeout and backoff/crash-loop thresholds
(MODULES 3). The draft's numbers were proposals only and have been removed here; the spike (V2_PHASES.md) supplies latency measurements.

Persistent abuse by a module (sustained rejections) flips it to a `quarantined` supervisor state (stream closed, not restarted
until an admin clears it); the threshold is not set.

## 6. From a raw line to an executed command, and the front-end API

### 6.1 The front-end-neutral core API (introduced in phase 1)

A front end speaks to core only in terms of **session ids** and **lines**. The surface (a C# interface in phases 1 to 3; the same
shape as gRPC messages from phases 4/6; the interface is specified in V2_PHASES.md phase 1):

| Direction | Operation | Fields |
|---|---|---|
| front end -> core | open a session | front end id, remote key (opaque string, the throttle key), terminal hints (width, height, supports_echo_control). Returns `session_id` |
| front end -> core | authenticate / create | `session_id`, name, password (plaintext, never logged). Returns: ok, bad credentials, retired, throttled, already online (takeover), name taken, try again later |
| front end -> core | line | `session_id`, raw line (already stripped of telnet control codes and control characters by the front end). Output is asynchronous |
| front end -> core | close | `session_id`, reason (client_closed, idle, flood, ...) |
| core -> front end (session events) | output | `session_id`, `channel` (say, tell, room, combat, system), `text` (`%^Color%^` markup, D28), `flush` |
| core -> front end | set echo | `session_id`, hidden yes/no (password prompts) |
| core -> front end | prompt | `session_id`, text without line ending |
| core -> front end | close | `session_id`, farewell text, flush |
| core -> front end | session ended | `session_id`, reason (quit, taken over, shutdown "world is restarting", D29) |

Web-only operations (accounts, rooms, players, audit, map) are on the Admin service (7.2) and are phase 6. They take the opaque
**session token** core issued at web authentication (D31); core re-checks the current role on every call and can revoke the session.

Where the brute-force login throttle (today `MUD.Telnet/LoginThrottle.cs`, keyed by address) and the prompted-login dialogue (today
`MUD.Telnet/LoginSession.cs`) live is G24; the phase 1 cards implement the recommendation.

### 6.2 Raw line to command

```
 telnet: "get sword"                                  core
   |  line(session_id=17, "get sword")  ---------->  | enqueue inbound item; per-session pending cap as World.EnqueueLine does today
   |                                                  | game thread:
   |                                                  |  1 session 17 -> Player P
   |                                                  |  2 if P.HasPrompt: send the line to the prompt handler (passwords) and return
   |                                                  |  3 create Before command.entered with parties P and P's room (G7); run the chain
   |                                                  |  4 no Deny -> ResolveCommand as today (room exits -> known -> role -> default), CommandQueue.AddCommand
   |                                                  |  5 DoCommand mutates the world through events; persistence queued
   |                                                  |  6 output to P -> output(session_id=17, channel, "You get a sword.")
   | <--- output event ---------------------------   |
   | convert %^Token%^ to ANSI for this connection's terminal choice, write to the socket
```

Rules:

- The front end sends **lines**, never parsed commands; core is the only place that knows verbs.
- `Redact` for `ISensitiveInputCommand` (today `Player.DataReceivedHandler`) stays in core. Neither core nor a front end logs a line
  received while an echo-off prompt is active.
- The `stop` command clears the actor's queue and cancels a parked action unless it is already committing (exact rule: G6).
- Output is **markup plus channel** (D28). The front end never parses game meaning.

## 7. gRPC design

### 7.1 Module shape (D16): server stream for events, unary calls back

```
 module                                      core (modules.sock)
   | -- Hello(token, manifest, contract v) -->  authenticate, strict-equal contract check (D24), grants -> reply
   | -- Subscribe() ------------------------>  server stream stays open for the module's lifetime
   | <== CoreEvent (delivery) ===============  Before delivery, After notification, timer.fired, world.shutdown
   | -- SubmitVerdict(event_id, ...) -------->  unary; late ones dropped
   | -- Emit / SetState / ScheduleTimer ----->  unary
   | -- Ready() ---------------------------->  gate for required modules (D21)
   | -- Ping() ----------------------------->  hung-module detection, separate from the stream
```

The module dials core, so a module needs only a gRPC **client** (no server, no second socket per module). The draft's single
bidirectional stream per module is **dropped** (D16): replies are unary calls with their own deadlines and status codes and no
hand-written request correlation, at the cost of two things to keep alive (the stream and the unary channel), which `Ping` and the
stream's end both signal.

Pitfalls to design around (standard gRPC behavior; .NET specifics in section 8):
- One writer at a time per server stream: all deliveries go through one outbound `Channel<T>` per stream drained by a single task
  (the pattern of `MUD.Telnet/OutboundQueue.cs`).
- Slow reader: the bounded outbound queue turns HTTP/2 backpressure into a policy instead of unbounded memory. The policy (for
  example drop After notifications, treat Before deliveries as missed) is part of the unset limits in section 5.
- A verdict's order relative to later events is not tied together; handlers must not rely on it.

### 7.2 Services and messages (sketch)

All text below is design sketch; field numbers are illustrative and must be reserved and never reused once real. The `.proto` files
live in `contracts/` with a small `MUD.Contracts` project (Grpc.Tools codegen) referenced by core, front ends and C# modules (D23).
Strict version equality (D24): every first call carries `contract_version`. No C#-only types on the wire (D25). No automated
contract tests until the contract settles after phases 3-5 (D26).

```proto
// ---- contracts/mud/module.proto (SKETCH) ---- served on modules.sock
service ModuleHost {
  rpc Hello(HelloRequest)           returns (HelloReply);
  rpc Subscribe(SubscribeRequest)   returns (stream CoreEvent);     // server stream (D16)
  rpc Ready(ReadyRequest)           returns (Ack);
  rpc Ping(PingRequest)             returns (PingReply);
  rpc SubmitVerdict(VerdictRequest) returns (Ack);
  rpc Emit(EmitRequest)             returns (EmitReply);
  rpc SetState(SetStateRequest)     returns (Ack);                  // wire form of state.set (G1)
  rpc GetState(GetStateRequest)     returns (GetStateReply);
  rpc ScheduleTimer(TimerRequest)   returns (Ack);                  // wire form of timer.schedule
  rpc CancelTimer(CancelTimerRequest) returns (Ack);
  rpc Log(LogRequest)               returns (Ack);
}

message HelloRequest  { string module = 1; string token = 2; uint32 contract_version = 3; Manifest manifest = 4; string module_version = 5; }
message HelloReply    { bool ok = 1; string connection_id = 2; repeated string granted = 3; repeated string rejected = 4; string refusal = 5; }
message Manifest      { string name = 1; repeated BehaviorDecl behaviors = 2; repeated string capabilities = 3; repeated string required_capabilities = 4; }
message BehaviorDecl  { string type = 1; repeated string hooks = 2; repeated uint32 schema_versions = 3; }
message SubscribeRequest { string connection_id = 1; }
message ReadyRequest  { string connection_id = 1; }
message PingRequest   { string connection_id = 1; uint64 n = 2; }  message PingReply { uint64 n = 1; }
message Ack           { bool ok = 1; string error = 2; }

message CoreEvent {                         // one delivery
  uint64 seq = 1;                           // per connection, gap-free while connected
  string event_id = 2;                      // the key SubmitVerdict refers to (one per handler call; G4)
  string name = 3;                          // "entity.move", "command.entered", "timer.fired", ...
  Phase  phase = 4;                         // BEFORE | AFTER
  string hook = 5;
  uint32 deadline_ms = 6;                   // relative to send time; 0 for AFTER
  string trace_id = 7; string cause_id = 8;
  BehaviorRef behavior = 9;                 // the entry being invoked: type, config, schema_version (absent for timer.fired)
  repeated PartySnapshot parties = 10;      // immutable value snapshots (G4)
  google.protobuf.Struct payload = 11;      // generic by design (G12)
}
enum Phase { PHASE_UNSPECIFIED = 0; BEFORE = 1; AFTER = 2; }
message BehaviorRef   { string hook = 1; string module = 2; string type = 3; int32 priority = 4; google.protobuf.Struct config = 5; uint32 schema_version = 6; }
message PartySnapshot { string role = 1; string entity_id = 2; string kind = 3; google.protobuf.Struct components = 4; }

message VerdictRequest { string connection_id = 1; string event_id = 2; Decision decision = 3; string message = 4; repeated EventRecord emitted = 5; }
enum Decision { DECISION_UNSPECIFIED = 0; ALLOW = 1; DENY = 2; }       // no MODIFY (D6)
message EmitRequest    { string connection_id = 1; string cause_id = 2; repeated EventRecord events = 3; }
message EmitReply      { repeated string rejected = 1; }
message EventRecord    { string name = 1; Phase phase = 2; repeated string targets = 3; google.protobuf.Struct payload = 4; }
message SetStateRequest { string connection_id = 1; string key = 2; google.protobuf.Struct doc = 3; }
message GetStateRequest { string connection_id = 1; string key = 2; }  message GetStateReply { bool found = 1; google.protobuf.Struct doc = 2; }
message TimerRequest   { string connection_id = 1; string timer_id = 2; int64 at_unix_ms = 3; google.protobuf.Struct payload = 4; }
message CancelTimerRequest { string connection_id = 1; string timer_id = 2; }
message LogRequest     { string connection_id = 1; string level = 2; string text = 3; string trace_id = 4; }
```

```proto
// ---- contracts/mud/frontend.proto (SKETCH) ---- served on frontends.sock, phases 6/7
// The front-end transport shape is not decided by the owner (G21); sketched with the module pattern:
// unary calls in, a server stream of session events out.
service Session {
  rpc Connect(FrontEndHello)  returns (FrontEndWelcome);          // token, contract_version, name ("telnet")
  rpc Events(SessionEventsRequest) returns (stream SessionEvent); // output, set_echo, prompt, close, session_ended (6.1)
  rpc OpenSession(OpenSessionRequest) returns (OpenSessionReply); // -> session_id
  rpc Authenticate(AuthRequest) returns (AuthReply);
  rpc CreatePlayer(CreatePlayerRequest) returns (AuthReply);
  rpc Line(LineRequest)       returns (Ack);                      // session_id, raw line
  rpc CloseSession(CloseSessionRequest) returns (Ack);
}
message SessionEvent { string session_id = 1; oneof body { Output output = 10; SetEcho set_echo = 11; Prompt prompt = 12; CloseFromCore close = 13; SessionEnded ended = 14; } }
message Output { string channel = 1; string text = 2; bool flush = 3; }   // channel: say|tell|room|combat|system (D28)

// ---- admin.proto (SKETCH) ---- unary, used by the web front end (phase 6); every call carries the session token (D31)
service Admin {
  rpc WebAuthenticate(WebAuthRequest) returns (WebAuthReply);    // returns the opaque session token
  rpc Logout(TokenRequest) returns (Ack);
  // GetRoom / ListRooms / CreateRoom / UpdateRoom / DeleteRoom / GetRoomMap  (semantics as WEB_EDITING 15, no expected_rev: D30)
  // ListPlayers / GetPlayer / UpdatePlayer / SetRetired / ChangePassword / ListAudit
  // GetStats / Flush
}
```

Mapping between today's web types and these RPCs is intentional: `RoomDetail`, `RoomEdit`, `RoomSaveResult`, `RoomFieldError`,
`AuditEntry` in `MUD.Web/Services/RoomEditing.cs` already model the same request/response shapes (verified in the draft task; not re-checked).

### 7.3 Socket files, permissions, identity

| Item | Design |
|---|---|
| Sockets | **Two** UDS (D22): `modules.sock` (module services only) and `frontends.sock` (session/admin services for telnet and web). Capability checks still apply |
| Directory | `$XDG_RUNTIME_DIR/mud/` = `/run/user/1000/mud/` on this host (`/run/user/1000` exists, mode `drwx------`, verified in the draft task). Created by core with mode 0700. Fallback when `XDG_RUNTIME_DIR` is unset: `~/.local/share/mud/run/` |
| Permissions | Mode 0600 inside the 0700 directory. The directory mode is the certain control; the socket file mode depends on the umask at bind time and Kestrel **(unverified)**, so core `chmod`s after bind and treats failure as fatal |
| Stale socket | Core takes an exclusive `flock` on `core.lock` in that directory (second core fails fast), then deletes stale `*.sock`, then binds. Never delete a socket without the lock |
| Module identity | Core spawns the child with `MUD_MODULE_NAME`, `MUD_MODULE_TOKEN` (random per spawn) and `MUD_CORE_SOCKET` in its environment and nothing else secret (D17). The first call (`Hello`) carries the token. A dev tool issues a token for a module started by hand (MODULES 4). No `SO_PEERCRED` |
| Duplicate instance | A second `Hello` for a connected module name is refused unless it carries the token of a newer spawn core itself issued (restart overlap) |
| Front-end identity | Telnet and web present a token read from a mode-600 file under `~/.config/mud/` (G21); in phases 1 to 3 the telnet project is in-process and needs none |

### 7.4 Versioning (D24)

Strict equality. Every module (and front end) sends a contract version number in its first call; core refuses any mismatch with a
clear error logging both versions. The field exists from day one so additive-only or a range can replace strict equality later.
Contract changes are expected to be infrequent (generic string events, data-driven behaviors), with churn mostly in phases 3-5.
Module data schema versions are the module's business: they travel in `BehaviorRef.SchemaVersion` and inside component data (D10).

### 7.5 Deadlines, backpressure, reconnect

- Unary calls: standard gRPC deadlines from the client (values **not set**). A request already on the game thread is allowed to
  finish; the response is then discarded.
- The stream has no deadline; `deadline_ms` on Before deliveries is a field. A verdict arriving after it is dropped (2.5).
- Modules exit after a grace period if the core connection is lost (D20). Core restart: the child is killed with core; a surviving
  module fails to connect and exits. Front ends close connections with "world is restarting" and clients reconnect (D29).

## 8. .NET 8 specifics (what was verified and what was not)

Verified in the draft task (public sources fetched 2026-10-19; **no code was built or run**), kept unchanged:

- NuGet flat-container listing showed `Grpc.AspNetCore` 2.84.0 (latest stable seen; 2.83.0 also present), `Grpc.Net.Client` 2.84.0,
  `Grpc.Tools` 2.84.0, `Google.Protobuf` 3.36.2. The `Grpc.AspNetCore` 2.84.0 nuspec has a `net8.0` dependency group and
  `Grpc.Net.Client` 2.84.0 has a `net8.0` group. Every project here targets `net8.0`. Pin exact versions in implementation and re-check then.
- Microsoft Learn "gRPC interprocess communication with Unix domain sockets": requires .NET 5+; server:
  `serverOptions.ListenUnixSocket(socketPath, o => o.Protocols = HttpProtocols.Http2)`; client: `GrpcChannel` with a `SocketsHttpHandler`
  whose `ConnectCallback` connects a `Socket` (`AddressFamily.Unix`) to a `UnixDomainSocketEndPoint`, with `GrpcChannel.ForAddress("http://localhost", ...)`.
- SDK on this host: 8.0.128 (re-confirmed in this revision with `dotnet --version`); `Microsoft.AspNetCore.App` runtime 8.0.28 in `/usr/share/dotnet`
  (draft task). No gRPC package was in the local NuGet cache then, so the first restore needs network access.

Not verified (the **pre-phase-4 spike**, D38, V2_PHASES.md, proves these and records measurements):

- UDS round-trip latency and the cost of a verdict round trip.
- `Grpc.Tools` codegen in CI. CI is `.github/workflows/ci.yml` (`actions/setup-dotnet@v4` 8.0.x, `dotnet restore/build MUD.sln`, tests
  guarded by a search for `Microsoft.NET.Test.Sdk`). `Grpc.Tools` bundles a native `protoc` per OS; expected to work, unverified.
- **Kestrel on two UDS endpoints** (modules.sock + frontends.sock) in one host with h2c (Http2 only).
- **Adding ASP.NET Core to the console host `MUD.Main`** (`MUD.Main/MUD.Main.csproj` is `Microsoft.NET.Sdk`; `MUD.Web` is `Microsoft.NET.Sdk.Web`):
  a `FrameworkReference Microsoft.AspNetCore.App` and how it interacts with the `Assembly.LoadFrom` scan and Scrutor DI in `World.LoadTypes()`.
- Socket file mode on bind and stale-file deletion by Kestrel (the design depends on neither).
- Performance numbers (protobuf cost, queue behavior).

Module projects need only `Grpc.Net.Client`, `Google.Protobuf`, `Grpc.Tools` and `MUD.Contracts`; they do not need the ASP.NET Core
framework. The wire contract is language-neutral (D25); supported modules are C#; there is no conformance suite.

## 9. Gaps found while revising

Each item is a point where the decisions leave an obvious implementation choice and this revision did **not** choose. Options and a
recommendation are given so the owner can decide; none is binding. (The recommendations are what the sketches above assume.)

**G1. How a module's `ctx` API maps to events.**
Options: (A) one `ctx.Emit(EventRecord)` and every effect is an event; (B) typed helpers (`ctx.SetComponent`, `ctx.Message`,
`ctx.ScheduleTimer`, `ctx.SetState`) that build events; (C) separate RPCs for state and timers as D16 lists them, generic `Emit` for the rest.
Recommendation: C on the wire (matches D16), B in the C# SDK so authors never hand-build event records. The sketch in 7.2 shows C.

**G2. Exact `BehaviorRef` persistence shape.**
Options: (A) embedded array on the entity document `behaviors: [{hook, module, type, priority, config, schemaVersion}]`; (B) a separate
`behavior` collection keyed by entity id; (C) under `components.behaviors`.
Recommendation: A. Entities already carry their own list (D5) and the entity document is the persisted unit (write-through snapshots).
Also unset: whether `Config` is a BSON document or a JSON string, and its size cap; recommend BSON document with a cap.

**G3. Which of the five initial hooks are flavor (fail open) and which rule (fail closed).**
Options: all open; `command entered` and `item dropped/taken` closed, the rest open; per behavior only via the policy file.
Recommendation: default all five to open (a dead module never strands players); the core policy file marks specific `(Module, Type)` closed. The owner should say which behaviors are rules.

**G4. Snapshot content and delivery identity.**
What a `PartySnapshot` contains: (A) core components + the module's own namespace; (B) everything (privacy and size cost);
(C) core components only (needs G11 for more). Delivery id: one `event_id` per handler call plus a `root_event_id`, or one per event.
Recommendation: A, one `event_id` per handler call.

**G5. Semantics of an emitted batch when one event is rejected.**
D16 assumes a `SubmitVerdict` batch is atomic on the game thread; what if one emitted event violates a capability, a quota or the
re-entrancy guard? Options: reject the whole batch; apply the valid ones and report the rest; all-or-nothing for the emitted events
but the verdict still counts.
Recommendation: the verdict (ALLOW/DENY) always counts; emitted events apply all-or-nothing; rejection is reported in `Ack` and logged.

**G6. Parked-entity corner cases, simultaneous actions, and the stale-snapshot race.**
(a) May a parked entity be targeted by another chain, be killed, quit, or be moved by a third party? (b) Two players enter the trapped
room together: both snapshots say `armed`, both handlers Deny and redirect; D7/D8 give no transaction. (c) `stop` while parked.
Options for (b): module-side serialisation (one module process keeps an in-memory per-room flag); a precondition field on
`component.set`; accept both falling in.
Recommendation: module-side serialisation for the examples (no contract field); revisit a precondition field after phase 5. For (a)/(c):
a parked entity queues new commands, still receives output, and `stop` cancels the wait unless commit has begun.

**G7. How a behavior sees a typed command, and what "handled" means.**
`command entered` is a hook (D3) but the parties are not specified, and the verdict has only ALLOW/DENY. Parties: (A) actor + actor's room;
(B) A + items resolved from the line by `MatchObjects`; (C) A + everything in the room and inventory. "Handled": (i) the behavior
returns `DENY(message)` meaning "consumed, show this", doing its work with emitted events; (ii) a new verdict flag (changes D6).
Recommendation: A and (i) for the first cut (the room-level lever of D14 needs only the room); flagged for the owner because it overloads Deny. The immovable-item lever needs B.

**G8. Heartbeat recipients and cost.**
Today only players receive heartbeats (`World.doHeartbeat`, `MUD.Core/World.cs:633`). D3 lists heartbeat as a hook. Which entities get it?
Options: all; only entities carrying a `heartbeat` behavior entry; only those in rooms with players.
Recommendation: only entities carrying a `heartbeat` entry, found via an index core maintains; interval stays 20 ticks (2 s). Rooms and NPCs thereby gain heartbeats for the first time (behavior change to note).

**G9. How module-set state reaches `look` text.**
`look` is core (D2). Options: (A) modules only `message.send`; `look` unchanged; (B) a core convention, e.g. components named `<ns>.description_append` that `look` appends;
(C) a Before-style describe event with an IPC hop on `look`.
Recommendation: A for phase 5 (the examples use it); B if the owner wants storm text in `look`; not C (latency on the most common command).

**G10. The SchemaVersion contract.**
D10: an unreadable SchemaVersion is refused and treated as a miss. Who declares readable versions, and when is it checked?
Recommendation: declared in the manifest (`BehaviorDecl.schema_versions`), checked at call time against the entry.

**G11. Whether modules can read the world beyond a delivery's snapshot.**
D16 lists verdicts, state, timers and emit; reads are not listed. Without reads a module cannot, for example, learn which rooms exist or
reconcile components after a restart. Options: (A) no reads; (B) add `GetEntity` / `QueryEntities` / `GetAreaGraph`.
Recommendation: A for the first cut (the examples are written to work with A and say where B would help); reopen after phase 5.

**G12. Typed versus generic payloads.**
Events are generic string-named records (D4); the sketch uses `google.protobuf.Struct` (numbers become doubles). Options: Struct everywhere;
typed `oneof` for well-known events plus Struct; JSON string. Recommendation: Struct now, a typed payload for `entity.move`/`item.move` later if needed.

**G13. Who may write which component; naming; validation; Mongo shape.**
Proposed: a module writes only `<its name>.*` components; no module writes a core component in v1; an optional JSON Schema per component
in the manifest that core validates if present; shape `components.<module>.<name>`. None is decided.
Recommendation: namespace rule yes; schema validation deferred; shape as stated.

**G14. Features embedded in the room or separate entities** (draft Q8; no decision answers it). D14 keeps features inert, so this does not block phases 1-5.
Recommendation: leave embedded until a feature needs behaviors.

**G15. Module-state document API shape.**
Whole-document `SetState(key, doc)` as sketched, versus patch operations, versus versioned compare-and-set.
Recommendation: whole-document replace per key, last write wins, with a size limit (unset); a module serialises itself (G6).

**G16. Timers: persistence and routing.**
Do `timer.schedule` timers survive a core restart? Recommendation: no; core timers are in memory, scoped to the module connection, and the module
recreates them on start from its state document and components (the D13 reconciliation). `timer.fired` goes directly to the issuing module (G18).

**G17. Startup import versus D39.**
`ImportOnStartup` defaults true (`MUD.Core/WorldSettings.cs:15`; `MUD.Main/appsettings.json:10`) and `World.ImportContent` (`MUD.Core/World.cs:614`) imports on
every start; ROOM_FILES section 6 re-import semantics are obsolete and D39 allows no recurring sync. Options: default the setting to false in phase 2 and keep `import` as a manual
command; or remove startup import. The open item "import collision semantics and whether validate/plan/apply is kept" stays open.
Recommendation: flip the default to false in phase 2, leave the importer code and semantics alone.

**G18. Events that belong to no entity, and hooks for change events.**
`timer.fired`, `module.ready`, `world.shutdown` have no party. `component.set` / `behavior.add` are hookable (D9) but are not among the five D3 hooks.
Options: direct delivery for non-entity events; hook names for change events (e.g. `component.changed`); leave change events unhookable until a use appears.
Recommendation: direct delivery for non-entity events; hook names for change events deferred, but build the machinery (cause chain, guards) in phase 3 so adding them is cheap.

**G19. Authorisation of behavior attachment.**
Who may add a `BehaviorRef` naming module M to an entity: builders, the web editor, module events? The creator/web editing surface is an open item.
Recommendation: a module-emitted `behavior.add` may name only the emitting module.

**G20. Capability vocabulary and the event names marked "proposed".**
Recommendation: capability = `emit:<event name>[:scope]` for events, plus `timer.schedule`, state access, and per-behavior-type declarations; the grants file lists them verbatim.

**G21. Front-end transport shape and token.**
D22 fixes two sockets and D27 says front ends use gRPC, but not whether the session transport is a bidirectional stream (reversed for modules by D16) or the module-style stream plus unary.
Recommendation: the module style (7.2), one mental model; the token is a mode-600 file read, as modules get theirs.

**G22. In-process delivery of session output (phases 1 to 3).**
Core calls the telnet project on the game thread; the telnet project must only enqueue to the connection's `OutboundQueue` and return.
Recommendation: document the output method "must not block" and test it with a deliberately slow fake front end.

**G23. Start gate numbers.**
`Ready` is a separate call per D21; the start-up timeout length is unset (open item); the systemd unit needs a restart limit (D21). Nothing else to decide but the numbers.

**G24. Where prompted login and the brute-force throttle live after the split.**
Today the dialogue is `MUD.Telnet/LoginSession.cs` (prompted connect/create plus legacy one-line forms) with `MUD.Telnet/LoginThrottle.cs` keyed by remote address;
`PasswordChangeFlow` in `MUD.Core/Commands/Player/ChpwdCommand.cs` shares that throttle.
Options: (A) dialogue stays in the telnet project, throttle moves into core so web and telnet share it; (B) both into core with a prompt/echo protocol; (C) both stay in telnet (web gets its own).
Recommendation: A. The phase 1 cards implement A.

**G25. Canonical string names for the five initial hooks.**
D3 names the hooks in prose (heartbeat, command entered, living enters/exits room, item dropped, item taken) and D4 names two events; no hook identifier strings are
given. The examples use `heartbeat`, `command.entered`, `living.move`, `item.drop`, `item.take` as hook ids (the same string is the `Hook` field of a `BehaviorRef`). Whether
one hook (`living.move`) covers both exit and enter (with the entry's role in the event telling which), or two hooks exist, is also open.
Recommendation: these five strings, one `living.move` hook covering both directions (matching D4's single `entity.move`), the entry reads its own role from the delivery.

**G26. Launch-entry file: path, format, reload.**
D18 says launch entries are "a separate core config, re-readable live" but not where or in what format. Options: a section in `appsettings.json` (the file `MUD.Main` already reads, `MUD.Main/appsettings.json`); or `~/.config/mud/modules.yaml` next to the grants file.
Recommendation: `~/.config/mud/modules.yaml`, because it holds executable paths and must be live-reloadable and kept out of the repo (secrets rule); `appsettings.json` is not live-reloaded by `MUD.Main` today (unverified).

**G27. Stale-module-process check at core start.**
D20 requires it; the mechanism is not given. Options: a pid file per module under the run directory, verified against `/proc/<pid>/cmdline`; or scan the cgroup; or rely on `KillMode=control-group` and only handle non-systemd runs.
Recommendation: pid files under the run directory, checked and terminated at start; document that a dev module started by hand is not covered.

**G28. Dev token form and what `modules reload <name>` does.**
D17 requires a dev tool to issue a token; D18 defines `modules reload <name>` as the update step. Open: is the dev tool an in-game admin command, and does `reload` restart a running module process or only re-read the manifest/config on the next connect?
Recommendation: an in-game admin command `modules token <name>` printing a one-time token valid while no supervised instance runs (not a separate CLI, D32); `reload` re-reads launch entries and grants and restarts the named module only if the owner passes it explicitly.

**G29. Channel tag for `chat` and for untagged messages.**
D28 lists the channels say, tell, room, combat, system. `Living.ReceiveMessage(string)` carries no channel today and the `chat` command has no matching channel name. Options: map chat to `say`; add `chat` to the list (changes D28's list); tag chat `system`. All other existing untagged messages default to `system`.
Recommendation: untagged = `system`; say/tell/`TellRoom` = `say`/`tell`/`room`; ask the owner whether `chat` gets its own tag (an addition to the D28 list, so the owner's call).

**G30. How the web reads roles between phase 2 and phase 6.**
The website derives roles from the `_t` discriminators in `ExtendedProperties` (`MUD.Web/Services/MongoAccountStore.cs:59-60`, `MongoPlayerEditStore.cs:64`). Phase 2 replaces `ExtendedProperties` with a `Roles` component, and D1 forbids migration code and dual-read, but the web keeps reading Mongo directly until phase 6.
Options: (A) the phase 2 card that introduces `Roles` also changes the two web stores to read the new field (small, same card); (B) core writes a mirror `ExtendedProperties` document until phase 6 (a dual-write, against the spirit of D1); (C) reorder so web precedes phase 2 (contradicts D37).
Recommendation: A; D1 means no old data exists, so only the web read path changes.

**G31. Where the session registry and `session_id` format live.**
Phase 1 needs a `session_id`. Options: opaque string generated by core on `OpenSession` (random, unguessable, not reused); or caller-chosen id from the front end (as `local_conn_id` in the draft). Recommendation: core-generated opaque string (works unchanged over gRPC in phase 7, and the web has its own session token, D31).
