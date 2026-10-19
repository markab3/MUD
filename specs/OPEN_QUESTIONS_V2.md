# Open questions for ARCHITECTURE_V2 (owner decisions needed before any code)

Part of [ARCHITECTURE_V2.md](ARCHITECTURE_V2.md). Nothing below is decided. Each question has background (written for a
skilled developer new to IPC and event-driven design), options, a recommendation, and the consequence of each option. The
design documents assume the recommended option where they reference a Q-number; if you choose otherwise, the dependent
sections need a revision pass (noted per question).

41 questions in 9 groups. Suggested order: answer groups 1 and 3 first (they shape everything), then the rest in any order.
Within a group the first questions matter most.

| Group | Questions | Count |
|---|---|---|
| 1. Architecture | Q1-Q4 | 4 |
| 2. Entity model | Q5-Q10 | 6 |
| 3. Events | Q11-Q16 | 6 |
| 4. IPC | Q17-Q23 | 7 |
| 5. Front ends | Q24-Q27 | 4 |
| 6. Lifecycle | Q28-Q31 | 4 |
| 7. Persistence | Q32-Q35 | 4 |
| 8. Security | Q36-Q37 | 2 |
| 9. Migration and scope | Q38-Q41 | 4 |

Glossary used below. **Pre-event**: core pauses an action, asks modules whether to allow, veto or change it, then continues.
**Post-event**: core tells modules something already happened. **Park**: hold one player's action while a reply is awaited, without
stopping the game loop. **Overlay**: a removable, module-owned layer on an entity (CORE_API.md 4.2).

---

## Group 1: Architecture

### Q1. Is telnet a separate process in the first cut?

Background: "Front end" can mean a separate project compiled into the same executable, or a separate OS process talking over the
UDS. The first is almost free and proves the boundary; the second is what the end state needs for isolation and for learning IPC.
Options:
- (a) **Project first, process later** (phases 1 then 7). Recommended. Consequence: game stays one deployable until phase 7; telnet bugs can still crash core during phases 1-6; phase 7 is optional/cuttable.
- (b) Separate process from the start (merge phases 1 and 7). Consequence: the session RPC and the reconnect/restart semantics (Q25, Q26) must be done before anything else works; phase 1 grows from M to L; no deployable intermediate where telnet is in-process.
- (c) Never separate (telnet stays a project in core's process). Consequence: smaller scope; requirement 1 is met structurally but not by process; the web front end still uses gRPC, so two styles coexist.
Depends: ARCHITECTURE_V2 7.3 and phase plan 8.

### Q2. Synchronous or asynchronous hooks: may a pre-event hold up a command?

Background: Players expect `north` to feel instant, and the game loop must never block (GAME_LOOP.md rule 6). A synchronous hook (the
game thread waits for the module) is simplest to reason about but a hung module freezes everyone for the timeout. The design's
answer is to **park the one actor** and let the loop continue (CORE_API 2.2) at the cost of a re-validation step before commit.
Options:
- (a) **Park the actor, loop never blocks** (recommended). Consequence: more complexity in core (parked state, re-validation, `stop` handling); a slow module only slows its own commands.
- (b) Block the game thread up to a small deadline (e.g. 20 ms). Consequence: simpler code and no re-validation; worst case `deadline x chain length` of frozen world per move through a trapped room; a hung module becomes a server-wide stutter. Violates the spirit of GAME_LOOP.md.
- (c) No pre-events at all; modules react only after the fact (post-events plus compensating actions). Consequence: much simpler; traps cannot veto or redirect (the pit-trap example B would have to teleport the player after arrival).
Depends: CORE_API 2, MODULES 8. Choosing (b) or (c) simplifies phase 3 considerably.

### Q3. Module language: C# only, or language-neutral?

Background: gRPC and protobuf have generators for most languages, so the wire contract is language-neutral by default. The cost is
in supporting it: examples, a test harness, documentation, and not letting C#-only conveniences leak into the contract.
Options:
- (a) **C# only supported; contract stays neutral** (recommended). Consequence: one SDK/harness in C#; nothing prevents a Python module later but it would be unsupported.
- (b) Explicitly language-neutral. Consequence: need conformance tests and a reference non-C# module (e.g. Python) to prove it; extra work in phase 4.
- (c) C# only and ship modules as assemblies in core. Consequence: contradicts requirement 4 (separate processes); not offered.
Depends: CORE_API 8, MODULES 11.

### Q4. Allow built-in "in-process modules" using the same contract?

Background: The design defines the module contract as an interface with two adapters (gRPC and in-process). In-process modules run on the
game thread with no IPC, so they are fast and cannot disconnect, but they are not isolated and cannot be unloaded safely.
Options:
- (a) **Yes, for built-in behaviours (search, bleeding), not for example modules** (recommended). Consequence: phase 3 can land before the gRPC work; extra adapter to maintain; the shared contract tests keep them honest.
- (b) No: everything behavioural is out-of-process. Consequence: phase 3 has no real consumer until phase 4; search/bleeding must wait or stay hard-coded.
- (c) Yes, for any module the owner chooses. Consequence: weakens the isolation story; useful for performance-critical modules.
Depends: ARCHITECTURE_V2 4.3, phase 3.

---

## Group 2: Entity model

### Q5. Module-owned versus core-owned state

Background: "Who owns the data?" has two parts: who is allowed to change it, and who stores it. Owning means the owner decides its schema and
rules; the other party only transports it. Core-owned components (Description, Location...) are typed and enforced by core. For module data, one
extreme is "everything on entities belongs to core, modules just ask"; the other is "modules own their data and core only stores opaque blobs".
Options:
- (a) **Modules own their component namespaces (`desert.*`); core stores and returns them but only the owner module may write them; core components are never writable by modules (overlays instead)** (recommended). Consequence: clean separation, modules evolve alone; core cannot enforce module invariants (only optional JSON Schema).
- (b) Modules may also write core components (e.g. `Description`) with capability. Consequence: simple for authors; multi-writer conflicts return (web edit vs storm vs import) and "who restores" gets unanswerable.
- (c) Core owns everything including module schemas (typed registration in core). Consequence: strongest validation; every module change needs a core release.
Depends: ARCHITECTURE_V2 5.4; CORE_API 4.

### Q6. Typed versus schemaless component data

Background: Typed means a C# class per component with compile-time checking; schemaless means a bag of fields (protobuf `Struct` / BSON
document) that core stores without knowing its shape. Typed is safer; schemaless lets modules add fields without touching core.
Options:
- (a) **Typed for core components, schemaless with optional JSON-Schema validation for module components, equality queries on top-level fields only** (recommended). Consequence: two paths in core; a typo in a module field is a runtime bug unless the schema catches it.
- (b) All schemaless, including core. Consequence: loses the compile-time safety that exists today (entities are C# classes); renderers and matchers get stringly typed.
- (c) All typed, modules ship generated typed components. Consequence: tight, but needs a code-generation and registration pipeline (large) and core must load module-defined types.
Depends: ARCHITECTURE_V2 5.3; query capabilities in CORE_API 3.1.

### Q7. Who persists module state?

Background: Requirement 2 says core is the only process that talks to MongoDB, and DESIGN_PRINCIPLES 1 says no gameplay state may exist only in memory. A
module's "private" state (the storm record, schedule) is not attached to an entity.
Options:
- (a) **Core persists it in a `module_state` collection through `PutModuleState`; entity-attached data is component data** (recommended). Consequence: module is effectively stateless across restarts; a size limit and a core write path are needed; each state write costs a request.
- (b) Module keeps private state in memory and rebuilds on start. Consequence: simple and fast; a crash loses a storm; contradicts "everything is persistent" for anything player-visible.
- (c) Module has its own files or database. Consequence: violates requirement 2; more things to back up and keep consistent with core.
Depends: ARCHITECTURE_V2 5.4, MODULES 7.1.

### Q8. Are features embedded in rooms or separate entities?

Background: Today `Feature` objects are a list inside the room document with no id (identity is the `short` string, ROOM_FILES 4.4). The
temple lever is a feature that needs a module component, a version, and addressing in `command.invoke` (`matched_entity`).
Options:
- (a) **Keep features embedded but give each a stable id (and allow components on it)** (recommended). Consequence: minimal migration of the YAML format (`id` field, like `properties[].id`); components live inside the room document, so a feature write snapshots the room.
- (b) Make features first-class entities in their own collection, located in a room. Consequence: uniform model (items, features, NPCs all alike); bigger migration of import/export and the room editor; more documents.
- (c) Keep features inert; module components may only attach to rooms and items. Consequence: the lever must be an item (can be picked up) or a room component; limits builders.
Depends: ARCHITECTURE_V2 5.1; ROOM_FILES 4.4, 6.2.

### Q9. How do existing areas bind to modules?

Background: When the desert area loads core must know to start the `desert` module and which rooms it cares about. Binding can be on the area, on
individual entities, or both.
Options:
- (a) **Both: the area YAML header lists `modules: [desert]` (start trigger), and entities carry `components:` entries naming the module namespace (what to act on)** (recommended). Consequence: importer validates names against installed manifests; the YAML format changes (replaces `properties/behavior` in ROOM_FILES 4.5, 7).
- (b) Only the module manifest says `areas: [desert]` and the module decides which rooms matter. Consequence: no YAML change; the module must query to decide; rooms cannot carry per-room config.
- (c) Only entity components; modules start when any entity with their component loads. Consequence: no area-level declaration; harder to see at a glance which areas need which modules.
Depends: MODULES 1, 2, 7; ROOM_FILES revision.

### Q10. Existing stored data: migrate or start clean?

Background: DESIGN_PRINCIPLES 3 says no live database or players exist ("No data to migrate"). Changing `ExtendedProperties` and subclasses to components changes the stored shape.
Options:
- (a) **Confirm: no live data; rebuild from YAML and re-seed accounts** (recommended if true). Consequence: no migration code; dual-read in phase 2 can be dropped.
- (b) There is data worth keeping (players). Consequence: write a one-time migration (like `ObjectIdMigration`) and keep dual-read until done; phase 2 grows.
Please confirm which is true today. Depends: phase 2 risk.

---

## Group 3: Events

### Q11. Fail open or fail closed, per hook

Background: When a module that subscribed to a pre-event is down or too slow, core must either proceed without it (fail open) or refuse the action
(fail closed). Open keeps the game playable; closed preserves a rule.
Options:
- (a) **Per-subscription policy declared in the manifest with core defaults by event class: flavour = open, rule enforcement = closed, module verb = command fails** (recommended; CORE_API 2.5). Consequence: authors must choose consciously; an owner policy file can bound what modules may claim.
- (b) Globally fail open. Consequence: simplest; enforcement hooks (payments, access) cannot be trusted.
- (c) Globally fail closed. Consequence: a crashed trap module blocks movement into half the dungeon.
Specific to example B: traps open; confirm or choose otherwise per module.

### Q12. Ordering across modules and conflicting changes

Background: Several modules may subscribe to the same pre-event. They need a deterministic order, and a rule for when two both change the same thing.
Options:
- (a) **Priority number (lower first), tie by module name; later handlers see earlier modifications; last MODIFY wins and each is logged** (recommended). Consequence: predictable; order is an owner-managed number space.
- (b) First MODIFY wins, later ones may only veto. Consequence: no cascades; simpler reasoning.
- (c) Conflict is a DENY. Consequence: safest; makes some legitimate combinations impossible.
- (d) Parallel veto-only handlers (no MODIFY). Consequence: latency is the max not the sum; no composition of changes.
Post-events have no cross-module ordering in any option.

### Q13. Which events may be pre-events?

Background: Every pre-event is a place where a module can slow or block the game. A small set keeps core simple and fast.
Options:
- (a) **Start with `entity.move`, `item.move`, `command.before`, `command.invoke` (verb), and modify-only `entity.describe`; everything else post** (recommended).
- (b) Only `entity.move` and `command.invoke` (smallest set that serves examples A and B; overlays replace `entity.describe`).
- (c) Many more (entity.destroy, login, damage...). Consequence: larger surface to defend and test in phase 3.

### Q14. Delivery guarantee for post-events

Background: "At-most-once" means an event can be lost (module restarting, queue overflow) but never repeated; "at-least-once" means never lost but may be duplicated and
needs persistent queues and acknowledgements. Exactly-once is not practical.
Options:
- (a) **At-most-once with sequence numbers, a short replay ring (1000 events / 60 s), and `ResyncRequired`; events are hints, state is queryable** (recommended). Consequence: modules must reconcile from state on start; no persistent queue in core.
- (b) At-least-once with a persisted per-module queue. Consequence: no lost events across module crash; extra storage, acks, dedupe in every handler; much bigger phase 4.
- (c) Best effort without sequence numbers. Consequence: simplest; modules cannot detect loss.

### Q15. Concurrent entry into a trapped room: what happens to the second parked action?

Background: Two players move into the same trapped room; both pre-events carry the same component version. The first commit changes the
version, so the second action's `on_commit` precondition fails. Core must either re-ask the module or abort the action.
Options:
- (a) **Re-emit the pre-event once with fresh data; if it fails again, abort with a generic "Something prevents you."** (recommended). Consequence: correct one-shot semantics; one retry bound.
- (b) Abort immediately with an error. Consequence: simplest; player sees a spurious failure.
- (c) Serialise pre-events per subject entity (queue the second until the first commits). Consequence: no retries needed; per-subject queues add state; same parked-actor mechanism.

### Q16. Latency budget numbers

Background: How long may a pre-event handler take? Nothing is measured yet. A UDS round trip locally should be small, but handler logic and a cold .NET
process are not.
Options:
- (a) **Handler 50 ms, chain 150 ms, at most 3 pre-subscribers per event name; `entity.describe` 20 ms** (recommended; benchmark in phase 4 first). Consequence: a good starting point; tune after measurement.
- (b) Tighter (20/60 ms). Consequence: snappier worst case; false timeouts under load or GC pauses.
- (c) Looser (200/500 ms). Consequence: tolerant of slow modules; sluggish commands on a bad day.

---

## Group 4: IPC

### Q17. Where does the contract (.proto) live and how is it built?

Background: Both core and every module compile the same `.proto` files. They need one source of truth and a build step that generates C#.
Options:
- (a) **A `contracts/` directory in this repo with a small `MUD.Contracts` project (Grpc.Tools codegen), referenced by core, front ends and C# modules; modules outside the repo copy the files or reference the package** (recommended). Consequence: one repo, simple; module authors need the repo or a published package.
- (b) Contracts published as a NuGet package. Consequence: clean for external modules; release process and versioning overhead.
- (c) Contracts duplicated per project. Consequence: drifts; not recommended.

### Q18. One bidirectional stream per module, or per-call RPC?

Background: With a bidirectional stream the module dials core once and both sides send typed messages over it for the module's whole life. With
per-call RPC each event and each request is its own call, requiring the module to also run a gRPC server. Evaluation in CORE_API 7.1.
Options:
- (a) **One bidirectional stream per module** (recommended). Consequence: lifecycle equals connection; one log shows the whole conversation; request/response correlation is hand-written; single-writer queue required.
- (b) Server-stream for events plus unary calls for module requests (option D in CORE_API 7.1). Consequence: gRPC handles deadlines and status for requests; two things to keep alive; replies to pre-events become unary calls. A strong second choice.
- (c) Per-call both directions, module hosts its own socket. Consequence: most conventional gRPC, most moving parts (two sockets per module, discovery, supervision).
Depends: CORE_API 7.

### Q19. One socket or two?

Background: A socket file is the "address". Putting modules and front ends/admin on different sockets means a module never even sees the admin surface, but it doubles the files to manage.
Options:
- (a) **Two: `modules.sock` and `frontends.sock`** (recommended). Consequence: a clear separation in logs and tooling; cosmetic against a same-user attacker.
- (b) One socket, services separated by identity/capability only. Consequence: simpler operations; admin RPCs exposed to a buggy module unless every call is guarded.

### Q20. Module authentication

Background: A same-user process can connect to a 0600 socket. Core wants to know the connecting process is the one it spawned.
Options:
- (a) **Random per-spawn token in the child's environment, checked in `Hello`; dev tokens via an admin tool** (recommended). Consequence: simple; readable by other same-user processes via `/proc/<pid>/environ`.
- (b) Token plus `SO_PEERCRED` PID check. Consequence: stronger identification of accidents; needs a .NET mechanism that is unverified, possibly P/Invoke.
- (c) Rely only on the socket permissions. Consequence: no module identity; a hand-started stray module could impersonate another.

### Q21. Quotas and timer limits: numbers

Background: Limits stop a buggy module from hurting the game. The proposed values (CORE_API 5) are guesses.
Options:
- (a) **Adopt the CORE_API 5 table as defaults, in a config file, adjust after measuring** (recommended).
- (b) Stricter defaults and raise per module on request.
- (c) No quotas in the first cut (rely on supervision). Consequence: a loop in a module can saturate the game thread's module share and spam players.

### Q22. Contract test tooling

Background: Breaking a wire contract silently is easy; tests catch it.
Options:
- (a) **Golden-file round-trip tests plus a committed descriptor baseline compared in a unit test, no new CLI tool** (recommended; the exact check is unverified and is a small spike).
- (b) Adopt `buf` (breaking-change detection). Consequence: stronger checks; new tool in CI.
- (c) No automated checks until phase 4 settles. Consequence: risk of accidental renumbering.

### Q23. How the web front end proves who the actor is

Background: Every admin call is "on behalf of player X with role R". Core must know X is authenticated.
Options:
- (a) **Core issues an opaque session token at `Authenticate`; the web keeps it in its cookie and passes it on each call; core re-checks the role on each call** (recommended). Consequence: the web cannot forge an actor; sessions can be revoked by core (retirement, demotion take effect immediately, unlike the cookie role that is "fixed at login" today).
- (b) The web asserts `actor_player_id` and trusts itself. Consequence: simplest; any bug in the web is an authority bug; fine for same-user localhost.
- (c) Signed token verified by core with a shared secret. Consequence: no state in core; revocation is harder.

---

## Group 5: Front ends

### Q24. Output wire format

Background: Core produces output text; the front end turns it into ANSI or HTML. Today the text carries `%^Red%^` style markers.
Options:
- (a) **Keep the existing markup string plus a `channel` tag** (recommended first cut). Consequence: tiny change; the web needs a markup parser.
- (b) Structured spans (`{text, style}`), message kinds. Consequence: richer, helps the web; more schema; every command's output code is affected.
- (c) Core emits ANSI. Consequence: contradicts requirement 1 and the web.

### Q25. What happens to online players when a front end restarts?

Background: Today losing a connection logs the player out (net-dead handling deferred, GAME_LOOP Q14). If telnet becomes its own process, restarting it drops everyone.
Options:
- (a) **Same as today: stream loss = disconnect = logout** (recommended for the first cut). Consequence: simple; every front-end restart kicks everyone; characters are saved.
- (b) A `linkdead` grace period (e.g. 60 s): player stays in the world, output is discarded, reconnect resumes. Consequence: nicer; new states for a player in the world (and risk that a linkdead player is attacked); the owner deferred this earlier.
- (c) Hand-over of sockets across restarts (systemd socket activation or fd passing). Consequence: no drops for telnet at all; considerable complexity.

### Q26. What happens to connected clients when core restarts?

Options:
- (a) **Front end closes connections with a "world is restarting" message and clients reconnect** (recommended; matches today's restart behaviour).
- (b) Front end holds TCP connections open, shows "reconnecting..." and returns to a login prompt when core returns. Consequence: smoother; the front end holds per-connection state; still a logout in the game.

### Q27. Is `Rev` (optimistic concurrency) kept for web edits?

Background: `Rev` exists so the website and the game do not overwrite each other (WEB_EDITING 8). With core as the only writer, that cross-process conflict is gone; but two
human editors in two browser tabs can still overwrite each other.
Options:
- (a) **Keep `Rev` for web and creator edits as a compare-and-set token** (recommended). Consequence: the web UX for conflicts (WEB_EDITING 8, tests in `MUD.Web.Tests`) stays; one small field; polling is removed.
- (b) Drop `Rev`; last write wins (audit has the history). Consequence: simpler; silent overwrites between human editors.

---

## Group 6: Lifecycle

### Q28. Core spawns child processes, or asks systemd?

Background: Requirement 4 says core starts modules. It can fork/exec them itself, or ask systemd to start templated user units. Comparison in MODULES 9.
Options:
- (a) **Core spawns and supervises children** (recommended). Consequence: you write the supervisor (educational); limited per-module resource isolation.
- (b) Core asks systemd (`systemctl --user start planar-realms-module@desert`). Consequence: systemd gives restart policy, limits and sandboxing for free; core depends on systemd; slower dev loop.
- (c) Support both behind the manifest (`launcher: child | systemd`). Consequence: most flexible; two code paths to test.

### Q29. Module upgrade and downgrade semantics for stored data

Options:
- (a) **Each module version must read all older data or refuse to start; core refuses writes that lower a component's stored `schema_version` unless allowed** (recommended). Consequence: safe default; migrations are the module's responsibility.
- (b) Core tracks versions only for information. Consequence: simpler core; a rollback can silently corrupt data.
- (c) Core runs registered migrations. Consequence: central control; core must know about module schemas (against Q6 recommendation).

### Q30. Does core wait for modules before opening the game?

Options:
- (a) **No; modules start in the background and reconcile when ready** (recommended). Consequence: game available even if a module is broken; players may briefly see unmodified rooms.
- (b) A `required: true` flag per module makes core hold the front-end gate until Ready. Consequence: consistent start; a broken required module blocks the whole game.

### Q31. Capability grants: partial grant or refuse to start?

Background: The manifest requests capabilities; core's grants file decides. If the module asks for more than granted, core can start it with fewer powers or refuse.
Options:
- (a) **Start with the granted subset and log a clear warning** (recommended for development ease). Consequence: a module may misbehave when missing a capability; error is explicit at request time.
- (b) Refuse to start. Consequence: no half-working modules; stricter, slower dev loop.

---

## Group 7: Persistence

### Q32. Out-of-band tools such as `seed-admin.sh`

Background: `scripts/seed-admin.sh` writes MongoDB through `mongosh`. Under V2 core is the only writer.
Options:
- (a) **Document "run only with core stopped" now; add a `core-admin` CLI over the UDS later** (recommended). Consequence: no new code now; a rule the owner must remember.
- (b) Build the `core-admin` CLI (EnsureAdmin etc.) in phase 6. Consequence: tooling is safe with core running; more work.
- (c) Leave direct Mongo writes allowed for emergency tools. Consequence: breaks the invariant; running core may overwrite or be surprised.

### Q33. Backup/restore method

Options:
- (a) **`mongodump` after an admin `Flush` (drains the persistence worker) plus the spool directory** (recommended). Consequence: simple, consistent because core is sole writer.
- (b) Stop core, dump, start. Consequence: simplest guarantee; downtime.
- (c) Core exports its own snapshot format. Consequence: portable; new code.

### Q34. Audit granularity for module changes

Background: The existing audit writes one row per changed field for web/creator edits. A module changing a room every 10 s would flood it.
Options:
- (a) **Per-field audit for web and creator edits as today; module mutations audit one row per `ApplyBatch` (entity, component, summary) with module name and trace id; configurable per component** (recommended).
- (b) Per-field audit for everything. Consequence: complete trail; large and noisy.
- (c) Audit modules only for core components (none are writable) and rely on logs. Consequence: minimal; no durable record of module effects.

### Q35. Orphaned module data policy

Background: Components belonging to a module that is no longer installed stay in the database as opaque data. Should they stay forever?
Options:
- (a) **Keep until an explicit, audited `modules purge <name>`** (recommended).
- (b) Auto-delete after N days uninstalled. Consequence: tidy; risk of surprising data loss.
- (c) Delete at uninstall. Consequence: violates "unload never destroys world data".

---

## Group 8: Security

### Q36. Capability grants: who edits them and where do they live?

Options:
- (a) **`~/.config/mud/module-grants.yaml`, owner-edited, mode 600, read by core only** (recommended).
- (b) Grants stored in MongoDB and editable by an Admin through the web. Consequence: convenient; then the web (or an admin account) can raise a module's powers.
- (c) Grants only in the module manifest. Consequence: a module grants itself everything; no meaning.

### Q37. Trust stance for modules

Background: All processes run as the same OS user (see ARCHITECTURE_V2 7.5 for what this does and does not protect).
Options:
- (a) **Owner's own modules only, trust by review; capability checks are a seatbelt against bugs** (recommended).
- (b) Plan for untrusted modules later: separate OS user or systemd sandboxing per module, ACL'd socket. Consequence: shapes socket location and spawn mechanism now (pushes toward Q28 option b).
- (c) Untrusted modules from the start. Consequence: significantly larger scope; not recommended.

---

## Group 9: Migration and scope

### Q38. How much of the plan is in scope?

Background: The seven phases total roughly 28 cards (ARCHITECTURE_V2 8). Phase 7 (telnet process) and parts of phase 2 (removing the `Living/Player/NPC`
subclasses) are optional for the stated goals.
Options:
- (a) **Phases 1-6 in scope; phase 7 and the full removal of subclasses optional later** (recommended).
- (b) All seven. Consequence: complete; longest.
- (c) Stop after phase 5 (modules working, web still on Mongo). Consequence: shortest route to the module learning goal; requirement 2 (core as sole Mongo client) is not met until phase 6.

### Q39. Sequencing of phases 1 and 2

Background: Both touch `Player.cs`, `Room.cs`, `World.cs`, `GameObject.cs`; parallel work causes merge conflicts.
Options:
- (a) **Sequential: 1 then 2** (recommended). 
- (b) Parallel with an agreed ownership split. Consequence: faster; conflicts and rework risk.

### Q40. Web move before modules?

Background: Phase 6 (web on the core API) needs only phase 1's API, and removes the Rev/poll machinery; modules need 2-4.
Options:
- (a) **Follow the suggested order (modules first, then web)** (recommended if modules are the learning priority).
- (b) Do the web move (6) right after phase 1. Consequence: core becomes sole Mongo client sooner and the poll code disappears early; the learning goal (modules) is delayed; roles via `ExtendedProperties` must be handled by whichever phase comes first.

### Q41. Spikes before committing

Background: Several claims are unverified: Kestrel on two UDS endpoints, `SO_PEERCRED` access in .NET, `Grpc.Tools` codegen in CI, UDS round-trip latency, adding the ASP.NET Core framework to the console host `MUD.Main`.
Options:
- (a) **Run a one-card throw-away spike before phase 4 that proves these and records measurements** (recommended).
- (b) Fold the verification into phase 4's first card. Consequence: no extra card; risk that a failed assumption reshapes phase 4 mid-way.

---

## Additional notes for the owner (not questions)

- Existing issues found while reading the code are listed in ARCHITECTURE_V2 section 11 and recommended as backlog items; this task filed none.
- The card said 44 command classes; the tree has 33 implementations plus 6 empty files (ARCHITECTURE_V2 2.3). Tell me if there is another branch with the rest.
