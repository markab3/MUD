# Design Principles and Direction

Standing intent for the project. Individual features are specified in their own
files (for example `ROOM_FILES.md` once written); this document records the
reasoning that should guide any change.

## 1. Everything is persistent and changeable

- Traditional LPMuds recycle objects when nobody is around and re-initialize
  rooms from code on entry. This project deliberately does not.
- An item dropped in a room stays there until someone moves it. A room altered
  in-game (for example by powerful magic) is permanently altered.
- No gameplay state may exist only in memory. Any mutation of a room, item,
  NPC or player must reach the database, immediately or through a queue that is
  guaranteed to flush on shutdown.
- The mudlib is mostly data in the database, not code. A new C# subclass is the
  exception, used only for behavior that cannot be expressed as data.

## 2. Definition versus state

- Authored **definition** (descriptions, exits, features, property
  configuration) is separate from runtime **state** (items present, cooldowns,
  occupants).
- Tooling that writes definitions (importers, generators, editors) must never
  overwrite state.
- A flag recording that a room was changed in-game after import is planned so
  later imports do not silently discard in-world changes. Not yet implemented.

## 3. Identity

- Entity ids are strings. Authored rooms use a human-readable key as the id
  (`castle.throne_room`). Entities without an authored key get a generated
  ObjectId string.
- All references (exits, player location, start room) use the same string id.
- No data exists to migrate: no database instance is live and there are no
  players.
- The hardcoded ObjectIds in `World` and `BuildCommand` are to be replaced by
  configuration naming a room key.

## 4. Content pipeline

- World content is authored as YAML files, one per area, kept in the
  repository. A fresh database is rebuilt by importing them.
- Import is an idempotent, field-level upsert by key.
- Every source feeds the same pipeline: hand-written files, map generators,
  in-game `export`, and a possible future web back end. Generators emit the same
  YAML the importer reads.
- The `terraform` command was found clunky and is not a primary world-building
  method. A web page for adding and updating rooms is a possible future
  direction and should call the same upsert.
- YAML was chosen over Markdown because the importer needs structured fields;
  long descriptions use YAML block scalars.

## 5. Infrastructure status

- The previous MongoDB instance (free tier) expired. A new instance or a local
  one is required before the server can run. Connection details must come from
  configuration or environment, not source.
- The repository holds secrets in source (see SPEC.md known issues). They should
  be rotated and removed; how to treat git history is the maintainer's decision.

## 6. Repository workflow

- `master` is never committed or pushed to.
- `hermes` is the integration branch for agent-produced work, cut from master.
  The maintainer merges into it by pull request. Direct commits there are
  allowed.
- Larger work is done on feature branches cut from `hermes`. A branch that
  depends on an unmerged branch is cut from that branch.
- Branch names contain no slashes (`roomkeys`, not `feature/roomkeys`).
- Commit messages describe what changed. No attribution lines, tool names or
  co-author trailers.
- Agent tasks run in per-task git worktrees under `.worktrees/` (ignored by
  git), not in a shared working copy.
- Work is queued as blocked Kanban tasks and released deliberately to control
  usage costs.
