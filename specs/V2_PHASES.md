# V2 phase plan

Part of [ARCHITECTURE_V2.md](ARCHITECTURE_V2.md). Status: PROPOSED plan for owner approval. Docs only; **no implementation card has been created** (the task forbids it). Decisions: D35
(all seven phases in scope), D36 (phases 1 then 2, sequential), D37 (modules before web), D38 (spike before phase 4). Decision numbers D1..D39 and gap numbers G1..G31 are explained in
[OPEN_QUESTIONS_V2.md](OPEN_QUESTIONS_V2.md) and [CORE_API.md](CORE_API.md) section 9.

Conventions: sizes are rough agent-effort estimates (S = a day or two, M = several days, L = a week or more); they are estimates, not measurements. Every card must leave
`dotnet build MUD.sln` and `dotnet test MUD.sln` green (CI: `.github/workflows/ci.yml`, ubuntu, .NET 8.0.x) and the game playable over telnet. "Files/projects touched" lists are
grounded in the tree at `hermes` 9a83afc; **what I could not verify** is stated at the end of each phase. Nothing was built or run to produce this plan: I had no .NET build of the
repo and no Mongo here (I did read `dotnet --version` = 8.0.128), so test counts and file sizes come from grep/wc on the sources, not from a test run.

Current test baseline (counted from `[Fact]`/`[Theory]` attributes, not by running): `MUD.Tests` 302 test methods in 28 files, `MUD.Web.Tests` 191 in 8 files. The runner expands Theories,
so the reported totals will be higher; cards should record the real before/after totals from `dotnet test`.

## Overview

| Phase | Name | Size | Depends on | Stays deployable because |
|---|---|---|---|---|
| 1 | Front-end-neutral core API; telnet as a project | M-L (5 cards) | none | same process, same behavior over telnet; refactor with existing tests moved, not rewritten |
| 2 | Components and behavior lists in core | L (5 cards) | 1 (D36) | old fields coexist until each is migrated; no data migration (D1) |
| 3 | In-process event bus | M (3 cards) | 2 | no behavior entries exist on live content until phase 5; fast path costs nothing |
| spike | Pre-phase-4 spike (D38) | S (1 card) | 3 (any time before 4) | throwaway, nothing merged except a measurements note |
| 4 | gRPC over UDS and the first trivial out-of-process module | L (5 cards) | 3 + spike | Kestrel/gRPC is additive; a core with no modules configured behaves as before |
| 5 | Desert and temple modules | M-L (3 cards) | 4 | modules are separate processes; fail-open flavor hooks |
| 6 | Web front end on the core API; remove Rev/poll | L (5 cards) | 4 (needs `frontends.sock`), 5 (D37) | web migrated page group by page group; poll removed last |
| 7 | Telnet as a separate process | M (3 cards) | 4, 1 | in-process telnet stays until the separate process is proven |

Total about 30 cards. Order is strict where stated; phase 6 may start after phase 4 if the owner changes D37 (the plan does not assume that).

---

## Phase 1: front-end-neutral core API; telnet as a project (D27, D28, D29)

### Scope

Cut every dependency of core on telnet. After phase 1 `MUD.Core` does not reference `MUD.Telnet`; the telnet code is behind a core API whose surface is **session id plus raw line in; markup text with a channel tag out;
session events** (CORE_API 6.1). Still one process: the telnet project runs in the core process and calls the API as C# interfaces.

### Grounding: what exists today (verified in code on `hermes`)

| Fact | Evidence |
|---|---|
| `MUD.Core` references `MUD.Telnet` | `MUD.Core/MUD.Core.csproj` (`ProjectReference ..\MUD.Telnet\MUD.Telnet.csproj`) |
| Only four core files use telnet types | `using MUD.Telnet;` in `MUD.Core/World.cs:9`, `MUD.Core/GameLoop.cs:4`, `MUD.Core/GameObjects/Player.cs:12`, `MUD.Core/Commands/Player/ChpwdCommand.cs:4`. Nothing in `MUD.Data`, `MUD.Web`, `MUD.PlanarRealms` |
| Telnet types leaking into core | `IClientConnection` (`MUD.Telnet/IClientConnection.cs`, extends `ILoginConnection`), `ILoginConnection` (declared in `MUD.Telnet/LoginSession.cs:8-24`: `RemoteKey`, `Send`, `SendPrompt`, `SetEchoHidden`, `DisconnectWithMessage`), `LoginThrottle` (`MUD.Telnet/LoginThrottle.cs`) |
| Core keys everything by connection object | `World._sessions : Dictionary<IClientConnection, Player>` (`World.cs:53`), `_inboxes` (`:60`), `PreLoginHandler : Action<IClientConnection,string>` (`:87`), `EnqueueLine`/`EnqueueDisconnect` (`:266`, `:287`), `RetireConnection` (`:438`), `TakeOverSession` (`:479`), `DoLogin(.., IClientConnection)` (`:498`), `DoCreateUser(.., Client)` (`:566`; takes the concrete telnet `Client`) |
| `InboundItem` carries the connection | `MUD.Core/GameLoop.cs:24-44` (`Client`, `ForLine`, `ForConnected`, `ForDisconnected`) |
| `Player` owns the connection and renders output | `Player.Connection` (`Player.cs:183`), `PromptChannel` (`:207`), `Quit` calls `_connection.Disconnect(true)` (`:~405`), `ReceiveMessage` picks an `ITerminalHandler` by `SelectedTerm` then `_connection.Send` (`:~408-416`) |
| Rendering is in core | `MUD.Core/Formatting/{ITerminalHandler,AnsiTerminal,GMudTerminal,DefaultTerminal,EFormatOption}.cs`; handlers are DI-scanned (`World.cs:152`) into `World.TerminalHandlers`; `TermCommand` reads them (`TermCommand.cs:33-34`); `Room.cs` uses `EFormatOptions` for exit lines (line 327) so the markup tokens stay in core |
| Login dialogue and throttle are telnet-project code | `MUD.Telnet/LoginSession.cs` (251 lines), `LoginThrottle.cs`; wired in `MUD.Main/Program.cs` (`_loginThrottle` :26, `_gameWorld.LoginThrottle` :283, `messageReceived` :~410, `WorldLoginHandler` :~464); `PasswordChangeFlow` in core uses `ILoginConnection` + `LoginThrottle` (`ChpwdCommand.cs:57-63`) |
| Host glue | `MUD.Main/Program.cs` creates `Server`, wires `ClientConnected`/`ClientDisconnected`, `ShutdownCoordinator` callbacks call `_serverInstance.StopAccepting/SendMessageToAll/Stop` |
| Telnet project is self-contained | `MUD.Telnet.csproj` has no package or project references; classes: `Server`, `Client` (500 lines), `OutboundQueue`, `ConnectionTracker`, `RateLimiter`, `TelnetLimits`, `LoginSession`, `LoginThrottle`, `TelnetCommand`, `OptionRefusals`, codes |

### Design for phase 1 (decided by the decisions, plus the recommendations of G22, G24, G29, G31 flagged for the owner)

New projects (names proposed; csproj changes happen in the cards, not here):

| Project | Contents | References |
|---|---|---|
| `MUD.CoreApi` (new) | the front-end-neutral surface only: interfaces and small value types, no game logic: `ICoreApi`, `IFrontEndSink`, `SessionId`, `OutputChannel`, `LoginResult`, `SessionEndReason` | none (net8.0 class library, no packages) |
| `MUD.Telnet` (existing project, kept and extended; D47) | everything telnet: socket server, option negotiation, limits, `LoginSession`, markup-to-ANSI conversion (`ITerminalHandler` and its three implementations), the `ITerminalHandler`-backed `TelnetSessionSink` | `MUD.CoreApi` only (no `MUD.Core`, no `MUD.Data`) |
| `MUD.Core` | implements `ICoreApi`; keeps `World`, `GameLoop`, commands, players | `MUD.CoreApi`, `MUD.Data` (no `MUD.Telnet`) |
| `MUD.Main` | host: builds `World`, builds the telnet front end, connects them | `MUD.Core`, `MUD.CoreApi`, `MUD.Telnet`, `MUD.Data` |

Why this shape: `MUD.CoreApi` is the one seam both sides depend on, so core can drop its `MUD.Telnet` reference without core referencing the front end and without the front end referencing core. The same
interface later becomes the gRPC `Session` service (CORE_API 7.2) in phase 7 and is what the web uses in phase 6, with a thin client implementation of `ICoreApi`. The project keeps the name **`MUD.Telnet`** (owner decision D47); there is no rename to `MUD.FrontEnd.Telnet`. The existing project is kept and extended, which avoids churn in `MUD.sln`, `MUD.Main.csproj`, `MUD.Tests.csproj` and `using` lines (`scripts/deploy.sh` publishes only `MUD.Main`, `MUD.PlanarRealms`, `MUD.Web`, so it is unaffected).

The surface (all calls are made on a caller thread and only enqueue; they never run game logic inline):

```csharp
// SKETCH ONLY -- docs, not code to commit.
public interface ICoreApi                       // front end -> core
{
    SessionId OpenSession(string frontEnd, string remoteKey, IFrontEndSink sink);   // sink = where core's events for this session go
    void SubmitLine(SessionId id, string rawLine);        // enqueue only; per-session pending cap enforced inside (today World.EnqueueLine)
    LoginResult Authenticate(SessionId id, string name, string password);   // runs on the game thread, blocks the *caller* briefly or completes via sink (card 1d decides; see below)
    LoginResult CreatePlayer(SessionId id, string name, string password);
    void CloseSession(SessionId id, SessionEndReason reason);                  // today World.EnqueueDisconnect
    bool IsThrottled(string remoteKey);                                       // brute-force throttle lives in core (G24 option A)
    void RecordLoginOutcome(string remoteKey, bool success);
}

public interface IFrontEndSink                  // core -> front end, per session; MUST NOT block (G22)
{
    void Output(SessionId id, OutputChannel channel, string markupText, bool flush);
    void SetEchoHidden(SessionId id, bool hidden);
    void Prompt(SessionId id, string text);       // text without line ending (today SendPrompt)
    void Close(SessionId id, string farewellMarkup, bool flush);
    void SessionEnded(SessionId id, SessionEndReason reason);
}
```

Mapping of today's pieces:

| Today | After phase 1 |
|---|---|
| `IClientConnection.Send(string)` called from `Player.ReceiveMessage` | `IFrontEndSink.Output(id, channel, markup, flush)`; `Player` holds a `SessionId` and the sink, not a connection |
| `Player.ReceiveMessage` selecting an `ITerminalHandler` | moves to `TelnetSessionSink`, which converts markup to ANSI using the terminal choice for that session. `SelectedTerm` stays a persisted `Player` field (it is saved: `PlayerFields.cs` comment, `PasswordChangeTests.cs:301-303`); the `term` command sets it through a core call and the sink asks the core for the current value (see card 1c) |
| `Player.Quit` -> `_connection.Disconnect(true)` | `sink.Close(id, "Thanks for playing!" ...)` then `SessionEnded` |
| `World._sessions` keyed by connection | keyed by `SessionId` (string) |
| `GameLoop.InboundItem.Client` | `InboundItem.Session` (a `SessionId`) |
| `World.PreLoginHandler` + `Program.messageReceived` (who / quit / login-session dispatch) | the pre-login dialogue stays in the telnet project (`LoginSession`), calling `ICoreApi.Authenticate/CreatePlayer`; the `who` and unknown-command replies currently in `Program.cs:~420-452` move to the telnet project too, using a read-only `ICoreApi.GetOnlineNames()` (added to the surface) |
| `LoginThrottle` in `MUD.Telnet` | moves to `MUD.Core` (G24 A); `LoginSession` and `PasswordChangeFlow` call `IsThrottled/RecordLoginOutcome` through the API / `Player` |
| `ILoginConnection` | split: `RemoteKey` goes to `OpenSession`; the rest becomes `IFrontEndSink` |
| `World.DoLogin/DoCreateUser(.., Client)` | `ICoreApi.Authenticate/CreatePlayer(SessionId, ...)`; messages that `DoLogin` sends directly to the connection today (retired, still-saving, rename errors; `World.cs:~503-566`) become `LoginResult` values plus `Output` events |

How `Authenticate` completes is a card-level decision with a constraint: login must run on the game thread (it mutates `Players`), and `LoginSession.Login` is synchronous today. Card 1c implements it as
"enqueue to the game thread, front end waits on a `TaskCompletionSource` with a timeout", the same pattern as the gRPC mapping in CORE_API 1 and what phase 7 needs. If that proves awkward the card raises it before coding.

### Cards (proposed; none created)

**P1-1. Introduce `MUD.CoreApi` and session ids in core (no behavior change).**
Scope: add the project with `ICoreApi`, `IFrontEndSink`, `SessionId` (opaque string, core-generated, G31), `OutputChannel` (say, tell, room, combat, system, chat; D44), `LoginResult`, `SessionEndReason`. In `MUD.Core`: add a session registry
(`SessionId` -> sink + Player), implement `OpenSession`, `SubmitLine`, `CloseSession` by wrapping the existing `EnqueueLine`/`EnqueueDisconnect`/`_sessions`. `IClientConnection` still exists and an adapter (`LegacyConnectionSink : IFrontEndSink` in `MUD.Main`) lets the old telnet code keep working while later cards cut over. Output channel tagging: `Living.ReceiveMessage(string)` stays (default channel `system`); add `ReceiveMessage(string, OutputChannel)` and update `SayCommand`, `TellCommand`, `Room.TellRoom` to tag (G29: chat tag is the owner's call; until answered chat stays `system`).
Depends on: none. Size: M.
Files: new `MUD.CoreApi/*`; `MUD.sln`; `MUD.Core/MUD.Core.csproj` (+ reference); `MUD.Core/World.cs`, `MUD.Core/GameLoop.cs`; `MUD.Core/GameObjects/Living.cs`, `Room.cs`; `MUD.Core/Commands/Player/{Say,Tell,Chat}Command.cs`; `MUD.Main/Program.cs`; new tests under `MUD.Tests/`.
Done when: all existing tests pass unchanged; new tests prove (a) session registry open/close/line routing with a fake sink, (b) per-session pending cap behaves as `World.EnqueueLine` does today (reuse `ConcurrencySafetyTests` expectations), (c) a slow sink does not block the game thread.

**P1-2. Move player output and quit onto the sink; remove `IClientConnection` from `Player`.**
Scope: `Player` stops holding `IClientConnection`; it holds `SessionId` and an `IFrontEndSink`. `ReceiveMessage` calls `sink.Output(..., channel, markup, false)` with **no terminal conversion** (conversion has not moved yet, so the adapter from card 1 applies the existing `ITerminalHandler` choice to keep telnet output identical until card 3). `Quit` uses `sink.Close`/`SessionEnded`. `Player.PromptChannel` (used by `ChpwdCommand.PasswordChangeFlow`) becomes a small `IPromptChannel` backed by the sink. `TakeOverSession`, `RetireConnection` use `SessionId`.
Depends on: P1-1. Size: M. Hotspot: `Player.cs` (467 lines mixing identity, persistence, web sync and connection) and `World.cs` (649).
Files: `MUD.Core/GameObjects/Player.cs`, `Living.cs`; `MUD.Core/World.cs`, `GameLoop.cs`; `MUD.Core/Commands/Player/ChpwdCommand.cs`; tests that construct players with a `FakeConnection`/`Client` and must switch to a `FakeSink`: `GameLoopTests.cs`, `PlayerQuitTests.cs`, `SessionTakeoverTests.cs`, `PasswordChangeTests.cs`, `RetiredLoginTests.cs`, `PlayerNameTests.cs`, `PlayerWebSyncTests.cs`, `InputSanitiseTests.cs` (these 8 reference `IClientConnection`/`ILoginConnection`/`Client` per grep; `PlayerWebSyncTests` only once).
Done when: tests pass with the same assertions on message text (tests may change the *way* messages are captured, never what they assert; any changed assertion is called out in the PR).

**P1-3. Move terminal rendering and the `term` setting out of core.**
Scope: move `MUD.Core/Formatting/{ITerminalHandler,AnsiTerminal,GMudTerminal,DefaultTerminal}.cs` to the telnet project (`EFormatOption.cs` and `StringExtensions.cs` stay in core because `Room.cs` and commands emit the tokens; the markup syntax is the contract). `TelnetSessionSink.Output` converts markup per the session's terminal. Remove `World.TerminalHandlers` and the DI scan line at `World.cs:152`. `TermCommand` (`MUD.Core/Commands/Player/TermCommand.cs`) asks the front end through a new optional `IFrontEndSink.TerminalNames()` query, or (recommended, simpler) core keeps a fixed list of names in a constant and the sink maps them; owner not asked because either is internal. `SelectedTerm` stays persisted on `Player` and is pushed to the sink at login and on `term`.
Depends on: P1-2. Size: S-M.
Files: `MUD.Core/Formatting/*`, `World.cs`, `Player.cs`, `Commands/Player/TermCommand.cs`; `MUD.Telnet/*`; `MUD.Tests/InputSanitiseTests.cs` (line 146 assigns `world.TerminalHandlers`), `PasswordChangeTests.cs` (SelectedTerm persistence, unchanged).
Done when: a new test per terminal handler checks the token table (golden strings taken from the current handlers, written before the move: characterization first), a test checks that `%^Red%^` is converted for an ANSI session and left stripped/neutral for `Default`, and `InputSanitiseTests.RawEscapeNeverReachesAnotherClientsSocket` still passes.

**P1-4. Move the login throttle into core; telnet login dialogue calls the API.**
Scope: `LoginThrottle` moves to `MUD.Core` (class unchanged, namespace changes); `ICoreApi.Authenticate/CreatePlayer/IsThrottled/RecordLoginOutcome` implemented; `MUD.Telnet/LoginSession` loses its `LoginThrottle` field and `ILoginHandler` is replaced by calling `ICoreApi`; `PasswordChangeFlow` uses the same throttle via `World`. `Program.messageReceived` / `WorldLoginHandler` (the pre-login `who`, `quit`, unknown-command replies, `MUD.Main/Program.cs:~410-485`) move into the telnet project as `PreLoginHandler`. `World.DoLogin/DoCreateUser` return `LoginResult` instead of sending to a connection. `World.PreLoginHandler` is deleted.
Depends on: P1-2 (can follow P1-3). Size: M.
Files: `MUD.Telnet/LoginSession.cs`, `LoginThrottle.cs` (moved); `MUD.Core/World.cs`, `Commands/Player/ChpwdCommand.cs`; `MUD.Main/Program.cs`; `MUD.Tests/LoginSessionTests.cs` (12 tests), `RetiredLoginTests.cs`, `PasswordChangeTests.cs`, `PlayerNameTests.cs`, `SessionTakeoverTests.cs`.
Done when: `LoginSessionTests` assertions are unchanged (they run against a fake `ICoreApi` instead of a fake handler); throttle semantics identical (blocked after N failures per key, cleared on success); passwords never appear in logs (the existing `Loggable` test is kept).

**P1-5. Flip the project reference; telnet host binds to `ICoreApi`; remove the adapter.**
Scope: delete `MUD.Core -> MUD.Telnet` reference (`MUD.Core/MUD.Core.csproj`), delete `IClientConnection` from the telnet project (`Client` implements what the sink needs through `TelnetSessionSink`), remove `LegacyConnectionSink` from `MUD.Main`, `GameLoop.InboundItem` no longer mentions telnet, `MUD.Main` becomes a pure composition root. `ShutdownCoordinator` callbacks keep calling the telnet server for `StopAccepting/SendMessageToAll/Stop`; the "world is restarting" message (D29) is sent by `TelnetSessionSink.SessionEnded(Shutdown)`. Update `README.md`/`specs/ARCHITECTURE.md`, `specs/API.md` and `specs/CONFIGURATION.md` for the new layout (spec updates are part of this card).
Depends on: P1-3, P1-4. Size: S-M.
Files: `MUD.Core/MUD.Core.csproj`, `MUD.Tests/MUD.Tests.csproj` (keeps referencing `MUD.Telnet` for the telnet tests), `MUD.Main/MUD.Main.csproj`, `MUD.sln`, `MUD.Main/Program.cs`, `MUD.Telnet/Client.cs`, `specs/{ARCHITECTURE,API,CONFIGURATION}.md`.
Done when: `grep -R "MUD.Telnet" MUD.Core` finds nothing (CI step proposed in the card: a build check that the `MUD.Core` assembly has no reference to `MUD.Telnet`); full test suite green; manual smoke documented in the PR (telnet in, create, say, quit, duplicate-login takeover, idle timeout) with the explicit note that no Mongo was available if that is the case.

### Test strategy for phase 1

- **Characterize before moving.** The first commit of P1-2, P1-3 and P1-4 each adds characterization tests for the behavior about to move (message text on quit/takeover/retired login, token-to-ANSI tables, throttle thresholds) using the old types, then the move commit changes how the tests build their fakes, never what they assert.
- **Fakes.** One shared `FakeSink : IFrontEndSink` in `MUD.Tests` records `(SessionId, channel, text)` tuples; replaces the three ad-hoc `FakeConnection` classes (`GameLoopTests`, `LoginSessionTests`, `PasswordChangeTests`). The existing fake clock (`GameLoopTests`) is reused.
- **Telnet project tests stay as they are** (`TelnetClientTests`, `TelnetLimitTests`, `ServerLifecycleTests`, `InputSanitiseTests`' `Client` parts) because the socket code does not change.
- **Slow-sink test (G22)** proves the game thread keeps ticking while a sink blocks.
- **Mongo-backed paths cannot be run in the sandbox**; the tests use the fake repositories already in `MUD.Tests`. Say so in each PR.

### Risk, deployability, size

Risk: medium. The code is cut along existing seams (only four core files import telnet), but `Player.cs`/`World.cs` are hotspots and eight test files change their fakes. Stays deployable: yes, every card keeps one process and identical telnet behavior. Size M-L overall; five cards; critical path P1-1 -> P1-2 -> (P1-3 | P1-4) -> P1-5.
Not verified: that nothing else in the 28 test files indirectly constructs `Client`/`Player` with a connection (I grepped for `IClientConnection|ILoginConnection|Client` only); the exact amount of code in `Program.cs` that belongs in the telnet project (I read lines 285-485, not the whole 485 lines); that `Authenticate` blocking-with-timeout is acceptable for `LoginSession` tests (a design call for card P1-4).

---

## Phase 2: components and behavior lists in core (D1, D3, D5, D13, D14, D36)

### Scope

Introduce components (core-typed and module-schemaless), the `BehaviorRef` list on rooms, items and livings, and the component registry; retire `ExtendedProperty` for roles, search and bleeding; fix the import default (G17). No events yet (phase 3). Starts only after phase 1 is merged (D36).

### Grounding

| Fact | Evidence |
|---|---|
| Polymorphic `ExtendedProperty` list on every `GameObject` | `MUD.Core/GameObjects/GameObject.cs`; classes in `MUD.Core/Properties/` (`ExtendedProperty`, `LivingProperties/BleedingProperty`, `PlayerProperties/{Admin,Creator}Property`, `RoomProperties/BasicSearchProperty`) |
| Roles resolved by type in command lookup | `Living.ResolveCommand` (`Living.cs:~178-190`) tests `ExtendedProperties.Any(s => s.GetType() == typeof(AdminProperty))` |
| Heartbeat only for players | `World.doHeartbeat` iterates `Players` (`World.cs:633-648`); `Living.DoHeartbeat` iterates `IHeartbeat` properties |
| The website reads roles from Mongo `_t` discriminators | `MUD.Web/Services/MongoAccountStore.cs:59-60`, `MongoPlayerEditStore.cs:64-66` (G30) |
| YAML `properties:` and behavior registry | `MUD.Core/Import/AreaImporter.cs`, `AreaExporter.cs`; ROOM_FILES 4.5, 7 |
| Startup import default true | `MUD.Core/WorldSettings.cs:15`, `MUD.Main/appsettings.json:10`, `World.ImportContent` (`World.cs:614`) |
| 6 empty command files, `Food`/`Wand` unregistered | see ARCHITECTURE_V2 section 11 (not touched by phase 2) |

### Cards (proposed; none created)

**P2-1. Component storage and registry.**
Scope: `Components` dictionary on entities (BSON `components.<module>.<name>` for schemaless, typed fields for core components), a registry that knows core component types and treats anything else as opaque BSON that is preserved on load/save (so unknown module data is never lost, D10/D13), class-map wiring (`MUD.Data` class maps via `MongoDBClassMapManager`). Core components `Description`, `Position`, `Exits` etc. are **not** all migrated in this card; the registry and the dictionary are.
Depends on: P1-5. Size: M. Risk: persisted shape; no migration (D1), new documents only.
Files: `MUD.Data/Entity.cs` + class maps, `MUD.Core/GameObjects/GameObject.cs`, new `MUD.Core/Components/*`, tests.

**P2-2. `BehaviorRef` list on rooms, items and livings.**
Scope: add `List<BehaviorRef>` (Hook, Module, Type, Priority, Config as BSON, SchemaVersion) to room, item, player, NPC; **not** on `Feature` (D14). Persistence shape per G2 (recommended: embedded array; owner confirms). No dispatch yet: the list is stored, loaded, snapshot-copied, and editable. Creator command to add/list/remove entries (e.g. `behavior add <hook> <module> <type> [json]`, final syntax in the card) so builders can bind (the editing surface for the web is an open item; creator command is the minimum for testing). Importer/exporter: YAML `behaviors:` list on room/item replaces `properties: behavior:` (ROOM_FILES 4.5/7); import is collision-safe (D39): an existing entry with the same `(hook, module, type)` is reported and skipped unless a flag says otherwise.
Depends on: P2-1. Size: M-L.
Files: `GameObject.cs`/`Room.cs`/`InventoryItem.cs`/`Player.cs`, `MUD.Core/Import/*`, `MUD.Core/Commands/Creator/*` (new command file), `MUD.Tests/AreaImporterTests.cs`, `AreaExporterTests.cs`, `FeatureYamlTests.cs`.

**P2-3. Roles as a component; web read path follows (G30 A).**
Scope: `Roles` core component replaces `AdminProperty`/`CreatorProperty` (+ creator's current directory); `Living.ResolveCommand` and `PromoteCommand` use it; `MUD.Web` `MongoAccountStore`/`MongoPlayerEditStore` read the new field (the only change in `MUD.Web` in phase 2). No dual-read of the old shape (D1).
Depends on: P2-1. Size: M. Risk: medium-high (two processes, one document).
Files: `MUD.Core/Properties/PlayerProperties/*` (removed), `Living.cs`, `Commands/Admin/PromoteCommand.cs`, `Commands/Player/CommandsCommand.cs`, `MUD.Web/Services/MongoAccountStore.cs`, `MongoPlayerEditStore.cs`, `MUD.Web.Tests/{AccountService,PlayerEdit}Tests.cs`, `MUD.Tests/RetiredLoginTests.cs`.

**P2-4. Bleeding and search off `ExtendedProperty`.**
Scope: search configuration (reward, cooldown, last-reward state) becomes a core component, fixing "cooldown resets on every boot" (ROOM_FILES 7.4); search stays baked in (D2). Bleeding becomes a heartbeat behavior-entry shape: component `status.bleeding` (duration) and an in-process handler registered for the heartbeat hook (wired up for real in phase 3; in this card it runs through the existing `Living.DoHeartbeat`). `ExtendedProperty` and `Properties/*` deleted.
Depends on: P2-2. Size: M.
Files: `MUD.Core/Properties/*`, `Living.cs`, `Commands/Player/SearchCommand.cs`, `Commands/Creator/ApplyStatusCommand.cs`, importer/exporter.

**P2-5. Import default and docs (G17); optional kind cleanup.**
Scope: `ImportOnStartup` default false (`WorldSettings.cs`, `appsettings.json`), documentation of the manual one-off import (D39), banners already in place from the spec revision; mark ROOM_FILES sections that remain accurate. Optional second part (separate card if big): collapse `Living`/`Player`/`NPC` into kinds + components (ARCHITECTURE_V2 5.1). That second part is **not required for phase 3** and is recommended to stay out of V2's critical path.
Depends on: P2-2. Size: S.

### Test strategy and risk

Characterization tests for `Living.ResolveCommand` role precedence (including the Admin branch quirk: it checks `CreatorCommands`, ARCHITECTURE_V2 11 item 1; the test pins today's behavior, the owner decides separately) before P2-3; round-trip persistence tests (entity with unknown module component survives load/save) for P2-1; importer collision tests for P2-2. Mongo-backed behavior cannot be run in the sandbox.
Risk: high (shape of persisted entities; class maps; two processes reading roles). Deployable: yes with a fresh database (D1: no live data; rebuild from YAML, re-create the account). Size L.
Not verified: that `InitializeProperty()`/`RemoveProperty()` truly have no caller (grep found only overrides plus `BleedingProperty`'s own call, ARCHITECTURE_V2 11 item 2); how many YAML fixtures use `properties:`; the class-map interactions of `MongoDBClassMapManager` with an open-ended components dictionary.

---

## Phase 3: in-process event bus (D3, D4, D6-D9, D15)

### Scope

Event records, Before/After chain, parking and re-validation, behavior resolution through the `(Module, Type)` dictionary with **in-process handlers only**, cascade/budget/re-entrancy guards (numbers config, defaults chosen by the card and flagged), dormant-behavior accounting (D10). First hooks wired: `entity.move`, `item.move` (get/drop), `command.entered`, `heartbeat`.

### Grounding

Movement is `Living.MoveToRoom` (`Living.cs:135-158`) calling `Room.ExitRoom`/`EnterRoom` (`Room.cs:87-107`); exits are `AnonymousCommand`s built by `Room.RebuildExitCommands`; get/drop are `GetCommand`/`DropCommand`; parking maps to `Living.NextActionAt` + `CommandQueue` (`MUD.Core/Commands/CommandQueue.cs`); the game thread is `GameLoop.Run` (100 ms tick, `MaxItemsPerPass`). Not verified: how `CommandQueue.DelayThen` and a parked actor interact exactly (read CORE_API 2.3, not the queue code in depth).

### Cards (proposed)

**P3-1. Event record, dispatcher, After phase, hookable change events.** Generic string-named event, `cause_id`/`trace_id`, parties, After notifications, per-tick event budget as an inbound queue share, cascade depth, re-entrancy guard, fast path benchmark test (zero allocations for entities with no entries is the aim; claim only what a test shows). Change events (`component.set`, `behavior.add`) emitted by the phase 2 APIs. Depends on P2-2. Size M. Files: new `MUD.Core/Events/*`, `GameLoop.cs`, `World.cs`.
**P3-2. Before chain, parking, re-validation, verdicts.** Allow/Deny with emitted events, all-handlers-wait, one chain deadline with the fail policy (flavor/ro rule per G3 default), first-Deny-to-arrive rule, redirect as Deny + new move; `stop` behavior (G6). Test double handlers with scripted delays; fake clock. Depends on P3-1. Size M-L (the hardest card of the plan). Files: `Living.cs`, `Room.cs`, `Commands/Player/{Get,Drop}Command.cs`, `CommandQueue.cs`, `AnonymousCommand.cs`, tests incl. two simultaneous movers.
**P3-3. In-process handler registry; port bleeding; heartbeat index (G8).** `(Module, Type)` dictionary with in-process registrations; bleeding as the first real behavior; heartbeat delivered to entities that carry a heartbeat entry (rooms and NPCs gain heartbeats; noted as a behavior change). Dormant-skip counting and once-per-interval log. Depends on P3-2. Size M.

Risk: medium-high (parked-command state machine; re-validation). Deployable: yes; no live content carries entries, so the fast path is exercised only by tests until phase 5. Size M overall; three cards. Cut candidates if scope must shrink: the `item.move` hook (keep `entity.move`, `command.entered`, `heartbeat`).

---

## Pre-phase-4 spike (D38) -- one throwaway card

**S-1. Spike: gRPC over UDS feasibility and measurements.** Branch `spike-grpc-uds`, **never merged**; output is a short `specs/` note (or card comment) with numbers. Prove and record:
1. UDS round-trip latency of a unary call (idle and under load) and of a server-stream push plus unary verdict reply, on this host (median, p99, 10k calls; tool and script named in the note).
2. `Grpc.Tools` codegen works in CI: a draft PR on a throwaway branch with a trivial `.proto` and `Grpc.Tools`/`Google.Protobuf` references, restored and built by `.github/workflows/ci.yml` as it stands.
3. Kestrel serving **two UDS endpoints** (`modules.sock`, `frontends.sock`) in one host, h2c only, a client on each; socket file mode after bind; stale-file behavior.
4. Adding ASP.NET Core to the console host `MUD.Main` (`FrameworkReference Microsoft.AspNetCore.App`) without breaking the existing `Assembly.LoadFrom` scan and Scrutor registration in `World.LoadTypes()`; startup time before/after; `dotnet publish` output size delta.
Constraint: no changes under `MUD.Core`/`MUD.Main` on any branch that will be merged; spike code lives in a scratch project. Depends on: P3 done (or earlier; only the CI and host questions need the repo). Size S. The measurements feed the deadline numbers (D15, still an open item) -- the spike **records**, the owner **decides**.
Not verifiable here: all four (no build was run in this task).

---

## Phase 4: gRPC over UDS and the first trivial out-of-process module (D16-D26)

### Scope

`contracts/` + `MUD.Contracts`, Kestrel on two UDS endpoints in the core process, module service, supervisor, launch entries/grants/manifest handshake, token auth, `required` gate, a trivial C# module (suggested: `MUD.Modules.Hello` greeting on login and counting moves) and the test harness.

### Cards (proposed)

**P4-1. `contracts/` and `MUD.Contracts`.** `.proto` files per CORE_API 7.2 (module service, event/verdict messages; `contract_version` from day one, D24), Grpc.Tools codegen, CI build proves it; no contract tests (D26). Depends on spike. Size S-M. Files: `contracts/mud/*.proto`, `MUD.Contracts/MUD.Contracts.csproj`, `MUD.sln`.
**P4-2. Core gRPC host and handshake.** Add ASP.NET Core to `MUD.Main` as the spike found; Kestrel on `modules.sock` (and `frontends.sock` stub); `Hello` with token, strict contract equality, manifest receipt, grants check against `~/.config/mud/module-grants.yaml`; `Subscribe`, `Ping`, `Ready`; inbound items for module calls with a bounded queue and per-pass share. Depends on P4-1, P3-3. Size L. Files: `MUD.Main/*`, new `MUD.Core/Modules/*` (host side), run-directory locking (`flock`, stale sockets).
**P4-3. Supervisor, launch entries, `required` gate.** Spawn children with token in env, connection watch, backoff/crash-loop (numbers from config, defaults flagged), stale-process check (G27), `modules` admin commands (`list`, `reload`, `diff`, `token`, G26/G28), required-module gate holding the telnet front end until Ready, start-up timeout exit with a clear error (D21), systemd unit restart limit in `planar-realms.service`/DEPLOYMENT.md. Depends on P4-2. Size L.
**P4-4. Remote behavior dispatch: verdicts, emit, state, timers.** Wire the phase 3 dictionary to connected modules: deliveries on the server stream, `SubmitVerdict` (late dropped), `Emit` with capability checks, `SetState/GetState` (new `module_state` collection through the repository path, size/rate limits configurable), `ScheduleTimer`. Depends on P4-2. Size L.
**P4-5. Trivial module, SDK helpers and harness.** `MUD.Modules.Hello` (or similar), `MUD.Modules.Sdk` (connection loop, ping, context wrapper with named helper methods, G1/D40), `ModuleTestHarness`, docs. Integration test with a real UDS in a temp dir. Depends on P4-4. Size M.

Risk: high (learning curve, new dependencies, hosting). Deployable: a core with an empty launch-entry file is the current game plus an idle Kestrel; if the spike fails, the plan reshapes here. Size L.
Not verified: package/version compatibility beyond the nuspec reading in CORE_API 8; any behavior under load.

---

## Phase 5: desert and temple modules (D13, D14)

### Scope

Implement MODULES 7 and 8 as real modules plus the builder-created content they need (how module-needed content gets created is an open item; the cards use creator commands and document the commands used). They are the acceptance test of phases 2-4.

### Cards (proposed)

**P5-1. Desert module.** `DesertStormRoom`, `DesertStormExposure`, `timer.fired` handling, `desert.storm`/`desert.sand_blind` components, state document keys, idempotent reconciliation, `MUD.Modules.Desert`. Depends on P4-5. Size M.
**P5-2. Temple module.** `DesertTempleTrap`, `TempleLeverRoom`, rearm timers, the two-player race handled module-side (G6). Depends on P4-5. Size M.
**P5-3. Content and acceptance.** Builder walkthrough creating rooms/behaviors by creator commands, an end-to-end scripted telnet session, crash/restart exercise (kill -9 the module mid-storm and observe), documentation of the findings. Revision pass on CORE_API for gaps found (the plan budgets for one). Depends on P5-1, P5-2. Size S-M.

Risk: medium; this is where G6, G7, G8, G11 get tested for real. Deployable: modules are separate processes with flavor (fail-open) hooks. Size M-L. Cut candidates: the lever (keep traps) or the temple (keep desert).

---

## Phase 6: web front end on the core API; remove Rev/poll (D27, D30, D31, D37)

### Scope

Core exposes the Admin service on `frontends.sock`; `MUD.Web` becomes a client; `MongoClient` leaves `MUD.Web`; session tokens; delete the poll machinery and `Rev`.

### Grounding

`MUD.Web/Program.cs` registers `IMongoClient`, `MongoAccountStore`, `MongoRoomReader`, `MongoRoomEditStore`, `MongoPlayerEditStore`, `MongoAuditLog` (ARCHITECTURE_V2 2.1); the poll is `MUD.Core/RoomWebSync.cs` (191 lines), `PlayerWebSync.cs` (129), enabled by `World.EnableWebSync` (`World.cs:360`, `Program.cs:282`) with `Player.ApplyWebEdit/SyncWebFieldsFromStore` and `Room.ApplyWebEdit`; tests: `RoomWebSyncTests` (16), `PlayerWebSyncTests` (14), `MUD.Web.Tests` (8 files, 191 test methods). `MUD.Web.Tests` already references `MUD.Core` (csproj). `MUD.Data/RoomRules.cs`, `PlayerFields.cs` carry rules shared by both processes.

### Cards (proposed, split by page group)

**P6-1. Core Admin service + session tokens.** In-memory session table, `WebAuthenticate/Logout`, per-call role re-check and revocation on retire/demote (D31), `AdminApi` implemented against the existing room/player/audit code in core (rules from `RoomRules`, `PlayerFields`). Depends on P4-2. Size L.
**P6-2. Web: accounts and login on the core API.** Replace `MongoAccountStore`; cookie holds the token (HttpOnly); markup-to-HTML converter with correct escaping (D28) if any page shows game output (unit tests with hostile input). Depends on P6-1. Size M.
**P6-3. Web: rooms, map.** Replace `MongoRoomReader`/`MongoRoomEditStore`; last-write-wins (no `expected_rev`; D30). Size M.
**P6-4. Web: players and audit.** Replace `MongoPlayerEditStore`/`MongoAuditLog`; audit rows written by core for web/creator edits (not module changes, D34). Size M.
**P6-5. Remove Rev/poll and Mongo from web.** Delete `RoomWebSync`, `PlayerWebSync`, `ApplyWebEdit`, `SyncWebFieldsFromStore`, `PlayerFields.NotWrittenByGame` special casing, `Rev`; remove `MongoDB.Driver` from `MUD.Web.csproj`; drop `mongo.env` from `planar-realms-web.service`; update WEB.md and mark WEB_EDITING removed sections. Size M.

Risk: medium (big surface; behavior is spec'd; tests re-target to a fake core client). Deployable: each page group is switched independently; the poll is removed last. Size L.
Not verified: whether any page needs live game state not in the Admin surface; the real size of `MUD.Web` beyond the files named.

---

## Phase 7: telnet as a separate process (D27, D29)

### Scope

The telnet host runs as its own executable talking to core over `frontends.sock` using the same session service.

### Cards (proposed)

**P7-1. Session service on `frontends.sock`.** `OpenSession`, `Authenticate`, `CreatePlayer`, `Line`, `CloseSession`, event stream (output with channel, echo, prompt, close, session-ended); the `ICoreApi` gRPC client implementation; front-end token (G21). Depends on P4-2, P1-5. Size M.
**P7-2. `MUD.Telnet.Host` executable + deployment.** Console host for the existing telnet project, reconnect-with-backoff to core, "world is restarting" on core loss, second systemd unit `planar-realms-telnet.service`, `scripts/deploy.sh` publishes it. Depends on P7-1. Size M.
**P7-3. Cut-over and removal of the in-process path.** Config switch, remove the in-process adapter once unused, link-dead behavior as it is decided (open item; until then a lost front end logs players out as today). Size S.

Risk: low-medium; main risks are session loss on restart and output back-pressure. Deployable: both modes coexist until P7-3. Size M. Optional in the sense that the game works in-process indefinitely; but D35 puts it in scope.

---

## Decisions this plan needed from the owner (answered)

The six questions raised by the first version of this plan were answered by the owner after the spec revision (decisions D40-D49 in `owner-decisions.md`, Kanban store). Nothing below blocks starting phase 1.

1. Project name: keep `MUD.Telnet`, no rename (D47).
2. `chat` gets its own channel tag; the channel list is say, tell, room, combat, system, chat; untagged messages default to `system` (D44, G29).
3. Confirmed: the login throttle moves into core and the dialogue stays in the telnet project (D43, G24); core generates session ids (D46, G31); the phase 2 card that introduces the `Roles` component also changes the web's two read paths (D45, G30).
4. `ImportOnStartup` defaults to false in P2-5; the manual `import` command stays (D41, G17).
5. The optional kind-collapse (remove `Living`/`Player`/`NPC` subclasses) is not on the V2 critical path; it stays an optional separate card at the end of phase 2, taken later or never (D48).
6. In-process handler registration stays permanently after phase 4 as a second adapter for core-shipped behaviors (D49; ARCHITECTURE_V2 4.3).

Also decided after the revision: G22 (the front-end sink output method must never block, enforced by a test with a deliberately slow fake front end; D42) and G1 (dedicated wire calls for state and timers plus a generic emit, with named helper methods in the C# SDK; D40).

Gaps G2-G16, G18-G21, G23 and G25-G28 in CORE_API section 9 are still undecided. They are decided when the phase that needs them is cut into cards, and are listed on the Kanban card "[deferred] MUD V2 redesign: parked decisions and later work".

## What could not be verified (collected)

No build, test run, Mongo, gRPC package restore or CI run was possible; every size and risk figure is an estimate from reading the sources; test counts are attribute counts; the whole of `Program.cs`, `Player.cs`, `Room.cs` and `World.cs` was
read only in the portions cited; `MUD.Web` internals were read only through grep and the earlier draft's inventory; the claim that only four core files use telnet types rests on `grep "MUD.Telnet"` plus an `IClientConnection|ILoginConnection` grep of the test files and may miss indirect uses through `Client`
(e.g. `World.DoCreateUser` takes the concrete `Client`).
