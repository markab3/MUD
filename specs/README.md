# Planar Realms — Specs

Initial draft generated 2026-09-21 from a full codebase analysis (base
commit `ce8f7b9`).

| File | Contents |
|---|---|
| [BRANCHING_POLICY.md](BRANCHING_POLICY.md) | **Mandatory** branch rule: all changes on non-master branches; never commit or push to master; enforcement via CI/CD + contributor guidelines |
| [DESIGN_PRINCIPLES.md](DESIGN_PRINCIPLES.md) | Standing design intent: total persistence, definition vs state, string ids, YAML content pipeline, branch and commit conventions |
| [SPEC.md](SPEC.md) | Project purpose, scope, tech stack, known issues, open questions |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Module layout, dependency graph, boot sequence, DI, game loop, data flow, domain model, persistence |
| [API.md](API.md) | Key interfaces: ICommand, telnet Server/Client, IRepository, ITerminalHandler, entity extension points, World |
| [CONFIGURATION.md](CONFIGURATION.md) | Runtime config (environment, appsettings), build, local dev |
| [DEPLOYMENT.md](DEPLOYMENT.md) | systemd user unit, `scripts/deploy.sh`, rollback, triggering deploys |
| [WEB.md](WEB.md) | MUD.Web site: config, routes, cookie auth model, plaintext password caveat |
| [ROOM_FILES.md](ROOM_FILES.md) | Area YAML format and import semantics. **Partly superseded by V2 (D39)**: sections 4.5, 6, 7, 9 obsolete; YAML is an optional one-off import/export format |
| [WEB_EDITING.md](WEB_EDITING.md) | Web editing vs the live game: allow-lists, roles, audit. **Rev/poll parts superseded by V2 (D30)** |
| [ARCHITECTURE_V2.md](ARCHITECTURE_V2.md) | V2 redesign revised to the owner's decisions D1..D39: core / front-end split, composition via entity behavior lists, process-per-module over gRPC on Unix sockets. Current-state inventory, target architecture, entity and behavior model, phase summary |
| [CORE_API.md](CORE_API.md) | V2: event model (Before/After, Allow/Deny with emitted events), front-end-neutral core API, module calls, gRPC over two UDS sockets (message sketches, .NET 8 notes), and "Gaps found while revising" (G1..G31) for the owner |
| [MODULES.md](MODULES.md) | V2: manifest, launch entries and grants, lifecycle, supervision, dormant behaviors, worked examples A (desert sandstorm) and B (temple lever and traps), failure modes, testing |
| [V2_PHASES.md](V2_PHASES.md) | V2 phase plan: the seven phases, the pre-phase-4 spike, deliverables, risks, sizes and the proposed card split (phase 1 in implementable detail) |
| [OPEN_QUESTIONS_V2.md](OPEN_QUESTIONS_V2.md) | V2 decisions record: D1..D39 with pointers to the implementing sections, and "Still open or deferred" (name kept for link stability) |
| [V2_SPIKE_RESULTS.md](V2_SPIKE_RESULTS.md) | Throwaway spike results (D38): gRPC over Unix domain sockets, Kestrel on two UDS endpoints, Grpc.Tools codegen, round-trip latency, process supervision |

Convention: these are lightweight living documents — update the relevant
file when the behavior they describe changes.
