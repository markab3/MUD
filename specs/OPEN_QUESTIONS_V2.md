# V2 decisions record

Status: the owner's decisions for the V2 redesign, recorded. This file replaces the draft list of 41 open questions (PR #42); the file name is kept so
existing links keep working. The source is the owner's walkthrough, `owner-decisions.md` (Kanban store), whose numbering D1..D49 is used here (it is NOT the draft's Q-numbering; the
draft Q number is shown in brackets where the decision file gives one). The decisions are authoritative; the specs were revised to match them (task t_17b7802d).
Pointers name the section that implements each: A = [ARCHITECTURE_V2.md](ARCHITECTURE_V2.md), C = [CORE_API.md](CORE_API.md), M = [MODULES.md](MODULES.md), P = [V2_PHASES.md](V2_PHASES.md).
The owner's standing requirements (split core and front end; core sole DB writer; composition over inheritance; one process per module started by core; gRPC over UDS; explicit,
inspectable designs) still stand (A 1).

## Decisions

| # | Decision | Implemented in |
|---|---|---|
| D1 (Q10) | No live data worth keeping (1 player, 3 rooms). Rebuild from YAML, re-create the account. No migration code, no dual-read | A 5.7; P phase 2 |
| D2 | Search stays baked into core, like look. Extension slots exist only where extensibility is intended | A 4.1, 5.7, 9 |
| D3 | Initial hook points: heartbeat, command entered, living enters/exits room, item dropped, item taken. The living, the item and the room/container can each carry behavior. Each action has a BEFORE and an AFTER phase | A 5.3-5.4; C 2.1, 2.4 |
| D4 | Events are generic string-named records (`entity.move`, `item.move` with a Reason). Phase is a field. One `entity.move` covers exit and enter with from/to roles; After notifications go to the mover and both rooms | A 5.3; C 2.4 |
| D5 | Entities hold no module-code references. Each has a BehaviorRef list (Hook, Module, Type, Priority, Config JSON, SchemaVersion); the entity filters its own list and core resolves (Module, Type) in a dictionary at call time. Hot swap edits only the dictionary | A 5.4; C 2.2; M 5.3 |
| D6 | Behaviors get immutable snapshots and return ALLOW or DENY(message) with optional emitted events. MODIFY is removed. Redirect = Deny plus a new `entity.move`. Any Deny vetoes | A 5.4; C 2.5; M 8.2 |
| D7 | Handler order is not significant; the player sees the first Deny message to arrive | C 2.7; A 5.4 |
| D8 | Events emitted by all handlers apply whether or not the action is denied. Effects that depend on committing belong in the AFTER phase | C 2.5; M 8.2 |
| D9 | All world change is an event on the game thread and every event is hookable. Needs cascade depth limit, per-tick budget, per-module emission quota, re-entrancy guard, fast path (numbers not set) | A 5.5; C 2.8, 5 |
| D10 | Dormant behaviors: entries stay on entities when a module unloads; unresolved (Module, Type) is skipped under the hook's fail policy (flavor open, rule closed, override bounded by a core policy file), counted and logged once per interval; reload resumes with stored config; unreadable SchemaVersion is a miss | A 5.4; C 2.6; M 5.2 |
| D11 | No overlay/undo/TTL. A temporary effect times itself out with `timer.schedule`, stores its own state, uses idempotent undo. Core does not track what is temporary. The draft's overlay design is dropped | A 5.4; C 4.2; M 7 |
| D12 | Module-private non-entity state is a keyed module-state document, written with `state.set`, read on start. Core is the only DB writer. Size and write-rate limits per module (not set) | A 5.6; C 4.1 |
| D13 | State split by ownership: core-affecting / player-visible / read-from-snapshot state is an entity component; module-private scheduling detail is in the state document. Startup reconciliation must be idempotent | A 5.6; C 4.1; M 7.1, 7.4 |
| D14 | Features stay inert: behaviors attach only to rooms, items and livings. The temple lever is an immovable item or a room-level behavior | A 5.1; M 8.1, 8.3 |
| D15 (Q14) | The Before chain waits for every handler or a single chain deadline; no short-circuit on first Deny. The acting entity is parked, re-validation precedes commit. Deadline numbers unmeasured (proposal 50 ms per handler, 150 ms per chain, 3 remote handlers) pending the spike. Timeout follows the hook's fail policy | C 2.3, 2.6; P spike |
| D16 | Module dials core over UDS; server stream for events plus unary calls back (SubmitVerdict, state, timers, emit); separate Ping; late verdicts matched by event_id and dropped; a SubmitVerdict batch is applied atomically on the game thread (assumed) | C 3, 7.1, 7.2 |
| D17 | Module authentication: random per-spawn token in the child's environment, presented on the first call with the manifest and contract version. Guards against accidents. No SO_PEERCRED. A dev tool can issue a token for a hand-started module | C 7.3; A 7.5; M 4 |
| D18 | Capabilities: owner-edited grants file (`~/.config/mud/module-grants.yaml`, mode 600), reloadable by an admin command; partial grant starts the subset with a warning; `required: true` capability refuses the start. Manifest ships with the module and is sent on connect. Launch entries are a separate live-readable core config. Update flow: build, `modules reload`, diff, edit grants, reload | C 3.1; M 1, 2 |
| D19 | Trust stance: same machine only (UDS), owner's own modules only; capability checks are a seatbelt against bugs | A 7.5; C 3.1 |
| D20 | Core spawns and supervises modules as child processes (token in env, connection watch, restart with backoff, crash-loop handling). No systemd launcher in the first cut. Modules exit after a grace period if the core connection is lost; core checks for stale module processes at start. Numbers not set | M 3, 9; A 4.1 |
| D21 | A module marked `required: true` in its launch entry holds the front-end gate until Ready; not Ready within the start-up timeout -> core exits with a clear error and stops started modules. Timeout not set; the systemd unit needs a restart limit | M 3, 6; C 3 |
| D22 | Two UDS sockets: modules.sock (module services) and frontends.sock (session/admin services). Capability checks still apply | C 7.3; A 4.1 |
| D23 | `.proto` files live in `contracts/` with a small `MUD.Contracts` project (Grpc.Tools codegen), referenced by core, front ends and C# modules | C 7.2; P phase 4 |
| D24 | Strict equality on a contract version number sent in every first call; refusal logs both versions. The field exists from day one so additive-only/range can replace it later | C 7.4; M 10 |
| D25 | Modules are written in C#. The wire contract stays language-neutral. No conformance suite | C 7.2, 8; M 11 |
| D26 | No automated contract tests (golden files/buf) until the contract settles after phases 3-5 | A 7.6; M 11 |
| D27 (Q20) | Telnet becomes its own project behind a front-end-neutral core API, still in the core process first; a separate telnet process is phase 7. The web uses the core API (gRPC), replacing MUD.Web's direct Mongo access | A 7.3; C 6.1; P phases 1, 6, 7 |
| D28 | Core output is the existing `%^Color%^` markup string plus a channel tag (say, tell, room, combat, system; `chat` added later by D44). Telnet converts to ANSI; web needs a markup-to-HTML converter with correct escaping. Structured spans deferred. Modules use existing channels | A 7.2; C 6.1 |
| D29 | On core restart, front ends close connections with "world is restarting" and clients reconnect. Characters saved on shutdown; persistence worker drained | A 7.3; C 7.5; M 10 |
| D30 | `Rev` (optimistic concurrency) is dropped. Web and creator edits are last-write-wins; the per-field audit keeps history. The Rev/poll machinery is removed; WEB_EDITING.md largely superseded | A 3, 7.4; P phase 6 |
| D31 | Web identity: core issues an opaque session token at web authentication (HttpOnly cookie suggested); core re-checks the current role on every call and can revoke sessions; in-memory session table, lost on restart. Passwords stay plaintext; never logged or displayed | A 7.3; C 6.1, 7.2 |
| D32 | seed-admin.sh was a one-off bootstrap and is not maintained. No core-admin CLI. First-run seeding on an empty database is possible later and is deferred. Core remains the only DB writer | A 7.4 |
| D33 | Backups: deferred | A 7.4 |
| D34 | Module-made changes are not durably audited; logs (module, event, targets, trace id, cause id) are the record. Web/creator edits keep the per-field audit | A 3, 7.4, 7.6; C 2.8 |
| D35 (Q38) | All seven phases are in scope: 1 front-end-neutral core API with telnet as a project; 2 components/behavior lists; 3 in-process event bus; 4 gRPC over UDS + first trivial module; 5 desert and temple modules; 6 web front end on the core API; 7 telnet as a separate process | A 8; P |
| D36 | Phases 1 then 2 are sequential (they touch Player/Room/World/GameObject) | P phases 1-2 |
| D37 | Modules before web (web stays phase 6) | A 8; P |
| D38 | One throwaway spike card before phase 4: UDS round-trip latency, Grpc.Tools codegen in CI, Kestrel on two UDS endpoints, ASP.NET Core in the console host MUD.Main; record measurements | P "Pre-phase-4 spike"; C 8 |
| D39 (Q29) | The area YAML is only an optional one-off, manually triggered import/export format. The database is the sole source of truth; no recurring sync. ROOM_FILES sections 6 and 9 are obsolete; 4.5 and 7 are replaced by behavior entries. Import must be collision-safe. Behavior binding lives only in entity behavior lists | A 3, 7.4; C 9 (G17); P phase 2 |
| D40 (G1) | Module-to-core calls: dedicated wire calls for state and timers plus a generic emit; the C# SDK offers named helper methods. Owner is not fully clear on it; revisit when phase 4 is cut into cards | C 9 (G1), 7.2; P phase 4 |
| D41 (G17) | `ImportOnStartup` defaults to false in phase 2 (P2-5); the manual `import` command stays; importer code and rules untouched | C 9 (G17); P P2-5 |
| D42 (G22) | The front-end sink's output method must never block (it only enqueues and returns); documented and enforced by a test with a deliberately slow fake front end. No second core-side queue | C 9 (G22); P P1-1 |
| D43 (G24) | The login dialogue stays in the telnet project; the brute-force `LoginThrottle` moves into core so telnet and web share one limit; telnet calls a core Authenticate operation | C 9 (G24); P P1-4 |
| D44 (G29) | `chat` gets its own channel tag: say, tell, room, combat, system, chat. Untagged messages default to `system` (assumed, not separately confirmed) | C 9 (G29), 6.1; P P1-1 |
| D45 (G30) | The phase 2 card that introduces the `Roles` component also changes the web's two read paths to the new field. No mirror write, no dual-read | C 9 (G30); P P2-3 |
| D46 (G31) | Session id is an opaque random string generated by core when a session is opened, never reused; the session registry lives in core | C 9 (G31); P P1-1 |
| D47 | Keep the project name `MUD.Telnet`; no rename to `MUD.FrontEnd.Telnet` | P phase 1 |
| D48 | Removing the `Living`/`Player`/`NPC` subclasses (kind-collapse) is not on V2's critical path; phase 2 adds components and behavior lists beside the existing classes; the collapse is an optional separate card, taken later or never | P P2-5 |
| D49 | In-process handler registration stays permanently after phase 4 as a second adapter for core-shipped behaviors; one contract, same `(Module, Type)` lookup, same contract tests against both | A 4.3 |

Confirmed assumptions (from the same file): modules live in this repo as solution projects; every module in the launch entries always runs (no area-header start trigger); non-required modules
start in the background; the `required` flag lives in the launch entry (core config), not the manifest. Implemented in A 1 and M 2, 3.

## Still open or deferred

Taken from the end of the owner's decisions file. These are recorded, not decided; no spec text closes them. Tracked on the Kanban card
"[deferred] MUD V2 redesign: parked decisions and later work".

- Latency numbers.
- Quotas, cascade depth and per-tick budgets.
- Start-up timeout.
- Crash-loop policy.
- Orphaned module data purge.
- Link-dead grace period.
- Creator/web editing surface for behavior entries.
- How module-needed content (temple/desert rooms) gets created.
- Import collision semantics, and whether the validate/plan/apply structure is kept.
- First-run seeding.
- Backups.
- Contract tests.

Separate from the list above, [CORE_API.md section 9](CORE_API.md) ("Gaps found while revising", G1..G31) lists points where the decisions leave an implementation choice that this revision did not make. Those are
proposals with options for the owner; answering them does not change any decision here. Decided so far: G1, G17, G22, G24, G29, G30, G31 (D40-D46). Still undecided: G2-G16, G18-G21, G23, G25-G28; each is decided when the phase that needs it is cut into cards.
