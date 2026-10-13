# Single Game Thread Loop (design spec)

Status: DRAFT for review (round 2: based on hermes 97304c8). Docs only; no code changes accompany this spec.

## 1. Goals and decisions (fixed by the owner)

1. One game thread executes ALL world mutation: command execution, heartbeats,
   autosave scheduling, room/occupant/inventory changes. Game code takes no locks.
2. Socket callbacks only decode lines and enqueue `(client, line)` on a
   `Channel<T>` / `BlockingCollection<T>`. The game thread blocks on it with a
   timeout equal to the tick interval (no busy spin).
3. `Living.IsBusy` is replaced by a `NextActionAt` timestamp checked each tick.
   There is no scheduled-event queue yet (see Future extensions).
4. Each client has an outbound queue with a single writer, so sends are ordered
   and never concurrent.
5. DB writes never run on the game thread. The game thread snapshots the entity
   (`BsonDocument`) and hands it to a persistence worker.
6. Heartbeat becomes a tick on the game thread. `System.Threading.Timer` goes away.

## 2. Current state (what is being replaced)

Observed in the code on branch `hermes` at 97304c8 (gameloop-spec has hermes merged in; this
spec is the only difference). File references below are to that tree.

- `World.Start()` (MUD.Core/World.cs) creates a `System.Threading.Timer` (2 s) that runs
  `doHeartbeat` on a threadpool thread, then spins `while (_isRunning) _commandQueue.ExecuteAllCommands();`
  with no blocking: a busy loop burning one core. `World.Stop()` calls `_heartbeatTimer.Change`
  and sets a plain bool from another thread. `doHeartbeat` already iterates `Players.ToList()` and
  catches per player, but it still runs on the timer thread.
- `CommandQueue` (MUD.Core/Commands/CommandQueue.cs) keeps a per-`Living`
  `ConcurrentQueue<CommandExecution>`. `ExecuteAllCommands` starts each command with
  `Task.Run`, guarded by `Living.IsBusy` (a plain `bool`, Living.cs). So commands run on arbitrary
  threadpool threads, concurrently across players and with the heartbeat timer. "stop" clears a
  queue; "Command queued." is sent when the issuer is busy.
- `Player.DataReceivedHandler` (MUD.Core/GameObjects/Player.cs) runs on the socket callback thread.
  It calls `ResolveCommand` (reads world/room/player state) and constructs `CommandExecution`,
  which calls `ParseCommand` (also reads state), before enqueueing. `World.AddPlayer` also calls
  `DataReceivedHandler(null, "look")` directly.
- `Client.Send(byte[])` (MUD.Telnet/Client.cs) calls `Socket.BeginSend` directly from whatever thread
  calls it. Concurrent `BeginSend` on one socket can interleave or reorder data. `sendCallback` and
  `Read` failures call `Disconnect()`. `Disconnect()` is now idempotent (`Interlocked.Exchange`)
  and raises `ClientDisconnected` once, but it raises it inline on whichever thread called it.
  `readCallback` -> `ProcessReceivedBytes` raises `DataReceived` inline on the receive thread.
  `Send(string)` now normalises line endings correctly and escapes IAC.
- `Server` (MUD.Telnet/Server.cs) keeps `_clients` under `_clientsLock`. `Stop()` closes the
  listener, then for each client snapshot sends the shutdown text with `Send` and calls
  `Disconnect()` immediately (no flush: the text races the socket close).
  `handleClientDisconnect` removes the client and re-raises `ClientDisconnected`.
- `Player.Save()` calls `_playerRepository.Insert/Update` synchronously on the calling thread
  (command task, heartbeat thread, or disconnect callback thread) and returns a bool.
  `Player.Quit()` guards re-entry with `_isQuitting`, sends "Thanks for playing!", sets
  Offline, calls `Save()`, and only if that succeeds calls `World.RemovePlayer` and
  `Connection.Disconnect()`; on failure it clears `_isQuitting` and leaves the player in
  the world. `ClientDisconnectedHandler` calls `Quit()` on the socket thread.
- `Player.DoHeartbeat()` calls `Save()` when `DateTime.Now.Subtract(_lastSave).TotalMinutes >= 10`.
  `Living.DoHeartbeat` iterates `ExtendedProperties.OfType<IHeartbeat>()`.
- Write-through persistence (specs/RUNTIME_STATE.md): `GetCommand`/`DropCommand` call
  `Player.Save(true)` and `Room.SaveState()` from the command, synchronously. `Room.SaveState`
  calls `IRepository.UpdateFields` (field-level `$set` of `Items`); `Room.Save()` is a whole-document
  upsert used by Build/Terraform. `Repository<T>` guards its cache and every collection call
  with one lock (`_sync`) held across the Mongo round trip. Its own comment says live entities
  are NOT locked and "game logic is expected to mutate them from the command-queue thread",
  which is exactly what this spec turns into a hard single-thread rule.
- `Repository.Update` is an upsert whose success is `MatchedCount == 1 || UpsertedId != null`
  (an unchanged document is a success). The cache is never evicted except on Delete, and
  the cached instance is the live game object (rooms hold occupants).
- `World.Players` is a plain `List<Player>` mutated from login (socket thread via
  `Program.messageReceived` -> `World.DoLogin`/`DoCreateUser` -> `AddPlayer`), quit, and read by
  heartbeat and by the pre-login `who` on socket threads.
- `World.DoLogin`/`DoCreateUser` do `playerRepository.Search` (a DB read) and `Player.Save()` on the
  calling socket thread. `Living.CurrentLocation` lazily calls `World.GetRoom` -> `Repository.Get`
  (DB read on a cache miss) from whichever thread touches it first.
- `SearchCommand.DoCommand` (MUD.Core/Commands/Player/SearchCommand.cs) calls `Thread.Sleep(3000)`,
  which is tolerated today only because every command has its own task. Under a single game thread
  it would freeze the world. It is the only `Sleep` in non-test code. `IsBusy` is only touched in
  `CommandQueue` and declared on `Living`.
- `Program.Main` (MUD.Main/Program.cs) starts `gameWorldThread` (`_gameWorld.Start`), then the server
  thread, then waits for console 'q' and calls `_gameWorld.Stop()` then `_serverInstance.Stop()`.
  `Program.messageReceived` (connect/create/who/quit pre-login handler) runs on socket threads and
  is unsubscribed (`DataReceived -=`) once login succeeds.
- Existing tests that pin current behaviour and will need deliberate, reviewed updates as the
  migration lands (do not edit them just to pass): MUD.Tests/ConcurrencySafetyTests.cs,
  PlayerQuitTests.cs, ServerLifecycleTests.cs, TelnetClientTests.cs, RuntimeStateTests.cs,
  PersistenceFixesTests.cs.

## 3. Thread model

```
 telnet I/O threads (N, from socket callbacks)           game thread (1)
 +---------------------------------+                +-------------------------------+
 | Client.readCallback             |                | loop:                         |
 |  decode telnet / split lines    |  InboundItem   |  item = inbound.TryTake(      |
 |  inbound.Writer.TryWrite(       | -------------> |         timeout=nextTickDue)  |
 |    (client, line))              |  Channel<T>    |  drain inbound (bounded/tick) |
 +---------------------------------+  (MPSC)        |  if now >= nextTickDue: Tick()|
                                                    |  Tick: heartbeats, NextActionAt|
 +---------------------------------+                |    command dispatch, autosave |
 | Client.WriterLoop (1 per client)|  string/bytes  |    scheduling                 |
 |  reads outbound, BeginSend/     | <------------- |  Player.ReceiveMessage ->     |
 |  await SendAsync one at a time  |  Channel<T>    |    client.Enqueue(text)       |
 +---------------------------------+  (per client,  +---------------+---------------+
                                       SPSC)                        | PersistRequest
                                                                    v (Channel<T>, MPSC)
                                                    +-------------------------------+
                                                    | persistence worker (1)        |
                                                    |  repository Insert/Update,    |
                                                    |  retry/backoff, completion    |
                                                    |  callback -> inbound (as      |
                                                    |  a world-event item)          |
                                                    +-------------------------------+
```

Threads: telnet accept/receive threads (many, never touch world state); one game thread;
one outbound writer per client (may be a task, not a dedicated thread); one persistence worker.

Rule: only the game thread reads or writes `World`, `Room`, `Living`, `InventoryItem`,
`Player` state and `World.Players`. Other threads see only immutable messages
(strings, byte[], BsonDocument snapshots).

## 4. Queue types

Inbound (socket -> game): one `Channel<InboundItem>` created with
`Channel.CreateUnbounded` (single reader, multiple writers) — or `BlockingCollection<InboundItem>`
if the project prefers it. All projects on hermes target net8.0 (MUD.*.csproj), which ships System.Threading.Channels;
`BlockingCollection` is equally available.
`InboundItem` is a small record/struct: `{ Client Client; InboundKind Kind; string Line }`
where Kind is `Line`, `Connected`, `Disconnected`, or `WorldEvent` (persistence completion,
admin action, shutdown request). Using one queue for all kinds keeps ordering simple:
a disconnect cannot overtake lines that arrived before it.

Because `Channel` has no synchronous timeout read, the game thread uses
`reader.WaitToReadAsync(cts)` with a `CancelAfter(timeUntilNextTick)` token, blocked via
`.AsTask().Wait()` / `GetAwaiter().GetResult()`, or `BlockingCollection.TryTake(out item, ms)`.
Choose one in implementation; behavior is identical. Open question Q1.

Per-line cap: an inbound line is truncated/rejected above a max length (proposed 1024 chars)
in the I/O layer, and lines are dropped (client told "Too much input") once a client has more than
N unprocessed lines queued (proposed 20, counted via an `Interlocked` per-client counter), to
bound memory from flooding. Open question Q4.

Outbound (game -> client): one `Channel<OutboundItem>` per `Client`, single reader (the writer
loop), written by the game thread (and by the pre-login handler, see Migration). `OutboundItem`
is `string` (already terminal-resolved) or `byte[]` (telnet negotiation). Bounded
(proposed 256 items) with `FullMode = DropWrite` plus a "client too slow" disconnect once the
bound is hit twice in a row, so a stalled client can never grow memory or block the game thread.
Game-thread enqueue is always non-blocking.

Persistence (game -> worker): one `Channel<PersistRequest>` (single reader). Unbounded;
coalescing is described in section 8.

## 5. Tick

Tick interval: 100 ms (10 Hz) proposed, as a named constant `World.TickInterval`.
Rationale: commands are dispatched on arrival (see below) so latency is not tied to the tick,
while `NextActionAt` checks, heartbeats and timeouts want finer than the current 2 s. This
is open question Q2; the design works for any interval.

Heartbeat cadence stays at 2 s for compatibility: `Heartbeat` fires every
`HeartbeatEvery = 20` ticks (or when `now >= nextHeartbeatAt`, advanced by fixed increments
to avoid drift). It iterates `Players` (and later NPCs) on the game thread.

Loop sketch (informational, not final code):

```
var next = clock.Now + TickInterval;
while (running) {
    var wait = Max(0, next - clock.Now);
    if (inbound.TryTake(out item, wait)) {
        Handle(item);
        // drain whatever else is already queued, up to MaxItemsPerPass, then fall through
        while (processed < MaxItemsPerPass && inbound.TryTake(out item, 0)) Handle(item);
    }
    if (clock.Now >= next) { Tick(); next += TickInterval; if (next < clock.Now) next = clock.Now + TickInterval; }
}
```

The `MaxItemsPerPass` cap and the "skip missed ticks, do not catch up" rule prevent a
long burst of input or a slow tick from starving ticks or causing a tick storm.
A tick that takes longer than the interval is logged (warning with duration).

### 5.1 Commands and NextActionAt

- `Living.NextActionAt` (`DateTime`/`long` ticks from the injected clock, `[BsonIgnore]`)
  replaces `IsBusy`. A living may act when `clock.Now >= NextActionAt`.
- Each Living keeps a pending command queue (plain `Queue<CommandExecution>`, game-thread only).
  `Handle(Line)` resolves the command and parses it on the game thread, then:
  - "stop": clear that living's queue (current behavior preserved).
  - else enqueue; if `NextActionAt` is in the future, tell the player "Command queued."
    (current behavior, same message).
- On every tick, and also immediately after enqueue, the dispatcher runs at most
  one command per living whose `NextActionAt <= now`, executes it synchronously,
  and the command (or the dispatcher via a command-declared cost) sets
  `NextActionAt = now + cost`. Default cost 0, so existing commands act immediately.
  Commands that previously relied on "busy for a while" (e.g. search) set a
  cost instead of sleeping.
- Exceptions thrown by a command are caught by the dispatcher and reported with the existing
  `CommandExecution.NotifyException`. One command's exception never stops the loop.
- Which livings are visited: the dispatcher keeps a set of livings with a non-empty queue
  to avoid scanning everyone each tick.

Future extension (not in scope): a scheduled-event queue (priority queue keyed by due time)
would let a command say "run this callback in 3 s" instead of polling `NextActionAt`. The
single-thread, fake-clock design is compatible. Not built now; no placeholders.

## 6. Rules for commands (no blocking)

Commands run on the game thread, so a command that blocks stalls every player. Mandatory:

1. No `Thread.Sleep`, `Task.Wait`, `.Result`, `.GetAwaiter().GetResult()`, or sync-over-async.
2. No direct DB, file, or network access. Existing synchronous calls found:
   `Repository.Get/Search/Update` via `World.GetRoom`, `DoLogin`, `DoCreateUser`, `Living.CurrentLocation`
   (lazy room load), `TerraformCommand`/export/import creator commands (YAML file I/O).
   These need an explicit decision, see section 10 and Q5. Rule going forward: a command
   needing I/O must start async work through a world-provided facade and receive the
   result back as a `WorldEvent` on the inbound queue, executing the continuation on the
   game thread.
3. Delays are expressed with `NextActionAt` (cost), never by waiting.
4. No `Task.Run`/threadpool continuations that touch world state. Any async continuation that must
   touch the world re-enters through the inbound queue.
5. `ReceiveMessage` never blocks: it enqueues to the client's outbound queue.
6. Keep per-command work bounded. Long iterations (area import, mass queries) must be
   chunked across ticks or run off-thread on snapshots.
7. Do not hold references to world objects in off-thread code. Hand off immutable data only.

`SearchCommand` currently does `Thread.Sleep(3000)`. Under this model that call must be
changed (message now, result after a delay) once a delayed action mechanism exists. With
only `NextActionAt` and no event queue, the interim option is: send "You begin your
search.", set `NextActionAt = now + 3s`, and perform the search as the next queued
pseudo-command. This is flagged as part of migration step 6 and open question Q6.

## 7. Outbound path

- `Client.Send(string)` and `Send(byte[])` become `Enqueue` onto the client's outbound
  channel. They never touch the socket directly. A single writer loop per client reads the
  channel and performs `SendAsync`/`BeginSend` sequentially, awaiting each completion
  before the next. Ordering is the queue order. There is never more than one send in flight.
- Line-ending normalization and IAC escaping (already correct in `Send(string)` on hermes) are done in
  `Enqueue`, unchanged, so the writer loop only deals in final bytes.
- Write failure: the writer logs, marks the client dead, completes the channel and
  requests disconnect through the inbound queue as a `Disconnected` item, not by raising events from the
  writer thread.
- `Disconnect()` is already idempotent (`Interlocked.Exchange`, raises `ClientDisconnected` once); keep
  that. What changes is that `ClientDisconnected` no longer runs `Player.Quit` on the I/O thread: the
  I/O layer posts a `Disconnected` item and the game thread performs the logout (section 8.4).
  `Server.handleClientDisconnect` (removal from `_clients`) stays on the I/O side.
- Graceful close: `Disconnect(flush: true)` completes the outbound channel for writing, lets
  the writer drain remaining items (bounded by a timeout, proposed 2 s), then shuts down the socket.
  This is how "Thanks for playing!" and the `Server.Stop` shutdown text reliably reach the client
  before close (today both race `Disconnect()`).
- Only `Client`s and the writer loop live in MUD.Telnet; the telnet project must not
  reference MUD.Core. Terminal-handler resolution (`ResolveOutput`) stays in `Player.ReceiveMessage`
  on the game thread, producing the final string before enqueue.

## 8. Persistence

### 8.1 Snapshot and hand-off

The game thread builds a `BsonDocument` for the entity (serialize the Player via
`ToBsonDocument()` or the class map; includes inventory) — a CPU-only, in-memory operation —
and posts `PersistRequest { string Collection; string Id; BsonDocument Doc; long Version; Reason; bool IsInsert }`.
The worker performs only `ReplaceOne/InsertOne` (upsert by `_id`) against Mongo.
`Repository<T>` is thread-safe (one `_sync` lock around cache and collection) and its cache holds the
live entity, so the worker must not call `Repository.Update/Insert` with a live entity (that would
serialise the live object off the game thread). It needs a snapshot-based write method on the
repository (for example `ReplaceDocument(id, BsonDocument)`, plus the existing `UpdateFields(id, BsonDocument)`
which already takes a BsonDocument) that does not touch the cache. The cache entry is created when the
game thread creates or loads the entity, as today. The worker never touches live entities.

Snapshot cost: serializing a Player on the game thread is the accepted price for no locks;
it should be measured (Q3) and, if heavy, snapshot incrementally.

### 8.2 Autosave

Scheduling lives on the game thread: each player has `NextAutosaveAt` (replaces
`_lastSave` + `DateTime.Now.Subtract(_lastSave).TotalMinutes >= 10`, now using the injected clock). The heartbeat tick checks it and
posts a snapshot. Interval stays 10 minutes (config value). `Save` no longer prints
"Saving..." synchronously about completion; the message is sent when the request is enqueued
(Q8 on whether to also confirm on completion).

### 8.2a Write-through mutations (specs/RUNTIME_STATE.md)

RUNTIME_STATE.md sets a policy of write-through: get/drop save immediately, via `Player.Save(true)`
(whole-document replace) and `Room.SaveState()` (field-level `$set` of `Items`). Those calls are made
synchronously from commands today, which breaks the "no DB writes on the game thread" decision.
Proposed mapping, preserving the policy's intent (every mutation is handed to persistence immediately, no
debounce, nothing held back for a timer):

- `Player.Save(quiet)` and `Room.SaveState()` become "snapshot and post" calls. The post happens in the
  same command, in the same order as today (get: player then room; drop: room then player). Because the
  single worker is FIFO, the persisted order is the same, so the "crash can duplicate but never destroy"
  property is kept. It does NOT hold if the process dies with requests still queued (a short window, as with
  any async write). This weakens RUNTIME_STATE.md section 1 ("nothing is lost if the process is killed")
  and needs owner sign-off (Q16).
- `PersistRequest` needs two kinds: `Replace` (whole document, upsert) and `SetFields` (field-level `$set`;
  falls back to `Replace` on the worker if `MatchedCount == 0`, as `Room.SaveState` falls back to `Save`
  today, which needs a snapshot of the whole entity captured at post time). Coalescing (8.3) applies per
  `(collection, id, kind)`; a `Replace` supersedes earlier queued `SetFields` for the same id, but a
  `SetFields` never supersedes a queued `Replace` (it is applied after it).
- `Room.Save()` used by Build/Terraform (definition edits) is a `Replace` request. Importer
  `UpsertFields` runs from the creator import command and from startup `World.ImportContent`
  (before the game thread exists); the in-game path is a blocking-I/O command (rule 6.2, Q5).
- Callers that use the bool result of `Save()` (`World.DoCreateUser`, `TerraformCommand`,
  `Player.Quit`, `PromoteCommand` via `recipient.Save()`) can no longer branch on the DB outcome
  synchronously. The migration must decide per call site (Q12, Q16).

### 8.3 Coalescing, ordering, failure and retry

- Per-entity ordering: the worker processes requests in FIFO order, and a single worker
  guarantees writes for the same `_id` are applied in snapshot order.
- Coalescing: if a request for entity X is queued and a newer one arrives before the older
  one starts, the worker may skip the older one (keep latest per `(collection, id)`), except for
  requests flagged `Final` (logout), which are never dropped and always applied last.
- Failure: a failed write (exception, or not acknowledged / no match and no upsert) is retried by the worker
  with exponential backoff (proposed 1 s, 2 s, 4 s ... capped at 30 s) up to a max attempts or max age
  (proposed: retry indefinitely for `Final` requests while the process runs; non-final
  requests are superseded by the next snapshot anyway, so after 5 attempts they are
  dropped with an error log). While retrying entity X, later snapshots of X replace the pending
  one (latest wins).
- Failure surfacing: after the first failure for an entity the worker posts a `WorldEvent`
  (`PersistFailed(id, attempt)`) to the game thread, which may warn the player/admins once.
  On success after failures, a `PersistRecovered` event is posted.
- Success criterion: acknowledged and (`MatchedCount == 1 || UpsertedId != null`), as in
  `Repository.Update` on hermes (an unchanged document counts as success). A raw `ReplaceOne` in the
  worker must use the same rule.
- Process exit: pending requests are flushed during shutdown (section 9). If the DB is down
  at exit, unsaved snapshots are written to a local spool file (proposed
  `<data dir>/unsaved/<collection>-<id>.json`, extended JSON) and replayed at next start (Q10).

### 8.4 Logout ordering

For a player quit or disconnect, on the game thread, in this order:

1. Mark `ConnectionStatus = Offline`; stop accepting commands from the client (drop further
   inbound lines for it).
2. Send farewell text to the outbound queue ("Thanks for playing!").
3. Remove from room and `World.Players` (`RemovePlayer`: announcements to others).
4. Take the final snapshot (after the room removal, so the saved state is the logged-out state;
   `CurrentLocationId` is retained) and post a `Final` persist request.
5. Request a flushing disconnect on the client.
6. Do NOT allow the same player name to log in again until the `Final` write has completed
   (worker posts `PersistCompleted(id)` event; the game thread clears the "saving" flag).
   A login attempt while a final save is pending is told "Your previous session is still being saved,
   try again in a moment." — otherwise the new session could load stale data from DB/cache
   and later overwrite newer data. (If the repository cache keeps the live object, the cache
   must be updated or invalidated consistently; Q11.)

Note: today `Quit()` (guarded by `_isQuitting`) only removes the player and disconnects if `Save()`
succeeded; otherwise it leaves the player in the world, clears `_isQuitting` and allows another quit attempt. In the new model disconnect never waits on the DB; the
fate of a failed final save is handled by retry plus the spool file. Behavior change; Q12.

Net-dead handling (keeping the player in the world after a lost connection) is not part
of this spec; current behavior (quit on disconnect) is preserved.

## 9. Startup, shutdown and drain

Startup order: create queues -> start persistence worker -> start game thread
(`World.Start`) -> start telnet server. (Today, the world thread starts before
the server; keep that.) Nothing accepts input until the game thread is running.

Shutdown (triggered by console 'q', SIGTERM or an admin command; all just post a
`Shutdown` item to the inbound queue, or set a flag the loop checks each pass):

1. Telnet server stops accepting new connections (`Server.Stop` minus the client loop).
2. Game thread: stop reading new `Line` items (still handle `Disconnected`); broadcast
   "Server is shutting down" to players.
3. For each player in `Players`: run the logout sequence from 8.4 (final snapshot posted,
   `Final`). Do this on the game thread in one pass (bounded by number of players).
4. Mark inbound complete; game thread exits its loop (after finishing the current tick).
5. Persistence worker: `Complete()` the persist channel and drain: apply all remaining requests
   with retries up to a shutdown deadline (proposed 30 s, configurable). On deadline, spool
   the remaining snapshots to disk (8.3) and log a loud error.
6. Each client's outbound channel is flushed (bounded wait, 2 s) and sockets are closed.
7. `Program.Main` joins game thread, then persistence worker, then exits.

`Program.Main` currently calls `_gameWorld.Stop()` then `_serverInstance.Stop()`, where `World.Stop()`
changes the timer and sets a bool from another thread, and `Server.Stop()` closes the listener then does
`Send` + `Disconnect()` per client inline, with no flush. `Disconnect()` raises `ClientDisconnected`, which
runs `Player.Quit` (and its synchronous `Save`) on the stopping thread, concurrent with the game thread. These become requests that return after the sequence above completes (join with timeout).
A second shutdown request while draining is ignored.

Unhandled exceptions on the game thread: caught at the top of each item/tick, logged,
loop continues. A failure of the loop machinery itself (not a command) triggers the
shutdown sequence so players get saved rather than the process dying with unsaved state (Q13).

## 10. Migration steps (from current code)

Each step should compile and pass tests on its own; steps 1–2 can land first because they
are additive.

1. Introduce an injectable `IClock` (`Now`) and `GameLoop` class (MUD.Core) containing the inbound
   queue, tick scheduling and `RunOnce(timeout)`/`Tick()` entry points (testable, no thread
   inside). `World.Start` runs `GameLoop.Run` on the existing thread created in `Program.Main`.
   Remove the busy `while` in `World.Start`.
2. Replace the heartbeat Timer: delete `_heartbeatTimer` (World.cs, `Start`/`Stop`), call
   `doHeartbeat` from the tick every `HeartbeatEvery` ticks (keep its per-player try/catch and
   `ToList()` snapshot). `World.Stop` posts the shutdown item.
3. Replace `CommandQueue`: delete `Task.Run` and the concurrent dictionary; move per-living queue into
   `Living` (game-thread only); `IsBusy` -> `NextActionAt`; keep `StopCommand` semantics and the "Command queued."
   message. Keep the class name `CommandQueue` if less churn is wanted (it can still be DI-registered at
   `World.LoadTypes`), but its API becomes game-thread-only; document that in the class.
4. `Player.DataReceivedHandler`: reduce to a thin enqueue. The handler's body (ResolveCommand,
   `new CommandExecution` which calls `ParseCommand`, "Command not recognized.") moves to a game-thread
   `HandleLine(Player, string)` invoked by the loop. Lookup from `Client` to `Player` goes through a
   game-thread-owned `Dictionary<Client, Player>` populated at login, so socket callbacks do not need
   `Player` references or event subscriptions. `Player.Connection` setter's `+=`/`-=` of events is removed.
5. Client send path (MUD.Telnet): add per-client outbound `Channel<OutboundItem>` plus writer loop;
   `Send(...)` overloads enqueue; make `Disconnect` idempotent with flushing option; `DataReceived`
   and `ClientDisconnected` become writes onto the inbound queue (via an interface/delegate set by the host so MUD.Telnet
   stays free of MUD.Core references). `Server.Stop`/`SendMessageToAll` use the same enqueue.
6. Pre-login flow: `Program.messageReceived` (connect/create/who/quit) runs on socket threads
   and reads `World.Players`, and `DoLogin`/`DoCreateUser` mutate world state and hit the DB. Move it
   behind the same inbound queue as a "login state" handled on the game thread; the DB lookups
   (`playerRepository.Search`) become a persistence-worker read with the result returned as a
   `WorldEvent`. (Q5). Intermediate acceptable form: keep the lookup synchronous on the game thread
   at first, flagged as a known violation of rule 6.2, and file a follow-up.
7. `Player.Save/Quit`: `Save()` becomes "build snapshot and post to persistence worker" and returns void
   (or a Task-less handle); `Quit()` implements 8.4. `ClientDisconnectedHandler` is removed
   (replaced by `Disconnected` inbound items). Add `NextAutosaveAt`, drop `_lastSave` and its `TotalMinutes` check. Keep the intent of the
   `_isQuitting` guard (logout runs once per session) as a game-thread flag. Re-point the get/drop/terraform/
   promote/create call sites per 8.2a.
8. Add the persistence worker (MUD.Data or MUD.Core) with retry/spool, and shutdown drain (section 9).
   Wire `Program.Main` shutdown.
9. Convert `SearchCommand` (remove `Thread.Sleep`) and audit all commands for blocking
   (grep for `Sleep`, `.Wait(`, `.Result`, repository calls, file I/O). Creator import/export/terraform
   commands: either accept bounded sync file I/O initially (flag it) or move to the worker.
10. Remove now-dead code (`CommandQueue` dictionary, `IsBusy`) and thread-safety patches that
    become unnecessary. `Repository` locking stays: the worker thread and the game thread both use it.
    Update the Repository.cs comment that says live entities are mutated "from the command-queue thread".

## 11. Test strategy

- Deterministic loop: `GameLoop` takes `IClock` and exposes `RunOnce(TimeSpan timeout)` and
  `Tick()`. Tests never start a thread and never sleep. `FakeClock.Advance(TimeSpan)` moves time;
  a test enqueues inbound items by calling `Post(item)` then `RunOnce(0)`.
- Fake clients: `Client` is a concrete class around a `Socket`; extract a minimal
  `IClientConnection` (`Enqueue(string)`, `Disconnect(bool)`) in MUD.Core for tests, with a
  `FakeConnection` that records output. (Extraction is part of step 5.)
- Fake persistence: `IPersistenceSink.Enqueue(PersistRequest)` with an in-memory implementation
  that records requests and can be told to fail N times; the real worker has its own tests with a
  fake `IWriter` (fail/succeed/delay) and the fake clock for backoff.
- Test cases:
  - ordering: lines from one client execute in arrival order; lines from different clients interleave
    by arrival; a disconnect after N lines runs after those N lines.
  - `NextActionAt`: command with cost 3 s delays the following command until the clock reaches it;
    "Command queued." message; `stop` clears the queue.
  - heartbeat cadence: after 20 ticks (2 s) exactly one heartbeat; no catch-up burst after a long gap.
  - autosave: scheduled after interval; snapshot posted, not DB call; no repository access on game thread
    (the fake repository asserts thread identity != game thread).
  - logout ordering (8.4): farewell enqueued before disconnect; Final request is last for that id;
    login blocked while Final pending; allowed after `PersistCompleted`.
  - failure/retry: N failures then success; backoff delays on fake clock; coalescing keeps latest;
    Final never dropped; spool on shutdown deadline.
  - outbound: many `Enqueue` from the game thread produce an ordered byte stream on a fake socket
    stream with a single in-flight write (fake stream asserts no concurrent calls); slow-client
    bound triggers disconnect.
  - shutdown: sequence 9 with fake time: all players final-saved, loop exits, nothing left in queues.
  - exception isolation: a throwing command reports to the issuer and later commands still run.
- Thread guard: a debug-only `GameThread.AssertOnGameThread()` used inside world mutators
  (`Room.EnterRoom`, `Inventory` changes) so integration tests catch off-thread mutation.
- Existing tests are preserved. Mongo is not available in the CI/sandbox, so real
  persistence paths are covered by fake writers only; a manual Mongo smoke test is needed before merge of
  the implementation cards.

## 12. Risks

- Slow command stalls everyone. Mitigation: rule 6, tick-duration warning log, optional watchdog.
- Snapshot serialization on the game thread may be expensive for large inventories (Q3).
- Behavior change in logout: connection loss no longer depends on the DB write succeeding.
  Mitigation: retry plus spool, and the re-login guard (8.4).
- Login while final save pending, plus the repository's model cache (never evicted, holds the live
  object): `Search` maps results onto the cached instance, so a re-login receives the old live
  `Player` object, not a fresh load. Needs a decision on cache ownership (Q11).
- Existing-code issues still present on hermes that interact with this work (document only; none fixed
  here): `SearchCommand` `Thread.Sleep(3000)`; `World.Players` is an unsynchronised `List<Player>` read
  by pre-login `who` on socket threads; `Living.CurrentLocation` lazy DB read from arbitrary threads;
  `Server.Stop` has no flush. Problems reported in an earlier draft (IHeartbeat filter, `Minutes` vs
  `TotalMinutes`, discarded `Replace`, `ModifiedCount` success rule, repository thread safety, bare
  `connect` `Substring`) are fixed on hermes and not repeated here.
- Write-through weakening: moving get/drop saves off-thread (8.2a) means a kill -9 can lose the last
  few queued writes that RUNTIME_STATE.md currently guarantees are durable. Needs owner sign-off (Q16).
- Large mechanical diff in Client and Program touches shared hot files (Client.cs, Player.cs, World.cs);
  steps should land as small PRs in order to avoid conflicts with sibling branches.
- Unbounded inbound queue plus flooding clients: mitigated by per-client caps (Q4).
- Single core ceiling for all game logic. Accepted by the owner; revisit if tick time grows.
- Subtle: events (`ClientDisconnected`) currently fire on I/O threads while holding user code; moving to
  queue-based delivery changes ordering guarantees for anything else subscribed (Server events used in `Program.Main`).

## 13. Open questions

Q1. `Channel<T>` vs `BlockingCollection<T>` for inbound. Both are available on net8.0. Proposal: `BlockingCollection` for the simplest timeout read, unless Channels are already in use.
Q2. Tick interval: 100 ms proposed; heartbeat stays 2 s. Confirm or give another value.
Q3. Acceptable snapshot cost on the game thread; do we need incremental/dirty-flag snapshots?
Q4. Inbound flood limits (line length 1024, 20 queued lines per client): confirm values and the penalty.
Q5. Login/creation and other DB reads (room lazy load via `World.GetRoom`, creator import/export): do they
    go through the persistence worker asynchronously now, or is synchronous read on the game thread acceptable
    as an interim step? The card says DB *writes* never run on the game thread; reads are not specified.
Q6. Interim handling of `SearchCommand` (3 s delay) before a scheduled-event queue exists.
Q7. (Withdrawn: line-ending normalisation is already fixed on hermes.)
Q8. Should "Saving..." be followed by a completion/failure message to the player?
Q9. (Withdrawn: `Repository.Update` already treats an unchanged document as success.)
Q10. Local spool file on shutdown with DB unavailable: acceptable, and where does it live?
Q11. Who owns the repository model cache (live entities, never evicted, `Search` maps onto them)? Should the
    worker bypass it entirely (proposed, 8.1) and should a Player be evicted after its Final write completes
    so a re-login reloads it?
Q12. Accept the logout behavior change (disconnect no longer waits on a successful save)?
Q13. On a fatal game-loop error: attempt orderly shutdown/save (proposed) or crash fast?
Q14. Is net-dead/linkdead handling (player stays in world after disconnect) wanted soon? It affects 8.4.
Q15. Single persistence worker thread, or one per collection if throughput demands it?
Q16. RUNTIME_STATE.md says every mutation is saved immediately and nothing is lost if the process is killed.
    Moving those saves to the worker (8.2a) weakens that to "queued, flushed on clean shutdown". Accept, or keep a
    narrow synchronous exception for get/drop?
