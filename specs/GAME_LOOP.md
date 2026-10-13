# Single Game Thread Loop (design spec)

Status: DRAFT for review. Docs only; no code changes accompany this spec.

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

Observed in the code on branch `gameloop-spec` (cf8b7df):

- `World.Start()` (MUD.Core/World.cs) creates a `System.Threading.Timer` (2 s) that runs
  `doHeartbeat` on a threadpool thread, then spins `while (_isRunning) _commandQueue.ExecuteAllCommands();`
  with no blocking: a busy loop burning one core.
- `CommandQueue` (MUD.Core/Commands/CommandQueue.cs) keeps a per-`Living`
  `ConcurrentQueue<CommandExecution>`. `ExecuteAllCommands` starts each command with
  `Task.Run`, guarded by `IsBusy`. So commands run on arbitrary threadpool threads, concurrently
  across players, and concurrently with the heartbeat timer. `IsBusy` is a plain
  bool, not a memory-safe handshake. "stop" clears a queue.
- `Player.DataReceivedHandler` runs on the socket callback thread. It calls
  `ResolveCommand` (reads world/room/player state) and constructs `CommandExecution`,
  which calls `ParseCommand` (also reads state), before enqueueing.
- `Client.Send` calls `Socket.BeginSend` directly from whatever thread calls it.
  Concurrent `BeginSend` on one socket can interleave or reorder data. `sendCallback` can
  call `Disconnect()`, which raises `ClientDisconnected` on the I/O thread.
  `Client.readCallback` raises `DataReceived` inline on the receive thread.
- `Player.Save()` calls `_playerRepository.Insert/Update` synchronously on the calling
  thread (command thread, heartbeat thread, or disconnect callback thread). `Player.Quit()`
  saves, then `World.RemovePlayer`, then `Connection.Disconnect()`.
  `ClientDisconnectedHandler` calls `Quit()` directly on the socket thread.
- `Player.DoHeartbeat()` triggers a `Save()` every 10 minutes using `DateTime.Now`.
  (Note: `TimeSpan.Minutes` is the minutes component, not total; see Risks.)
- `World.Players` is a plain `List<Player>` mutated from login (socket thread via
  `Program.messageReceived` -> `DoLogin`/`DoCreateUser` -> `AddPlayer`), quit, and read by
  heartbeat and `who`. `Program.messageReceived` (pre-login handler) also reads it on socket threads.
- `SearchCommand` calls `Thread.Sleep(3000)`, which is currently "tolerated" because every
  command has its own task. Under a single game thread it would freeze the world.
  `Living.IsBusy` is only touched in `CommandQueue`.

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
if the project prefers it. Projects on this branch target netcoreapp3.1 (a `dotnet8` branch exists), which
ships System.Threading.Channels; `BlockingCollection` is equally available.
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
- Line-ending normalization is done in `Enqueue` (note: the current `Send(string)` computes
  `data.Replace(...)` and discards the result, so normalization currently does nothing; fixing that is a
  behavior change and is flagged, not folded in. Q7).
- Write failure: the writer logs, marks the client dead, completes the channel and
  requests disconnect through the inbound queue as a `Disconnected` item, not by raising events from the
  writer thread.
- `Disconnect()` becomes idempotent (guarded with `Interlocked.Exchange`) and its
  `ClientDisconnected` event is not raised on the game thread's stack; the I/O layer posts
  a `Disconnected` item and the game thread performs the logout (section 8.3).
- Graceful close: `Disconnect(flush: true)` completes the outbound channel for writing, lets
  the writer drain remaining items (bounded by a timeout, proposed 2 s), then shuts down the socket.
  This is how "Thanks for playing!" reliably reaches the client before close.
- Only `Client`s and the writer loop live in MUD.Telnet; the telnet project must not
  reference MUD.Core. Terminal-handler resolution (`ResolveOutput`) stays in `Player.ReceiveMessage`
  on the game thread, producing the final string before enqueue.

## 8. Persistence

### 8.1 Snapshot and hand-off

The game thread builds a `BsonDocument` for the entity (serialize the Player via
`ToBsonDocument()` or the class map; includes inventory) — a CPU-only, in-memory operation —
and posts `PersistRequest { string Collection; string Id; BsonDocument Doc; long Version; Reason; bool IsInsert }`.
The worker performs only `ReplaceOne/InsertOne` (upsert by `_id`) against Mongo.
Cache updates in the repository (currently `Repository._modelCache`, a non-thread-safe
`Dictionary`; note branch `runtimestate` makes Repository thread-safe) are not the worker's
job: the game thread already holds the live object and the cache is updated on the
game thread when an entity is created/loaded. The worker never touches live entities.

Snapshot cost: serializing a Player on the game thread is the accepted price for no locks;
it should be measured (Q3) and, if heavy, snapshot incrementally.

### 8.2 Autosave

Scheduling lives on the game thread: each player has `NextAutosaveAt` (replaces
`_lastSave` + `DateTime.Now.Subtract(...).Minutes >= 10`). The heartbeat tick checks it and
posts a snapshot. Interval stays 10 minutes (config value). `Save` no longer prints
"Saving..." synchronously about completion; the message is sent when the request is enqueued
(Q8 on whether to also confirm on completion).

### 8.3 Coalescing, ordering, failure and retry

- Per-entity ordering: the worker processes requests in FIFO order, and a single worker
  guarantees writes for the same `_id` are applied in snapshot order.
- Coalescing: if a request for entity X is queued and a newer one arrives before the older
  one starts, the worker may skip the older one (keep latest per `(collection, id)`), except for
  requests flagged `Final` (logout), which are never dropped and always applied last.
- Failure: a failed write (exception or `ModifiedCount == 0` on an existing id) is retried by the worker
  with exponential backoff (proposed 1 s, 2 s, 4 s ... capped at 30 s) up to a max attempts or max age
  (proposed: retry indefinitely for `Final` requests while the process runs; non-final
  requests are superseded by the next snapshot anyway, so after 5 attempts they are
  dropped with an error log). While retrying entity X, later snapshots of X replace the pending
  one (latest wins).
- Failure surfacing: after the first failure for an entity the worker posts a `WorldEvent`
  (`PersistFailed(id, attempt)`) to the game thread, which may warn the player/admins once.
  On success after failures, a `PersistRecovered` event is posted.
- Existing quirk to carry over: `Update` reports success only when `IsModifiedCountAvailable
  && ModifiedCount == 1`, so saving an unchanged document reports failure. The worker must treat
  `MatchedCount == 1` as success. This is a fix flagged for the implementer, Q9.
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

Note: today `Quit()` only disconnects if `Save()` succeeded and otherwise leaves the player
logged in with a disconnected socket (an "Uh oh!" stub). In the new model disconnect never waits on the DB; the
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
changes the timer and sets a bool from another thread, and `Server.Stop()` sends and
disconnects inline. These become requests that return after the sequence above completes (join with timeout).
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
   `doHeartbeat` from the tick every `HeartbeatEvery` ticks. `World.Stop` posts the shutdown item.
   Note that `Living.DoHeartbeat` filter `p.GetType().IsSubclassOf(typeof(IHeartbeat))` is wrong for an interface
   (it is never true, so property heartbeats never fire). Do not fix it silently: flag it (see Risks).
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
   (replaced by `Disconnected` inbound items). Add `NextAutosaveAt`, drop `_lastSave` and the
   `.Minutes` check.
8. Add the persistence worker (MUD.Data or MUD.Core) with retry/spool, and shutdown drain (section 9).
   Wire `Program.Main` shutdown.
9. Convert `SearchCommand` (remove `Thread.Sleep`) and audit all commands for blocking
   (grep for `Sleep`, `.Wait(`, `.Result`, repository calls, file I/O). Creator import/export/terraform
   commands: either accept bounded sync file I/O initially (flag it) or move to the worker.
10. Remove now-dead code (`CommandQueue` dictionary, `IsBusy`) and thread-safety patches that
    become unnecessary (repository locking may stay since the worker thread also reads/writes).

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
- Login while final save pending, plus the repository's model cache: the cache may hand
  a stale object to a new session. Needs a decision on cache ownership (Q11).
- Latent bugs found in current code that this migration will expose or interact with
  (document only; not fixed in this spec): `Living.DoHeartbeat` uses
  `IsSubclassOf(typeof(IHeartbeat))` (always false for interfaces), `Player.DoHeartbeat` uses
  `TimeSpan.Minutes` (component, not total minutes; autosave could trigger at wrong times),
  `Client.Send(string)` discards the result of the line-ending `Replace`,
  `Repository.Update` treats an unchanged document as failure, `World.Players` iterated while mutated,
  `Server.Stop` message typo ("closeed"), `Program.messageReceived` `message.Substring(8)` fails
  for a bare "connect". Recommend separate backlog cards.
- Large mechanical diff in Client and Program touches shared hot files (Client.cs, Player.cs, World.cs);
  steps should land as small PRs in order to avoid conflicts with sibling branches.
- Unbounded inbound queue plus flooding clients: mitigated by per-client caps (Q4).
- Single core ceiling for all game logic. Accepted by the owner; revisit if tick time grows.
- Subtle: events (`ClientDisconnected`) currently fire on I/O threads while holding user code; moving to
  queue-based delivery changes ordering guarantees for anything else subscribed (Server events used in `Program.Main`).

## 13. Open questions

Q1. `Channel<T>` vs `BlockingCollection<T>` for inbound. Both are available on netcoreapp3.1. Proposal: `BlockingCollection` for the simplest timeout read, unless Channels are already in use.
Q2. Tick interval: 100 ms proposed; heartbeat stays 2 s. Confirm or give another value.
Q3. Acceptable snapshot cost on the game thread; do we need incremental/dirty-flag snapshots?
Q4. Inbound flood limits (line length 1024, 20 queued lines per client): confirm values and the penalty.
Q5. Login/creation and other DB reads (room lazy load via `World.GetRoom`, creator import/export): do they
    go through the persistence worker asynchronously now, or is synchronous read on the game thread acceptable
    as an interim step? The card says DB *writes* never run on the game thread; reads are not specified.
Q6. Interim handling of `SearchCommand` (3 s delay) before a scheduled-event queue exists.
Q7. Fix `Client.Send` line-ending normalization as part of this work, or separately (behavior change)?
Q8. Should "Saving..." be followed by a completion/failure message to the player?
Q9. Treat `MatchedCount == 1` as save success (fixing the unchanged-document false failure)?
Q10. Local spool file on shutdown with DB unavailable: acceptable, and where does it live?
Q11. Who owns the repository model cache (it holds live entities shared with the game thread)? Should the
    worker bypass it entirely (proposed) and what happens to cached Player objects after logout?
Q12. Accept the logout behavior change (disconnect no longer waits on a successful save)?
Q13. On a fatal game-loop error: attempt orderly shutdown/save (proposed) or crash fast?
Q14. Is net-dead/linkdead handling (player stays in world after disconnect) wanted soon? It affects 8.4.
Q15. Single persistence worker thread, or one per collection if throughput demands it?
