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

Convention: these are lightweight living documents — update the relevant
file when the behavior they describe changes.
