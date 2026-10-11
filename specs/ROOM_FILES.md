# Planar Realms — Room Content Files and Import Semantics

Status: DRAFT specification. Docs only; nothing here is implemented.
Task: t_2c9f6235. Branch: roomspec (based on `hermes`).

Conventions in this document:
- MUST / SHOULD / MAY have their RFC 2119 meaning.
- "Today" describes the code at base commit cf8b7df, with file references.
- "Proposed" describes what this spec requires of future work.

## 1. Purpose and non-goals

Purpose: define one YAML file format (one file per area) for authored room
content, and the import ("upsert") rules that load it into MongoDB without
destroying runtime state or in-game edits.

Non-goals (separate work, not specified here): the importer code, the
`locally modified` implementation, a generator, a web backend, items/NPC
files. Those are only constrained where this spec says so.

## 2. Grounding: what the code does today

Facts established by reading the code:

- `Room : GameObject : Entity` (MUD.Core/GameObjects/Room.cs, GameObject.cs,
  MUD.Data/Entity.cs).
- Persisted-looking members of Room: `Items` (List<InventoryItem>),
  `Features` (List<Feature>), `Exits` (List<Exit>). Inherited from
  GameObject: `ShortDescription`, `LongDescription`, `Plurals`, `Adjectives`,
  `Handles`, `ExtendedProperties` (List<ExtendedProperty>). Inherited from
  Entity: `Id`.
- Non-persisted Room members: `Occupants`/`_occupants`, `RoomCommandSource`
  (`[BsonIgnore]`), and the private fields. Entity has `[BsonIgnoreExtraElements
  (Inherited = true)]`, so unknown stored fields are tolerated on read.
- `Exit` has only `Name` and `DestinationId`. `DestinationId` is annotated
  `[BsonRepresentation(BsonType.ObjectId)]`. `Entity.Id` carries the same
  annotation.
- `Feature : GameObject` is an empty subclass. Features have their own
  descriptions, handles and extended properties, but no key.
- `ExtendedProperty : Entity` (MUD.Core/Properties/ExtendedProperty.cs) has a
  public field `objectContext` and abstract `InitializeProperty()` /
  `RemoveProperty()`.
- `BasicSearchProperty` (Properties/RoomProperties) has public fields
  `SearchTarget` (string), `Reward` (InventoryItem), `RefreshInterval` (int,
  milliseconds), and a private `_nextReward` (DateTime) used as the cooldown.
  `_nextReward` is private, so it is not persisted today, and it is runtime
  state.
- `MUD.PlanarRealms/.../TowerOfAellioc_1.cs` is a `Room` subclass whose
  constructor adds `new SearchProperty(lambda)`. The lambda says that
  searching "table" gives "a really cool sword", and anything else gives
  "nothing of any value". No `SearchProperty` type exists in
  MUD.Core/Properties (only `BasicSearchProperty`), and the file's namespace
  (`...Planes.Neutral.Rooms`) does not match its folder. So this example is
  already stale relative to the property model.
- `Repository<T>` (MUD.Data/Repository.cs): `Update` is `ReplaceOne` of the
  whole document, and returns false unless `ModifiedCount == 1` (so an
  unchanged document reads as failure). There is no upsert, no partial
  update, and no bulk operation. It keeps a per-process `_modelCache` of live
  objects keyed by Id; `Update` and `Delete` do not refresh a stale cached
  instance, so after an out-of-band write the cache still holds the old
  object.
- `Room.Save()` inserts when `Id` is blank and replaces otherwise. Creator
  commands (e.g. Terraform) mutate `Exits`, descriptions etc. on live rooms
  and save them. Terraform LINK adds the reverse exit as a separate manual
  step with its own name.
- Nothing today models Area, X, Y or Z.
- Mongo class maps are created through DI (MongoDBClassMapManager), so room
  subclasses resolve their constructors from the container.

Consequences this spec depends on (each needs follow-up implementation work;
see section 14):
1. Human-readable string `_id`s (`castle.throne_room`) conflict with the
   `BsonType.ObjectId` representation on `Entity.Id` and `Exit.DestinationId`.
   Those annotations must be changed to plain string (rooms without an
   authored key then need an explicit generated ObjectId-hex string).
2. Whole-document `ReplaceOne` cannot be used by the importer; it would wipe
   state. Field-level `$set` is required.
3. Live cached `Room` objects must be refreshed after an import.
4. Cooldown state of properties must live somewhere persistable and separate
   from configuration (see 7.4).

## 3. Identity

- Room identity is a string `_id`. For authored rooms `_id` equals the room's
  full key: `<area.prefix>.<local_key>`, e.g. `tower_of_aellioc.entry`.
- Rooms created in-game or without an authored key get a generated ObjectId
  string (24 hex chars). The importer never touches these unless a file
  explicitly names them (it MUST NOT, and SHOULD warn if a full key matches
  the 24-hex pattern).
- Key grammar: lowercase letters, digits, `_`; segments joined by `.`.
  Regex for a local key: `^[a-z0-9_]+$`. Area prefix: `^[a-z0-9_]+(\.[a-z0-9_]+)*$`.
  Full key = prefix + "." + local key. Keys are case-sensitive in storage but
  the grammar forbids uppercase, so this is moot for authored data.
- Keys are permanent. Renaming a key is a different room (old one becomes an
  orphan, see 9.5). There is no rename operation in the file format.
- Exits within a file MAY use a local key (`entry`) or a fully qualified key
  (`castle.gate`) to reference another area. Resolution: if the value
  contains a `.` it is a full key; otherwise it is prefixed with this file's
  area prefix. (Local keys cannot contain `.`, so this is unambiguous.)

## 4. File layout

- One file per area, UTF-8, extension `.yaml`. Suggested location:
  `MUD.PlanarRealms/Areas/<prefix>.yaml` (location is not normative).
- Top level has exactly two keys: `area` and `rooms`. Unknown top-level or
  unknown room-level keys are a validation error (typos must not silently
  drop content). Keys beginning with `x-` are ignored (free-form metadata).
- `schema_version: 1` is REQUIRED in the `area` block.

### 4.1 Area header

```
area:
  schema_version: 1          # required, integer
  prefix: tower_of_aellioc   # required, key prefix (section 3)
  name: "Tower of Aellioc"   # required, display name
  description: "..."         # optional, free text for builders
  defaults:                  # optional, shared defaults applied to each room
    coords:
      z: 0                   # default z (see section 10)
    properties: []           # property configs added to EVERY room (rare)
```

`defaults` rules:
- Only the listed sub-keys are allowed. A default applies to a room only when
  the room omits that field entirely. Lists are not merged: if a room
  specifies `properties`, the default is not used for it. (Deliberate: merging
  rules hide where a value came from.)
- Defaults are resolved at parse time into concrete room values before
  validation and before diffing. The database stores resolved values only;
  it never stores "inherited from default".

### 4.2 Room

```
rooms:
  - key: entry                     # required, local key
    short: "the entrance hall"     # required
    long: "..."                    # required, may be multi-line
    handles: [hall]                # optional (GameObject.Handles)
    adjectives: [grand]            # optional (GameObject.Adjectives)
    plurals: []                    # optional (GameObject.Plurals)
    coords: {x: 0, y: 0, z: 0}     # optional, see section 10
    exits: [...]                   # optional, see 4.3
    features: [...]                # optional, see 4.4
    properties: [...]              # optional, see 4.5
    class: null                    # optional, see section 8
```

Mapping to the model: `short` -> `ShortDescription`, `long` ->
`LongDescription`. `handles`/`adjectives`/`plurals` map one-to-one. A missing
optional list is treated as empty (and so, on import, clears the stored list;
see 6.2). Note `Plurals` has no initializer in GameObject today (null), so
the importer MUST write an explicit empty list, and `IsPluralMatchForString`
needs a null guard — follow-up (section 14).

### 4.3 Exits

```
exits:
  - dir: north                 # required; maps to Exit.Name
    to: great_hall             # required; local or full room key
    reverse: south             # optional; creates/maintains the return exit
  - dir: out
    to: town.gate              # cross-area full key
```

- `dir` is the exit name as typed by the player (`Room.RebuildExitCommands`
  keys commands from `Exit.Name`; it already knows the 10 compass names and
  their short aliases). Within one room, `dir` MUST be unique (case
  insensitive). Today nothing enforces this.
- `to` resolves to `Exit.DestinationId`.
- `reverse` is the name of the exit on the destination room that points back
  here. Semantics: the importer expands it, in memory before validation, into
  an exit on the destination room `{dir: <reverse>, to: <this room's full
  key>}`. Rules:
  - If the destination room is in the same file and ALSO declares an exit
    with the same `dir` back to this room, the two must agree; otherwise
    a conflicting explicit exit is a validation error.
  - If the destination room is in the same file and does not declare it, the
    importer synthesizes it as if it had been written.
  - If the destination is in another area (full key), `reverse` is a
    validation error. A file only has authority over its own rooms; the other
    area's file must declare its own exit. (Avoids one import editing
    rooms owned by another file.)
  - A synthesized reverse exit is attributed to this file; for ownership
    and 6.2 it is treated exactly like an authored exit on the destination
    room.
- There is no shorthand for automatic compass opposites. The author states
  the reverse name; this avoids guessing for non-compass exits ("up the
  ladder" / "down the ladder").
- Doors, locks, hidden flags etc. do not exist on `Exit` today and are
  out of scope. When added they become definition fields of an exit; unknown
  keys are rejected until then.

### 4.4 Features

A feature is a described, matchable thing in a room that is not an item
(table, statue). It is a `Feature : GameObject`.

```
features:
  - short: "a heavy oak table"
    long: "A heavy oak table, scarred by years of use."
    handles: [table]
    adjectives: [oak, heavy]
    plurals: [tables]
    properties: [...]          # optional, same form as 4.5
```

Features have no key, so identity within a room is the `short` string
(case-insensitive, unique within the room; duplicates are a validation
error). Feature order in the file is the stored order.

### 4.5 Extended properties (named, parameterized behaviors)

Extended properties are written as a behavior name plus parameters. The name
resolves through the behavior registry (section 7).

```
properties:
  - behavior: search           # required, registered behavior name
    id: table_search           # required, unique among this owner's properties
    params:                    # behavior-specific, validated by the behavior
      target: table
      refresh_ms: 60000
      reward:
        short: "a really cool sword"
        long: "A sword that is really, truly cool."
        handles: [sword]
        adjectives: [cool]
```

For `search` the params correspond to `BasicSearchProperty`:
`target` -> `SearchTarget`, `refresh_ms` -> `RefreshInterval`, `reward` ->
`Reward` (an InventoryItem definition; fields as in 4.4 minus `properties`).
`id` is new: it is what makes field-level upsert of a list element possible
(see 6.2). It is stored with the property.

Unknown `behavior` or invalid `params` is a validation error.

## 5. Definition versus state

Authoritative classification. The importer writes definition fields only.

Definition (authored; overwritten by import, subject to 6.4 and section 9):
- `ShortDescription`, `LongDescription`, `Handles`, `Adjectives`, `Plurals`
- `Exits` (name, destination)
- `Features` (all their own descriptive fields and their property configs)
- `ExtendedProperties` configuration (behavior, id, params: `SearchTarget`,
  `RefreshInterval`, `Reward` template)
- `Area`, `X`, `Y`, `Z` (section 10), `class` (section 8)
- Import bookkeeping fields (section 9): `source`, `source_hash` — written by
  the importer, never by hand.

State (never touched by import):
- `Items` (everything on the floor, including items dropped by players)
- Property runtime state, e.g. `_nextReward` / cooldown timestamps
- Anything not named in this document (unknown stored fields, thanks to
  `BsonIgnoreExtraElements`; also fields later added by game systems)
- Transient, never persisted: `Occupants`, `RoomCommandSource`

Rule: a field is state unless it appears in the Definition list. New fields
default to state until this table is amended.

## 6. Import semantics

### 6.1 Overview: validate everything, then write

Import runs in three phases. Nothing is written unless phase 1 passes
completely for the whole file.

Phase 1 — Parse and validate (read-only; may read the DB):
1. YAML parses; `schema_version` supported; no unknown keys; types correct.
2. Area prefix valid. Every room `key` matches the grammar.
3. Duplicate keys: no two rooms in the file with the same full key; no
   two features with the same short in a room; no two exits with the same
   `dir` in a room; no two properties with the same `id` per owner. All
   duplicates are reported, not just the first.
4. Defaults resolved; `reverse` expanded.
5. Dangling exits: every exit `to` must resolve to either a room in this
   file, or (full key) a room that already exists in the database. Anything
   else is an error naming room, `dir`, and the missing key.
6. Every `behavior` is registered and its `params` validate. Every `class`
   (section 8) resolves.
7. Cross-file check: a room in this file whose full key is already in the
   database with a different `source` (owned by another file) is an error
   (key collision between files), never an overwrite.
8. All errors are collected and reported together; any error aborts with
   zero writes.

Phase 2 — Plan (read-only): compute per-room diff against the DB:
`insert`, `update` (list the changed definition fields), `unchanged`, and
`orphaned` (section 9.5). A `--dry-run` mode stops here and prints the plan.

Phase 3 — Apply: execute the plan.
- Rooms are written with field-level `$set` of only the changed definition
  fields (and `$setOnInsert` for state defaults, e.g. `Items: []`, when
  inserting). Never `ReplaceOne`. Never `$set` a state field on an existing
  room.
- Order: all rooms are upserted before any live-cache refresh.
- Atomicity: Mongo has no multi-document guarantee without replica-set
  transactions, so the guarantee is "validate everything first" rather than
  "all or nothing". If apply fails midway the run is safe to re-run
  (idempotent) and the error reports which rooms were written. Use a
  transaction when the deployment supports one (open question, section 15).
- After apply, live objects are refreshed (6.5).

### 6.2 Field-level upsert rules

- Idempotent: importing the same file twice produces no changes the second
  time (diff reports all `unchanged`; zero writes).
- Authoritative per field, not per document: a definition field present in
  the file replaces the stored value of that field completely (lists included:
  `Exits`, `Features`, `Handles` etc. are replaced as a whole list in file
  order, subject to the element-merge exception below). A definition field
  omitted from the file is treated as its empty value and cleared. This is
  what lets removing an exit from the file remove it from the world.
- Never touch state. The `$set` document is built only from the definition
  list in section 5.
- Element-level merge exception (needed to protect state inside definition
  lists): `Features` and `ExtendedProperties` elements can carry runtime
  state inside the element (cooldown, later). Matching is by identity —
  feature by `short`, property by `id` — and for a matched element only the
  definition sub-fields are replaced; its state sub-fields are kept.
  Elements in the DB with no match in the file are removed (definition
  removal) unless flagged locally modified (section 9). Elements in the
  file with no match are added with default state.
- Exits carry no state today and are replaced wholesale as a list, except that
  exits flagged locally added (section 9) are retained.
- Unchanged detection compares resolved values; whitespace in `long` is
  compared after normalization (section 11).
- The importer never deletes a room document. Rooms removed from a file
  become orphans (9.5).

### 6.3 Why exits that players added in-game need care

Creator tools (Terraform LINK/UNLINK) change `Exits` directly in the DB.
A strict "file is authoritative" re-import would silently undo those edits.
That is exactly what the locally-modified mechanism in section 9 exists to
prevent. Until it is implemented, the interim rule is in 9.4.

### 6.4 Which wins

Order of authority per definition field, highest first:
1. If the field is flagged locally modified (section 9): the DB value, and
   the importer reports the divergence instead of writing.
2. The file.
There is never a merge of two edits of the same field.

### 6.5 Interaction with the repository and live objects

- The importer needs new repository operations (proposed, not present
  today): `UpsertDefinition(id, setDocument, insertDefaults)` and a bulk
  lookup of existing ids. The existing `Update` (ReplaceOne) MUST NOT be used
  by the importer.
- After a successful write of a room, any `Repository._modelCache` entry for
  that id MUST be refreshed (reload from DB and swap definition fields onto
  the live instance, preserving `Items`, `Occupants` and runtime state) or
  evicted if no one is in the room. A cached stale `Room` that is later
  `Save()`d with `ReplaceOne` would otherwise write old definitions back and
  undo the import.
- Players currently in an updated room stay in it. `RebuildExitCommands()`
  MUST be called after exits change.

## 7. Behavior registry

### 7.1 Principle

Data describes configuration; code supplies behavior. The file never contains
code. A property in a file is `behavior: <name>` plus `params`; the name is
looked up in a registry that maps it to a concrete `ExtendedProperty` type.

### 7.2 Registry contract (proposed)

A behavior registration provides:
- `name` (stable, lowercase, e.g. `search`) — part of the public file format.
- The .NET type to instantiate (an `ExtendedProperty` subclass; must be DI
  resolvable like other entities).
- A param schema/validator: required and optional params, types, ranges,
  producing validation errors for phase 1.
- A `FromParams(params) -> property` factory and the reverse
  `ToParams(property) -> params` (needed for diffing and for generators and
  the web backend to read config back).
- Which of the property's members are definition vs state (so 6.2 element
  merge knows what to keep). For `BasicSearchProperty`: definition =
  `SearchTarget`, `RefreshInterval`, `Reward`; state = next-reward time.

Registration happens by attribute or by explicit table in the content
assembly; discovery piggybacks on the existing assembly scan in
`World.LoadTypes()`. Mechanism is an implementation choice; the name
and param contracts are normative.

### 7.3 Initial behavior set

Only behaviors that exist as classes today are specified:
- `search` -> `BasicSearchProperty` (params in 4.5).
Other existing properties (Bleeding, Creator, Admin) are Living/Player
properties and are not room-file behaviors.

### 7.4 State for property cooldowns

`_nextReward` is private and unpersisted today, so cooldowns reset on every
boot. To keep "state is never touched by import" meaningful once cooldowns are
persisted, property state must be stored apart from its config, e.g. a
`state` sub-document on the property element that the importer never writes.
Exact shape: implementation task. Requirement: import must not reset it.

### 7.5 Behavior that cannot be data: TowerOfAellioc_1

The tower's search lambda has two parts:
1. `table` -> reward "a really cool sword": this IS data. It is the `search`
   behavior with `target: table` and a `reward` (see worked example). No
   code needed.
2. Anything else -> "You search the X but find nothing of any value.": this
   is the default miss message. `BasicSearchProperty.DoSearch` simply returns
   false on a miss; the caller's generic "nothing" message handling replaces
   the lambda branch. If custom miss text per room is wanted, add an optional
   `miss_message` param to `search`. Not required by this spec.

True arbitrary code (puzzles, scripted events) cannot be expressed as data.
Two mechanisms, in order of preference:

A. Custom registered behavior. Write a new `ExtendedProperty` subclass in the
   content assembly, register it under a new name (e.g. `tower_riddle`), and
   configure it from YAML via `params`. Preferred: it works with any room and
   keeps the room a plain `Room`.

B. Room subclass named in the file. For logic that is really about the room
   as a whole (override of `Examine`, custom enter/exit hooks), the room
   specifies:
   ```
   - key: entry
     class: MUD.PlanarRealms.Planes.AstralCentrum.Rooms.TowerOfAellioc_1
   ```
   Rules: `class` is a full .NET type name that MUST resolve to a `Room`
   subclass in a loaded content assembly (checked in phase 1). The class is
   stored as a Bson discriminator/`class` field so the Mongo class map
   (DI-created via MongoDBClassMapManager) instantiates the right type on
   load. It is a definition field. Changing `class` on an existing room is
   allowed and is reported as a notable change in the plan. A room subclass
   MUST NOT add data in its constructor that duplicates what the file
   defines; constructors are for code-only wiring. Properties added in a
   constructor are not persisted and are not touched by import (they are
   "code-owned").

Existing `TowerOfAellioc_1` should migrate to approach (data), because its
only behavior is expressible as `search` data; its C# class is then
removable. This migration is out of scope here.

## 8. Room subclass resolution (summary)

- No `class`: instantiated as plain `Room`.
- `class` given: validated in phase 1 as above. Importer never loads
  assemblies; it only looks in assemblies the World already loaded.

## 9. Locally-modified flag (future; describe only — DO NOT IMPLEMENT)

Problem: in-game edits (Terraform setshort/setlong/link/unlink, future
creator tools) change definition fields in the DB. A later re-import would
overwrite those edits. Conversely, some changes should win.

Design (to implement later):
- Each room (and optionally each list element) has `local_modified`: a set of
  definition field names changed in-game since the last import. Example:
  `local_modified: [long, exits]`. In-game edit code adds the field name; the
  importer is the only thing that clears it, and only with an explicit
  operator choice.
- 9.1 Default import behavior with the flag present: for fields in
  `local_modified`, DO NOT write; report `skipped (locally modified)` with a
  field-level diff of file vs DB values in the plan output.
- 9.2 Operator options per import run: `--respect-local` (default),
  `--force` (overwrite flagged fields and clear the flag), `--force-field
  <room>:<field>` (targeted), and `--adopt` (leave DB value but
  record file-hash so it no longer reports divergence — for when the edit
  is to be exported back to the file).
- 9.3 Export back: a future export command writes the DB's current
  definition fields to YAML, so in-game edits can be captured into the file
  and the flag cleared. (There is an `ExportCommand` in Creator commands
  today; its relation to this format is a follow-up question.)
- 9.4 Interim rule until implemented: with no flag, the file wins entirely.
  Importing over a world that has been edited in-game will lose those edits.
  The importer MUST therefore print a loud warning when the DB `source_hash`
  of a room differs from the hash of what the importer last wrote (it can
  detect a divergence even without the flag), and SHOULD require
  `--force` to proceed on such rooms. This is a cheap way to avoid the flag
  while still not destroying work silently.
- 9.5 Orphans: a room in the DB whose `source` is this file but whose key is
  no longer in the file. Never deleted by import. Reported in the plan.
  Later, an explicit `--prune` could remove them if empty of players and
  items. Exits from other rooms still pointing at an orphan are reported as
  dangling references.
- Bookkeeping fields to support this: `source` (area prefix / file id that
  owns the room, set on insert and checked in phase 1.7) and `source_hash`
  (hash of the resolved definition last written).

## 10. Area / X / Y / Z

Fields on a room (definition): `area` (string, the area prefix), `x`, `y`,
`z` (integers). Neither exists on `Room` today; they are proposed.

- `area` is set from the file's `area.prefix` for every room in the file. It
  is not written per room in the YAML.
- Coordinates are optional. In YAML: `coords: {x: .., y: .., z: ..}`, with
  `z` defaulting from `area.defaults.coords.z`, otherwise absent. A room with
  no coordinates stores none (null) rather than 0, so "unplaced" is
  distinguishable from the origin.
- Coordinates are per-area (local grid); not global. The pair (area, x, y, z)
  SHOULD be unique among placed rooms in an area; a duplicate is a
  validation warning, not an error (intentional overlays, e.g. a maze).
- Convention (not enforced): +x east, +y north, +z up. An optional importer
  check may warn when a compass exit's destination coordinates disagree
  with the exit direction.
- Purpose: mapping/minimap, generators placing rooms, web editor layout. Exits
  remain the only movement truth; coordinates never create movement.

## 11. Text normalization

- `short`: single line, trimmed. No trailing period requirement.
- `long`: YAML block scalars (`|` or `>`) allowed. The importer trims
  trailing whitespace on each line and the whole value, converts line endings
  to `\n`, and stores the result. Rendering (wrapping, `\r\n` for telnet) is
  not the importer's concern.
- Formatting markup such as `%^GREEN%^` is passed through untouched.

## 12. Generator output contract

Procedural and tool generators (maze builders, dungeon makers, converters
from other MUD formats) MUST emit this same YAML format, and nothing else:
- They produce an area file (section 4) and never write to MongoDB directly.
  The importer is the only writer of authored content.
- Output must pass phase 1 validation unmodified. A generator SHOULD run the
  validator on its own output.
- Deterministic and stable: the same input and seed give byte-identical
  output, and local keys are derived from stable inputs (not random or
  positional counters that shift when one room is added), so re-generation
  produces small diffs and idempotent upserts. Randomness is seeded; seed
  recorded in `x-generator` metadata.
- Generated areas use their own prefix, so hand-written files and generated
  files never collide (phase 1.7 will catch it if they do).
- Generated files are authoritative and regenerable: do not hand-edit them. If
  hand-edits are wanted, copy to a new hand-authored area.
- A generator MAY include `x-generator: {name, version, seed}` under `area`.
- Coordinates SHOULD be emitted by generators (they know the layout).

## 13. Future web backend

The web backend (admin/builder UI, future) uses the same upsert, not a second
write path:
- The backend edits rooms as structured data (the same schema), and calls
  the same validate/plan/apply code as the file importer, with the same
  field-level rules and the same state protection.
- A UI edit of a room is a one-room, one-field import: `source` becomes
  `web` (or the file's area when the file is the owner), and the locally-
  modified flag (section 9) applies exactly as for in-game edits.
- Web edits must be exportable back to YAML (9.3) so files stay the
  source of truth where used.
- Concurrency: the backend and the running MUD process both write rooms. Both
  MUST use the field-level `$set` path; neither may use whole-document
  replace on rooms. The MUD process must refresh or evict cached rooms on
  change (6.5), by polling a change marker or via change streams — decision
  deferred (open question).
- Authentication/authorization is out of scope.

## 14. Required follow-up work (separate tasks; not part of this spec task)

1. Change `Entity.Id` and `Exit.DestinationId` Bson representation from
   ObjectId to String; keep generated-ObjectId-string for unauthored rooms.
2. Add `Area`, `X`, `Y`, `Z`, `Class`/discriminator, `Source`, `SourceHash`
   to the Room model; initialize `Plurals` and null-guard
   `IsPluralMatchForString`.
3. Repository: field-level upsert, bulk id lookup, cache refresh/evict.
4. Behavior registry and `search` registration; property `id` and separated
   property state.
5. Importer (validate / plan / apply, `--dry-run`).
6. Locally-modified flag (section 9) and hooking Terraform and other
   creator edits to it.
7. Migrate `TowerOfAellioc_1` to data.

## 15. Open questions

1. Do we have replica-set Mongo available for transactions on apply, or do
   we accept validate-first plus idempotent re-run?
2. Cache coherence between a separate web process and the MUD process:
   change streams, polling a version field, or restart-only?
3. Should `reverse` be allowed to target other areas if both files agree? (Spec
   currently forbids it.)
4. Should miss messages for `search` be configurable (`miss_message`)?
5. Are items (InventoryItem templates) going to get their own keyed files? The
   `reward` is currently an inline template; a keyed item catalog would let
   rewards reference by key.
6. Relationship of this format to the existing Creator `ExportCommand`.
7. Do creator edits that arrive via a future `local_modified` flag apply to
   feature/property elements as well as room-level fields? (Spec says
   optionally yes.)

## 16. Worked example

File: `tower_of_aellioc.yaml`. It uses all constructs, and reproduces the
TowerOfAellioc_1 search as data. Cross-area exit `to: town.gate` assumes the
area `town` is already imported (otherwise phase 1.5 reports it dangling).

```yaml
area:
  schema_version: 1
  prefix: tower_of_aellioc
  name: "Tower of Aellioc"
  description: "A lone wizard's tower on the edge of the Astral Centrum."
  defaults:
    coords:
      z: 0
  x-generator: null            # hand-authored

rooms:
  - key: entry
    short: "the entrance hall of the Tower of Aellioc"
    long: |
      Pale light spills through high windows onto a worn stone floor.
      A heavy oak table stands against the far wall, and a spiral
      stair winds upward into the tower.
    handles: [hall, entrance]
    adjectives: [entrance]
    coords: {x: 0, y: 0}
    exits:
      - dir: north
        to: study
        reverse: south
      - dir: up
        to: landing
        reverse: down
      - dir: out
        to: town.gate          # cross-area; no reverse allowed here
    features:
      - short: "a heavy oak table"
        long: "A heavy oak table, scarred by years of use. Something might be hidden on it."
        handles: [table]
        adjectives: [heavy, oak]
        plurals: [tables]
    properties:
      - behavior: search
        id: table_search
        params:
          target: table
          refresh_ms: 600000
          reward:
            short: "a really cool sword"
            long: "A sword that is really, truly cool."
            handles: [sword]
            adjectives: [cool]
            plurals: [swords]

  - key: study
    short: "a cluttered study"
    long: >
      Books lie in drifts on every surface. A narrow window overlooks
      the astral void.
    coords: {x: 0, y: 1}
    exits: []                  # south exit is created by 'reverse' on entry

  - key: landing
    short: "a drafty landing"
    long: "The spiral stair opens onto a small landing. The air is cold here."
    coords: {x: 0, y: 0, z: 1}
    exits: []                  # down exit is created by 'reverse' on entry
```

What an import of this file does, on a fresh DB: validates (all keys unique,
`study` and `landing` exist, `town.gate` exists in the DB, `search` is
registered), expands reverses (`study.south -> entry`, `landing.down ->
entry`), then inserts three rooms with `_id`s `tower_of_aellioc.entry`,
`tower_of_aellioc.study`, `tower_of_aellioc.landing`, all with `Items: []`.
On a second run it reports three `unchanged` rooms and performs no writes. If
a player has dropped a rope in `entry`, and `long` of `study` is edited in the
file, a re-import updates only `study.LongDescription` and `source_hash`;
the rope in `entry` and the search cooldown are untouched.
