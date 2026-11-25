# Planar Realms — Terrain Generators

Status: DRAFT specification. Section 15 records what is implemented (generator card t_6eb48f02); everything else is not yet built.
Task: t_3326f04a. Branch: tgspec (based on `hermes`, tip 78e053d).

Conventions:
- MUST / SHOULD / MAY have their RFC 2119 meaning.
- "Today" describes the code at `hermes` 78e053d, with file references. "Proposed" is what this spec requires of the follow-up cards.
- "Owner decision" (OD1..OD12 below) is fixed input; this document does not reopen them. Anything not decided by the owner and not
  derivable from code is in section 14 (Open questions), not silently resolved.

## 1. Purpose, scope, non-goals

Purpose: make the ASCII map generator produce varied, neighbour-aware room text and features from a shared, editable library, and give
creators a way to regenerate or refresh generated rooms without hand-writing every room.

In scope: terrain generator classes, the library (Mongo + YAML seed), the legend `color`, provenance, regen and update operations, map
re-import semantics, the admin gRPC surface and web pages that expose them.

Non-goals (later): roads, buildings and elevation layers; an image loader (the ASCII grid stays the only map input); a `locally modified`
lock (section 9 of ROOM_FILES.md stays obsolete); any change to who writes authored content.

## 2. Owner decisions (verbatim intent)

| # | Decision |
|---|---|
| OD1 | Extend `MUD.Core/Generation/MapGenerator.cs` (ASCII map -> area YAML -> importer). Do not replace it. ASCII grid is the only map input. |
| OD2 | Legend entries get optional `color: "#RRGGBB"`, used only by the website map view. The generator parses and ignores it; it never reaches room data. |
| OD3 | Terrain id = legend template name. One generator class per terrain (`ITerrainGenerator`; `GenContext` with the cell, 8-neighbour lookup, seeded RNG via `SeededRandom`, the library). A generic `Terrain` base class does weighted, tag-filtered picks. Bespoke classes (e.g. river orientation from adjacent water) override. Registered in a `TerrainRegistry`. |
| OD4 | The library lives in MongoDB, seeded from a YAML file in the repo on fresh install. Entities: description fragment, feature template, neighbour phrase. |
| OD5 | Every entity has `terrains: []`. Empty list = NOT used by any generator (disabled/unassigned), never "any terrain". |
| OD6 | Neighbour-aware descriptions: scan the 8 neighbours, pick phrases from the library, cap the count ("A river flows just north of here."). |
| OD7 | Generated rooms carry provenance `x-gen` (map name, terrain, seed, libVersion). Generated features carry `templateId` + `templateVersion`. Description fragments are NOT tracked for update. |
| OD8 | REGEN of a room wipes and refills description, ALL features (hand-added too) and properties. Keeps exits and occupants. No lock; hand edits are lost by design. |
| OD9 | UPDATE of a room touches only features carrying a `templateId`; newer template version -> rewrite text/handles; template deleted -> report, leave unchanged. Features without a reference are never touched. Update never touches the room description. |
| OD10 | Room keys from maps carry the map identity (prefix); `x-gen` records the map name. Re-importing a map regenerates every room of that map (effectively regen of all rooms). Importer upsert must replace, not merge, for generated rooms. |
| OD11 | The importer stays the only writer of authored content (ROOM_FILES.md section 12). Generated output must pass importer phase-1 validation. |
| OD12 | Roads / buildings / elevation layers are out of scope. |

## 3. Grounding: what the code does today (verified)

Facts, with the file read. Each is relied on by a later section.

1. **Generator pipeline** (`MapGenerator.cs`). `MapGenerator.Generate(mapYaml)` parses a map file with top-level keys `generator`,
   `grid`, `legend`, `templates`, `overrides`, builds one `Cell` per non-space glyph, keeps walkable cells, emits an area YAML, and
   self-validates it with `AreaParser.Parse`. Legend is `glyph -> template name` (a plain scalar; `ParseLegend` reads
   `(pair.Value as YamlScalarNode)?.Value`, so a mapping value today yields "unknown template"). Templates carry `short` / `long` pools only
   (`TemplateKeys`: walkable, short, long, handles, adjectives, plurals); `CheckKeys` rejects unknown non-`x-` keys.
2. **Determinism.** `BuildRoom` seeds `new SeededRandom(seed + "\n" + prefix + "\n" + cell.Key)` per cell and draws in a fixed order, with the
   comment "so a cell's text never depends on its neighbours". Neighbour-aware text (OD6) deliberately changes that property for the neighbour
   scan inputs (the *glyph layout* now matters) but not RNG isolation; see 7.3.
3. **Room key** = `<prefix>.x<XX>y<YY>` (`KeyFor`, `Coord`; negatives render `n<abs>`). The prefix is `generator.prefix` and is the area (`Area`).
   `MapFileService` writes `<contentDir>/<prefix>.yaml` from `<contentDir>/maps/<map>.map.yaml`; the map *file name* and the *prefix* are
   independent today. Command: `genmap <map> [dry]` (`GenMapCommand`).
4. **x- keys are parsed away.** `AreaParser.CheckKeys` skips any key starting `x-` (area, room, feature, ...). The room and feature models
   (`RoomDefinition`, `FeatureDefinition`) have no field for them, and the importer builds the stored document from `Room`/`Feature`. The
   existing area-level `x-generator: {name, version, seed}` is therefore never stored. **Provenance is not persisted today** and needs a parser
   field, an importer field list entry and a stored field (section 5).
5. **Importer write set.** `AreaImporter.DefinitionFields` = `ShortDescription, LongDescription, Handles, Adjectives, Plurals, Exits,
   Features, search, behaviors, Area, X, Y, Z`. Only changed fields are `$set` (`Repository.UpsertFields`, `$set` + `$setOnInsert {Items: []}`),
   so `Items` (state) is never written. The import store only exposes `Find`, `FindIdsByArea`, `Upsert`; there is no delete.
6. **Today's merge behaviour** (`BuildDesiredFields`, only when the room already exists):
   - Description, handles, adjectives, plurals, exits, area, coords: plain overwrite of the whole field. **Description is replaced already.**
   - `Features`: `MergeElements(existing, authored, ByShort)` returns the list in *file order* (stored features absent from the file are
     **dropped**) but a feature whose `short` (case-insensitive) matches a stored one starts from the stored element and overlays authored
     fields, so any stored field the file does not author survives (e.g. a feature behavior/component, `_id`, and, for new fields, a stale
     `templateId`). This is a merge at element level.
   - `search` (properties): `MergeElements` by entry `id`: stored entries absent from the file are dropped, but a matched entry keeps its stored
     `nextRewardAt` cooldown (state).
   - `behaviors`: stored entries are **always kept**; a file entry colliding on (hook, module, type) is skipped and reported, or replaces in
     place with `replaceBehaviors`; stored entries absent from the file are never removed (`MergeBehaviors`). This is an additive merge.
   - Rooms of the same `Area` in the store but absent from the file are reported as `Orphans`, never deleted.
   - Cross-area key collision is an error (`Validate`: "already exists and belongs to area").
7. **Live objects.** `RoomRepository.RefreshCached` calls `Room.ApplyDefinition(fresh)`, which copies description, handles, `Components`,
   `Search`, `Features`, `Exits`, `Area`, `X/Y/Z` onto the live room; `Items` and `Occupants` are untouched. `UpsertFields` runs this under the
   repository lock. `ApplyDefinition` does not copy `Behaviors` today (a gap for replace-on-import of behaviours; see 8.2 and Q5).
8. **Feature model.** `Feature : GameObject` with `RoomLine`, `DetailDescription`, handles/adjectives/plurals. Feature id is
   `<roomKey>#<short lowercased>`. Area-file features: `short`, `long`, `room_line`, `detail`, `handles`, `adjectives`, `plurals`; unknown keys
   other than `x-` are errors; duplicate `short` in a room is a phase-1 error.
9. **Admin API.** `contracts/mud/admin.proto`, `Admin` service, unary calls, token on every request, roles creator/admin; gRPC layer
   `MUD.Core/Administration/AdminService.cs`, logic `AdminApi.cs`, specified in `specs/ADMIN_API.md`. `RoomFeature` has no provenance field;
   `UpdateRoom` with `features_present` replaces the Feature list matching by `short` and keeps unrepresentable stored fields
   (`MergedFeatureDocuments`). Rule refusals return in `result`; audit rows are written first, only web/creator edits (D34).
10. **Web.** Razor Pages in `MUD.Web/Pages` (`CreatorMap`, `RoomEdit`, `Creator`, `Modules`, ...), authorised `CreatorOrAdmin`; the map is laid out in the
    web from `ListRooms` (`CreatorMapModel`). The web never touches Mongo (P6-5).
11. **Mongo.** Core is the only process that talks to Mongo (ADMIN_API.md). Room collection name `"room"`; audit collection `"audit"`
    (`AuditStore.CollectionName`). Raw-document access exists (`GetRawDocument`, `GetIdsByField`, `UpsertFields`).

Verified-not-built: Mongo is not available in the authoring sandbox, so nothing here was exercised against a database.

## 4. Architecture overview

```
 maps/<map>.map.yaml ──► MapGenerator ──► area YAML (with x-gen) ──► AreaImporter ──► Mongo room
                            │   ▲                                         ▲
                 TerrainRegistry│ library (Mongo: terrain_*)               │
                            ▼   │                                         │
                    ITerrainGenerator ◄─ GenContext                 Regen / Update service
                                                                  (build area YAML for chosen
                                                                   rooms, feed the importer)
```

Principles:
- **Single writer.** Regen and update are implemented as "produce an area file for the selected rooms, hand it to the importer with a
  mode flag" (section 9). They never call `UpsertFields` themselves. This keeps OD11.
- **Generator is pure.** Given (map text, library snapshot, seed) it returns YAML. It reads the library through an immutable snapshot
  (`ITerrainLibrary`), so output is a function of its inputs and the library `libVersion`.
- **Library is data, generators are code.** Editing text/weights/templates never needs a deploy. Adding a *new* terrain class does; terrains with
  no bespoke class use the generic `Terrain`.

## 5. Provenance (`x-gen`)

### 5.1 Room level

Emitted by the generator on every generated room, as a room key `x-gen` (the parser already tolerates `x-` keys; it MUST now read this one):

```yaml
- key: city.x12y07
  short: "..."
  long: |
    ...
  x-gen:
    map: sample_city          # map file name (without .map.yaml); the identity used for map-wide regen
    terrain: river            # legend template name (OD3)
    seed: "abc123"            # the map's generator.seed
    libVersion: 17            # library version the room was generated against (section 6.4)
    genVersion: 1             # MapGenerator.GeneratorVersion at generation time (proposed addition)
```

Required keys: `map`, `terrain`, `seed`, `libVersion`. `genVersion` is optional and informational. Unknown keys inside `x-gen` are an error
(phase 1), so the shape stays closed.

Stored as a new room field `Gen` (BSON element `gen`): `{ map, terrain, seed, libVersion, genVersion }`. Proposed changes:
`RoomDefinition.Gen` (parser), `Room.Gen` (model, `[BsonElement("gen")]`), add `"gen"` to `AreaImporter.DefinitionFields`. A room with no
`gen` is hand-authored and is never selected by regen/update (section 9.1).

The existing area-level `x-generator` stays. It is documentation only and MUST NOT be used to decide anything (it is not stored today, fact 4).

### 5.2 Feature level

A generated feature carries a reference to the template it came from:

```yaml
features:
  - short: "a muddy bank"
    long: "..."
    room_line: "A muddy bank slopes down to the water."
    handles: ["bank", "mud"]
    x-gen: { templateId: bank-mud, templateVersion: 3 }
```

Parser: `FeatureDefinition.TemplateId` (string) and `TemplateVersion` (int), read from the feature-level `x-gen` (both required together, or both
absent). Stored on `Feature` as `[BsonElement("templateId")]` and `[BsonElement("templateVersion")]`; null/absent for hand-added features.
A feature without `templateId` is "unreferenced" and update never touches it (OD9).

### 5.3 What is not tracked

Description fragments and neighbour phrases are baked into the description text at generation time; the room records nothing about which
fragments were used (OD7). The only description-related provenance is room `x-gen.{seed,libVersion}`, which makes a regen reproducible
against the same library snapshot but not a basis for partial update.

### 5.4 Colour never in room data

`color` is read from the map legend and exposed only on the map-metadata endpoint (section 11.3). It is never copied into `x-gen`, rooms,
features or the area YAML.

## 6. Library

### 6.1 Entities and the `terrains` rule

All three entities have `terrains: [<terrain id>, ...]`. **Empty list = not used by any generator** (OD5). A generator never selects an
entity whose `terrains` does not contain its terrain id. The admin UI shows such entities under "Unassigned".

**Description fragment** (`terrain_fragments`)

| field | type | notes |
|---|---|---|
| `id` | string | unique, kebab-case, stable |
| `terrains` | string[] | see above |
| `slot` | string | which pool of the terrain's long description it fills (e.g. `ground`, `sky`, `sound`, `flora`); a terrain's generator declares its ordered slots, one pick per slot (`GenericTerrain.Slots`); an empty slot is skipped |
| `text` | string | one sentence/fragment; normalised by `AreaParser.NormalizeText` rules |
| `weight` | int >= 0 | 0 = never picked by weight (kept for tag-forced use); default 1 |
| `tags` | string[] | free-form, used for `requireTags` / `excludeTags` filters and for mutual exclusion (e.g. `wet`, `night`) |

**Feature template** (`terrain_features`)

| field | type | notes |
|---|---|---|
| `id` | string | unique, stable; this is what `templateId` references |
| `terrains` | string[] | |
| `short` | string | the feature `short` (identity within the room, case-insensitive). **Added to the owner's field list**: a feature needs `short` and `long` to pass phase 1; see Q1 |
| `long` | string | |
| `handles`, `adjectives` | string[] | |
| `roomLine` | string | optional; text appended to the room description when examined |
| `detail` | string | optional; `look <noun>` text |
| `weight` | int | pick weight among eligible templates |
| `chance` | number 0..1 | probability the template is *attempted* per room, rolled before weight selection |
| `version` | int >= 1 | bumped by the editor on any text/handles change; referenced by `templateVersion` |
| `tags` | string[] | optional, same use as fragments (Q1) |

**Neighbour phrase** (`terrain_neighbor_phrases`)

| field | type | notes |
|---|---|---|
| `id` | string | |
| `terrains` | string[] | the *room* terrains the phrase may appear in |
| `neighborTerrain` | string | the terrain id of the neighbouring cell that triggers it |
| `text` | string | contains `{dir}` (e.g. `A river flows just {dir} of here.`) |
| `weight` | int | choice among phrases for the same `neighborTerrain` |
| `merge` | bool | default true; if true, several sides matching the same `neighborTerrain` become one phrase with `{dir}` = `north and east` (rules in 7.4); if false, one phrase per side |

### 6.2 Seed file (repo) — YAML schema

Path: `MUD.Core/Generation/Library/terrain-library.yaml` (shipped as a content file copied to the output directory; location is Q3).

```yaml
schema_version: 1
library_version: 1            # initial value of the Mongo library version counter
fragments:
  - id: grass-ground-1
    terrains: [grass]
    slot: ground
    text: "Short green grass carpets the ground."
    weight: 3
    tags: [dry]
features:
  - id: boulder-grey
    terrains: [grass, forest]
    short: "a grey boulder"
    long: "A lichen-spotted boulder sits half buried in the earth."
    handles: [boulder, rock]
    adjectives: [grey, lichen-spotted]
    roomLine: "A grey boulder squats nearby."
    detail: "..."
    weight: 2
    chance: 0.15
    version: 1
neighborPhrases:
  - id: river-near
    terrains: [grass, forest]
    neighborTerrain: river
    text: "A river flows just {dir} of here."
    weight: 1
    merge: true
```

Validation of the seed file (run on load; failure aborts seeding and logs, nothing partial is written): known top-level keys only; ids unique
per entity type; `weight` int >= 0; `chance` in [0,1]; `version` int >= 1; `text` of a neighbour phrase contains exactly one `{dir}`; every
`terrains` / `neighborTerrain` entry is a non-empty string (it need not be registered; unknown ids are warned, not rejected, because maps
define terrains by legend name and a library may be authored ahead of its terrain). Unknown non-`x-` keys are errors.

**Seeding rule ("fresh install").** On core start, if `terrain_meta` has no document `_id: "library"`, load the seed file, insert all entities,
then insert `{_id: "library", version: <library_version>, seededAt}`. If the meta document exists the seed file is never read again, so
admin edits survive restarts and upgrades. Re-seeding is an explicit admin action (`ResetTerrainLibrary`, section 10), off by default and
destructive (see Q4).

### 6.3 Mongo collections

All in the world database; accessed only by core.

| collection | `_id` | document |
|---|---|---|
| `terrain_fragments` | entity `id` | `{_id, terrains, slot, text, weight, tags, updatedAt, updatedBy}` |
| `terrain_features` | entity `id` | `{_id, terrains, short, long, handles, adjectives, roomLine, detail, weight, chance, version, tags, updatedAt, updatedBy}` |
| `terrain_neighbor_phrases` | entity `id` | `{_id, terrains, neighborTerrain, text, weight, merge, updatedAt, updatedBy}` |
| `terrain_meta` | `"library"` | `{_id: "library", version: int, seededAt}` |

Index: multikey on `terrains` for each entity collection. Entity deletion removes the document (no tombstone); update's "template deleted" case
is therefore "no document with that `_id`" (section 9.3). Edits go through the audit store like web room edits (D34, per-field rows); the
collection name in the audit row is the entity collection.

### 6.4 Library version

`terrain_meta.library.version` is an integer incremented (`$inc`) by every library write (create/edit/delete of any entity). A generated room
records the value current when its generation started (a single snapshot read at the start of a run, so all rooms of one run share it).
It is informational/diagnostic (which library state produced this room); it is NOT a staleness trigger for update — only per-feature
`templateVersion` is (OD9, OD7).

## 7. Generators (code)

### 7.1 Interfaces (sketch; namespace `MUD.Core.Generation.Terrain`)

```csharp
public interface ITerrainGenerator
{
    string TerrainId { get; }                       // = legend template name
    GeneratedRoom Generate(GenContext ctx);         // pure; no I/O
}

public class GeneratedRoom
{
    public string Short;
    public string Long;                             // base text + neighbour sentences; features' room lines are NOT inlined (Room.GetRoomLine adds them)
    public List<string> Handles, Adjectives, Plurals;
    public List<GeneratedFeature> Features;         // each: FeatureDefinition fields + TemplateId + TemplateVersion
    public List<SearchPropertyDefinition> Properties; // normally empty; allows a bespoke terrain to add search entries
}

public class GenContext
{
    public int X, Y;                                // cell
    public string MapName, Prefix, Key;
    public string TerrainId;
    public SeededRandom Rng;                        // per cell, see 7.2
    public ITerrainLibrary Library;                 // immutable snapshot, includes Version
    public Neighbour[] Neighbours;                  // always 8 entries, order below
    public Neighbour At(Dir d);                     // convenience
}

public enum Dir { N, NE, E, SE, S, SW, W, NW }      // Neighbours[] order
public class Neighbour { public Dir Dir; public bool Exists; public string TerrainId; public bool Walkable; }
// Exists=false: outside the grid, or a space/unmapped glyph. TerrainId is the legend template name of that cell (also for
// non-walkable cells such as water: they have no room but they do have a terrain, which is what makes "river to the north" possible).

public interface ITerrainLibrary
{
    int Version { get; }
    IReadOnlyList<DescriptionFragment> Fragments(string terrain, string slot);
    IReadOnlyList<FeatureTemplate> Features(string terrain);
    IReadOnlyList<NeighborPhrase> Phrases(string roomTerrain, string neighborTerrain);
    FeatureTemplate FeatureById(string id);         // for update (9.3)
}

public class TerrainRegistry
{
    public void Register(ITerrainGenerator g);
    public ITerrainGenerator For(string terrainId); // registered bespoke class, else new Terrain(terrainId) (generic)
}
```

The cell grid passed to `GenContext` comes from the *full* parsed legend, including cells whose template has `walkable: false`; they are
already built into `cells` in `MapGenerator.GenerateInternal` but filtered out before `BuildRoom`. Neighbour lookup MUST use that full cell map, not the
`walkable` set. Overrides that change a cell's template (`overrides[].template`) are reflected in neighbour lookups (the final cell template wins).

### 7.2 Randomness

Per-cell seed unchanged from today: `new SeededRandom(seed + "\n" + prefix + "\n" + key)`. Draw order is fixed and documented, so adding a library entity
only changes picks that draw from that pool:
1. one pick per declared fragment slot, in slot order;
2. for each eligible feature template (in `id` order): one `chance` roll, then (if it passes) selection by weight happens in one combined
   draw loop (algorithm in 7.5);
3. neighbour phrases in `Dir` order for the grouping step (7.4).
A bespoke terrain MAY draw extra values but MUST draw them after the standard ones, so standard picks stay stable.

### 7.3 Determinism with neighbour awareness

Same map text + same seed + same library snapshot (same entities, same weights) => byte-identical YAML. This replaces the stronger "text never depends on
neighbours" comment in `MapGenerator.BuildRoom`; the new guarantee is "depends only on the cell, its eight neighbours' terrain ids, the seed and the
library". Changing the grid can therefore change the text of neighbouring rooms (intended: a new river beside a room should be mentioned).
`MapGeneratorTests` pin existing output; see 12.

### 7.4 Neighbour phrases (OD6)

1. For each of the 8 neighbours with `Exists`, collect `Phrases(room terrain, neighbour.TerrainId)`; skip neighbours with none.
2. Group neighbours by `neighborTerrain`. For a group, choose one phrase (weighted RNG pick among that terrain's phrases; one pick per group).
   If the chosen phrase has `merge: true`, one sentence is produced with `{dir}` = the group's directions joined by the rule below; if
   `merge: false`, one sentence per direction.
3. Direction wording: single cardinal -> `north`; diagonal -> `northeast`; set joined `north and east`; three or more `north, east and south`.
   Cardinals sort N, E, S, W then diagonals NE, SE, SW, NW. If all eight sides match the group becomes `all around` (so the text reads
   "A river flows just all around of here" — an awkward template; Q6 asks whether a separate `{dir}`-less variant is wanted).
4. **Cap**: at most `MaxNeighborSentences` (default 2, a constant of `Terrain`, overridable per terrain class) sentences. If more groups
   qualify, keep the groups with the most matching sides, ties broken by `Dir` order then by `neighborTerrain` ordinal (deterministic, no RNG).
5. Sentences are appended to the end of the description, in the same order, separated by a space. They are baked into the stored text (OD7).

### 7.5 Generic `Terrain` base class

```csharp
public class Terrain : ITerrainGenerator
{
    public Terrain(string terrainId);
    public virtual string TerrainId { get; }
    public virtual IReadOnlyList<string> Slots => new[] { "ground" };   // overridable
    public virtual int MaxNeighborSentences => 2;
    public virtual int MaxFeatures => 2;

    public virtual GeneratedRoom Generate(GenContext ctx)
    {
        // 1. Short: weighted pick, slot "short" (a terrain MUST have at least one eligible "short" fragment, else generation errors: 7.6).
        // 2. Long: one weighted pick per slot in Slots, joined with a space; then neighbour sentences (7.4).
        // 3. Features: see PickFeatures.
    }

    protected T WeightedPick<T>(SeededRandom rng, IReadOnlyList<T> items, Func<T,int> weight);   // sum of weights; weight 0 excluded; empty -> null
    protected IReadOnlyList<DescriptionFragment> Filter(IReadOnlyList<DescriptionFragment> all, string[] requireTags, string[] excludeTags);
    protected virtual List<GeneratedFeature> PickFeatures(GenContext ctx);
    protected virtual string Orientation(GenContext ctx) => null;   // hook used by bespoke classes
}
```

`PickFeatures`: eligible = `ctx.Library.Features(TerrainId)` in `id` order; for each, one `rng.Next(1_000_000) < chance * 1_000_000` roll
(exactly one draw per eligible template, pass or fail, for stability); the passers are weighted-picked without replacement until `MaxFeatures`;
duplicate `short` (case-insensitive) within the room is skipped (phase-1 would otherwise fail). Each picked template becomes a feature with
`TemplateId = id`, `TemplateVersion = version`.

Bespoke classes: `River : Terrain` overrides `Generate` (or `Orientation`) to read `ctx.At(Dir.N/E/S/W).TerrainId == "river"` and choose a
fragment tagged `flows-ns` or `flows-ew` (via `requireTags`) and append the orientation to `Handles`/description as needed. Classes are registered in
`TerrainRegistry` at startup (explicit list in `TerrainRegistry.Default()`; no reflection scanning); a terrain with no class falls back to the
generic `Terrain`.

### 7.6 Errors

Generation errors are collected like `MapGenerator._errors` (no partial YAML). New errors: `terrain '<id>' has no eligible 'short' fragment`,
`... no eligible fragment for slot '<slot>'` (only for the first slot; other slots may be empty), `unknown terrain '<id>' in legend` is NOT an error
(generic class applies). Yaml and area header unchanged. The `TemplateKeys`/`walkable` handling in the map is unchanged; see 8.1.

## 8. Map file changes (`MapGenerator`)

### 8.1 Legend

Today a legend value is a scalar template name. Proposed (backwards compatible): a value may be a scalar **or** a mapping:

```yaml
legend:
  ".": grass                       # unchanged
  "~": { template: river, color: "#3a7bd5" }
  "T": { template: forest, color: "#1f6f3a" }
```

- `template` required in the mapping form; `color` optional, regex `^#[0-9A-Fa-f]{6}$`, else error `legend: glyph 'x' has an invalid color`.
- Unknown keys in the mapping are errors (closed set: `template`, `color`).
- Terrain id = the template name (OD3). A cell's terrain is `legend[glyph].Template.Name`; an override `template` changes it.
- `color` is parsed into `ParsedMap.Colors` (`terrainId -> "#RRGGBB"`), never into room data (OD2). If two glyphs map to the same template with
  different colours, the first by glyph ordinal wins and a warning is returned (colour is per terrain, because the web shows terrain colours,
  Q7).

### 8.2 Templates when the library exists

The map file's `templates:` block stays valid and keeps its meaning: `short`/`long` pools, handles etc. defined in the map file. Resolution order
per terrain (proposed; Q2): if a legend template has map-file `short`/`long` pools, those are used and the library supplies only *features* and
*neighbour phrases*; if it has neither, the library supplies short/long (fragments, 7.5). `walkable: false` stays a template property in the map
file (the library has no notion of walkable). This keeps existing maps (e.g. `sample_city`) generating the same output until they opt in.

### 8.3 Provenance on output

`BuildRoom` additionally emits room `x-gen` (5.1) and per-feature `x-gen` (5.2). `x-gen.map` = map file name (`MapFileService.Generate(mapName)`
already has it; `MapGenerator.Generate(mapYaml)` must gain a `mapName` parameter, added without breaking the existing signature via an
overload; callers: `MapFileService`, tests).

## 9. Regen, update, map re-import

All three produce an area YAML and call the importer; they differ in the *mode* passed to `AreaImporter.Import`.

### 9.1 Selecting rooms

A room is "generated" iff it has a stored `gen` field. Selection is by room key list, or by `gen.map` (all rooms of one map), or by `Area`.

### 9.2 Import modes (proposed `ImportOptions`)

`AreaImporter.Import(yaml, dryRun, replaceBehaviors)` gains an options object (existing parameters stay as an overload):

```csharp
public class ImportOptions
{
    public bool DryRun;
    public bool ReplaceBehaviors;
    public ReplaceGenerated ReplaceGenerated = ReplaceGenerated.Merge; // Merge | Replace
}
```

With `ReplaceGenerated.Replace`, for each room in the file that has `x-gen` in the file (i.e. the generator says it owns the room) AND whose stored
document is either absent or has a stored `gen` with the same `map`, the importer builds the desired value of the owned fields **without
consulting the stored document**, instead of `MergeElements`/`MergeBehaviors`:

| field | rule under `Replace` for a generated room |
|---|---|
| `ShortDescription`, `LongDescription`, handles etc., `Area`, `X/Y/Z` | overwrite (same as today) |
| `Exits` | overwrite from the file (map defines them; same as today) |
| `Features` | **replace wholesale** with the file's list; stored features are dropped, hand-added ones included; stored-field overlay NOT applied (no stale `templateId`, no feature behaviors/components carried) |
| `search` (properties) | replace wholesale; cooldown `nextRewardAt` of matching entries is reset (it is state of a thing that is regenerated; Q8) |
| `behaviors` | replace wholesale with the file's list (empty list => stored cleared) |
| `gen` | overwrite |
| `Items` | untouched (state; never in `DefinitionFields`) |
| occupants | not stored; unaffected |

A stored room with a *different* `gen.map`, or a stored room with no `gen` (hand-authored) is **not** replaced even in `Replace` mode: it goes
through the normal merge and the plan reports it (`NotOwned`), because the key collision means a hand-authored room sits at a map key (OD10
only authorises replacing the map's own rooms). The importer result gains `Replaced: List<string>` and `NotOwned: List<string>` for reporting.

Live objects: `RoomRepository.RefreshCached` -> `Room.ApplyDefinition` already assigns Features/Search/Exits wholesale (fact 7), so replacement
reaches live rooms. **Gap:** `ApplyDefinition` does not copy `Behaviors` (or `Gen`); the implementation card MUST add both, otherwise a replaced
behaviors list shows only after restart.

Validation is unchanged: phase 1 (parser + `Validate`) runs on the generated YAML first; the importer is the only writer (OD11).

### 9.3 Operations

**Map re-import (OD10).** Flow: `genmap <map>` (or `RegenerateMap` rpc) generates `<prefix>.yaml` from the map text and library snapshot; the
import of that file is run with `ReplaceGenerated.Replace`. Result: every room of the map is regenerated (description, features, behaviors,
properties) and exits follow the grid; occupants and items stay. Rooms of the prefix missing from the new map remain **orphans** (reported,
not deleted — unchanged importer rule, fact 5; deleting is not offered, see Q9). Identity: the map identity is the prefix; the map *file name*
is stored as `gen.map`. A prefix change in the map file produces a different key space (new rooms); the old rooms become orphans of the old prefix.
Rule: `gen.map` and prefix SHOULD be equal; the generator emits a warning if a map file name and `generator.prefix` differ (Q10).

**REGEN of selected rooms (OD8).** `RegenerateRooms(keys[])`: for each key, load its `gen.map`, regenerate the *whole map* in memory (neighbour
awareness needs the grid, and the per-cell seed makes a single cell's output reproducible), take only the requested rooms' definitions,
write them as a partial area file, import with `Replace`. Seed: use the stored `gen.seed` unless the request supplies `new_seed` (optional;
a new seed rerolls). Library: current. The map file is read from the content directory (`maps/<gen.map>.map.yaml`); if it is gone the call
reports `MAP_NOT_FOUND` and changes nothing. The room's `gen.libVersion` becomes the current version.

**UPDATE of selected rooms (OD9).** `UpdateRooms(keys[])`: per room (generated rooms only), for each stored feature with a `templateId`:
1. template found, `template.version > feature.templateVersion` -> rewrite that feature's `long`, `short`, `handles`, `adjectives`, `roomLine`, `detail`
   and `templateVersion` from the template. If `short` changes, the Feature id changes (`<roomKey>#<short>`); an update that would cause a duplicate
   `short` in the room is skipped and reported (`CONFLICT`);
2. template found and `version == templateVersion` -> unchanged; `version < templateVersion` (a rollback) -> reported `NEWER_IN_ROOM`, unchanged;
3. template missing -> reported `TEMPLATE_DELETED`, feature left unchanged;
4. a feature without `templateId` -> never read for change.
The room description, `gen`, exits, properties and behaviors are not touched. Implementation: the importer writes whole fields, so Update reads the
stored `Features`, patches the referenced elements in memory, and writes the full `Features` array through the importer. Proposed: a third mode
`ImportOptions.FeaturesOnly` in which the importer writes only `Features` (stored elements kept, patched ones rewritten, merge by `short` as today).
Rationale: update must not need the map file or touch other fields. This keeps the importer the only writer (OD11); see Q11 for the alternative
of a small `GeneratedRoomStore`.

### 9.4 Reports

Every operation returns one line per room: `key: replaced | unchanged | not-owned | map-not-found | error`; for update: per feature
`(featureShort) updated v2->v4 | unchanged | template-deleted | newer-in-room | conflict`. Dry run is supported on all (importer `DryRun`).

## 10. Admin gRPC surface

Added to the `Admin` service in `contracts/mud/admin.proto`; logic in `MUD.Core/Administration` (new `TerrainAdminApi.cs`, same pattern as `AdminApi`:
game-thread dispatch for anything touching live rooms, audit-first, token on every call, fixed-sentence messages). Role: **creator or admin** for read
and library edits; **admin only** for `ResetTerrainLibrary`. Unauthenticated/PermissionDenied/Unavailable semantics as ADMIN_API.md.

```proto
service Admin {
  // ... existing rpcs ...
  rpc ListTerrainLibrary(TerrainLibraryQuery) returns (TerrainLibraryReply);
  rpc SaveFragment(SaveFragmentRequest) returns (LibraryEditReply);
  rpc SaveFeatureTemplate(SaveFeatureTemplateRequest) returns (LibraryEditReply);
  rpc SaveNeighborPhrase(SaveNeighborPhraseRequest) returns (LibraryEditReply);
  rpc DeleteLibraryEntity(DeleteLibraryEntityRequest) returns (LibraryEditReply);
  rpc ListMaps(TokenRequest) returns (ListMapsReply);
  rpc GetMapMeta(GetMapMetaRequest) returns (GetMapMetaReply);
  rpc RegenerateMap(RegenerateMapRequest) returns (GenerationReply);
  rpc RegenerateRooms(RegenerateRoomsRequest) returns (GenerationReply);
  rpc UpdateRooms(UpdateRoomsRequest) returns (GenerationReply);
  rpc ResetTerrainLibrary(ResetLibraryRequest) returns (LibraryEditReply);   // admin only
}

enum LibraryEntityKind { LIBRARY_ENTITY_KIND_UNSPECIFIED = 0; LIBRARY_FRAGMENT = 1; LIBRARY_FEATURE = 2; LIBRARY_NEIGHBOR_PHRASE = 3; }

message TerrainLibraryQuery {
  string token = 1;
  string terrain = 2;          // filter: entities whose terrains contain this id
  bool unassigned_only = 3;    // filter: terrains is empty (OD5); wins over `terrain`
  LibraryEntityKind kind = 4;  // UNSPECIFIED = all kinds
}
message TerrainLibraryReply {
  int32 library_version = 1;
  repeated FragmentRecord fragments = 2;
  repeated FeatureTemplateRecord features = 3;
  repeated NeighborPhraseRecord phrases = 4;
  repeated string known_terrains = 5;   // registered classes + terrains seen in stored rooms' gen.terrain + terrains referenced by entities
}
message FragmentRecord { string id = 1; repeated string terrains = 2; string slot = 3; string text = 4; int32 weight = 5; repeated string tags = 6; }
message FeatureTemplateRecord {
  string id = 1; repeated string terrains = 2; string short = 3; string long = 4;
  repeated string handles = 5; repeated string adjectives = 6;
  string room_line = 7; string detail = 8; int32 weight = 9; double chance = 10; int32 version = 11; repeated string tags = 12;
}
message NeighborPhraseRecord { string id = 1; repeated string terrains = 2; string neighbor_terrain = 3; string text = 4; int32 weight = 5; bool merge = 6; }

message SaveFragmentRequest { string token = 1; FragmentRecord fragment = 2; bool create = 3; }
message SaveFeatureTemplateRequest {
  string token = 1; FeatureTemplateRecord template = 2; bool create = 3;
  // The server sets version = stored version + 1 when any of short/long/handles/adjectives/room_line/detail changed; weight/chance/terrains/tags edits do not bump it.
  // The client-sent version is ignored.
}
message SaveNeighborPhraseRequest { string token = 1; NeighborPhraseRecord phrase = 2; bool create = 3; }
message DeleteLibraryEntityRequest { string token = 1; LibraryEntityKind kind = 2; string id = 3; }
message ResetLibraryRequest { string token = 1; bool confirm = 2; }

message LibraryEditReply {
  AdminResult result = 1;            // existing enum (OK / REFUSED / INVALID / AUDIT_FAILED ...)
  repeated FieldError errors = 2;    // existing message
  int32 library_version = 3;         // new value after the write
  string message = 4;                // fixed sentence
}

message MapSummary { string name = 1; string prefix = 2; int32 room_count = 3; int32 stored_room_count = 4; bool has_errors = 5; }
message ListMapsReply { repeated MapSummary maps = 1; }
message GetMapMetaRequest { string token = 1; string map = 2; }
message LegendEntry { string glyph = 1; string terrain = 2; string color = 3; bool walkable = 4; }   // color empty if none
message GetMapMetaReply {
  bool found = 1; string map = 2; string prefix = 3; string seed = 4;
  repeated LegendEntry legend = 5;   // the ONLY place color leaves the map file (OD2)
  repeated string errors = 6;        // map parse errors, if any
}

message RegenerateMapRequest { string token = 1; string map = 2; bool dry_run = 3; string new_seed = 4; }
message RegenerateRoomsRequest { string token = 1; repeated string keys = 2; bool dry_run = 3; string new_seed = 4; }
message UpdateRoomsRequest { string token = 1; repeated string keys = 2; bool dry_run = 3; }

enum RoomGenOutcome {
  ROOM_GEN_OUTCOME_UNSPECIFIED = 0; ROOM_GEN_REPLACED = 1; ROOM_GEN_UNCHANGED = 2; ROOM_GEN_NOT_OWNED = 3;
  ROOM_GEN_MAP_NOT_FOUND = 4; ROOM_GEN_ERROR = 5; ROOM_GEN_UPDATED = 6; ROOM_GEN_NOT_GENERATED = 7;
}
enum FeatureUpdateOutcome {
  FEATURE_UPDATE_OUTCOME_UNSPECIFIED = 0; FEATURE_UPDATED = 1; FEATURE_CURRENT = 2;
  FEATURE_TEMPLATE_DELETED = 3; FEATURE_NEWER_IN_ROOM = 4; FEATURE_CONFLICT = 5;
}
message FeatureUpdateLine { string feature_short = 1; string template_id = 2; FeatureUpdateOutcome outcome = 3; int32 from_version = 4; int32 to_version = 5; }
message RoomGenLine { string key = 1; RoomGenOutcome outcome = 2; string detail = 3; repeated FeatureUpdateLine features = 4; }
message GenerationReply {
  AdminResult result = 1;
  repeated string errors = 2;         // phase-1 / map errors; when non-empty nothing was written
  repeated RoomGenLine rooms = 3;
  repeated string orphans = 4;        // map regen only: rooms of the prefix no longer in the map (never deleted)
  bool dry_run = 5;
  int32 library_version = 6;
}
```

Notes:
- Audit: library edits audit per changed field like room edits (D34); `RegenerateMap`, `RegenerateRooms`, `UpdateRooms` write one audit row per
  room changed (`Applied` marked after the importer returns), field `*regen*` / `*update*` with a short summary, not the full old text
  (so a regen of a whole map is bounded; Q12 asks if the old values should be retained for undo).
- Long-running: a whole-map regen runs on the game thread in batches; the call returns only at the end (unary, D66). Maps of thousands of rooms are
  acceptable; larger needs a job/poll API (Q13).
- The existing `RoomFeature` message gains `string template_id = 8; int32 template_version = 9;` (read-only for web edits: `UpdateRoom` keeps the stored
  values for matched features; a client cannot set them) and `RoomDetail` gains `RoomGen gen = 10` (`message RoomGen { string map; string terrain;
  string seed; int32 lib_version; }`) so the room editor can show provenance and offer Regen/Update.
- Web edit of a generated room's Features through `UpdateRoom` keeps working (last write wins); a hand-edited feature that keeps its `templateId` will be
  overwritten by update when the template is newer — by design (OD9 tracks the reference, not an edit flag).

## 11. Web pages (MUD.Web, Razor Pages, `CreatorOrAdmin` policy)

All through `IAdminClient`; the web never reads Mongo.

1. **`/creator/terrain`** — library list with tabs Fragments / Features / Neighbour phrases / Unassigned; filter by terrain and tag; shows
   weight, and for features the version. Row actions Edit / Delete / "assign to terrain". "Unassigned" shows every entity whose `terrains`
   is empty (OD5) with a banner "Not used by any generator".
2. **`/creator/terrain/edit?kind=&id=`** — one form per entity kind; `terrains` as a multi-select of `known_terrains` plus free text; preview of
   the phrase with `{dir}` filled in; features show "saving text changes bumps the version from N to N+1".
3. **`/creator/maps`** — maps from `ListMaps`, with the legend colour swatches from `GetMapMeta`; buttons "Dry run" and "Regenerate and re-import
   (all rooms of this map)" with a confirmation that hand edits will be lost (OD8/OD10); the result table is `GenerationReply`.
4. **`CreatorMap` (existing)** — rooms are coloured by `gen.terrain` using the colour from `GetMapMeta` for the room's `gen.map`; hand-authored rooms keep
   the current look. The colour is never persisted on rooms (OD2). Needs `RoomDetail.gen` (section 10).
5. **`RoomEdit` (existing)** — for a generated room, a "Generated" panel: terrain, map, seed, libVersion; buttons "Regenerate this room" and "Update
   features from library" (each with a dry-run link) calling `RegenerateRooms` / `UpdateRooms`. The Features list shows `templateId@version` for
   referenced features.

Nav: add "Terrain" and "Maps" links to the Creator page. Layout/styling follows the existing pages; no new client framework.

## 12. Tests expected from the implementation cards

- `MapGenerator`: existing `MapGeneratorTests` keep passing for maps that do not use the library; new tests for legend mapping form, bad colour,
  `x-gen` output, neighbour phrase merging/cap, determinism (two runs byte-identical; changing a far cell does not change this room's text).
- Library seed: valid file loads; each invalid case of 6.2 is rejected; second start does not reseed.
- Importer: `Replace` replaces features (hand-added gone), behaviours, properties; a hand-authored room at a map key is reported `NotOwned`; phase-1 errors
  write nothing; `Unchanged` when regenerated with identical output (idempotent).
- Update: each outcome of 9.3 (updated / current / newer-in-room / deleted / conflict / unreferenced untouched; description untouched).
- Admin API: roles, audit rows, `terrains: []` semantics (an entity with empty terrains is never selected by a generator).
Mongo is unavailable in CI sandboxes; repository-level behaviour needs the in-memory fakes (`MUD.Tests/InMemoryRepositories.cs`) and a clear note of what was not run against Mongo.

## 13. Proposed card split (for the orchestrator; not created here)

1. Library: model, Mongo collections, seed file + loader + `ITerrainLibrary` snapshot, version counter.
2. Generators: `ITerrainGenerator`/`GenContext`/`Terrain`/`TerrainRegistry`, legend mapping + `color`, neighbour phrases, `x-gen` emission, tests.
3. Importer + model: `gen` / `templateId` / `templateVersion` fields, parser support, `ReplaceGenerated.Replace`, `FeaturesOnly`, `ApplyDefinition`
   gap (Behaviors, Gen).
4. Admin proto + `TerrainAdminApi` (regen/update/library CRUD/map meta) + audit.
5. Web pages.
Order: 1 and 3 in parallel, then 2, then 4, then 5.

## 14. Open questions

Not decided by the owner or the code; each has a proposed default used in the text above.

1. **Feature template fields.** The owner's field list for a feature template omits `short`, `long` and `tags`; a feature needs `short` and `long`
   to pass phase 1 (`AreaParser.Text(..., required)`). Spec adds `short`, `long` and `tags`. Confirm, or say where `short/long` come from (e.g. `detail`
   as `long` and `adjectives+handle` as `short`).
2. **Map-file templates vs library text.** Section 8.2 lets existing map templates keep working and uses the library only when a terrain has no map-file
   text. Alternative: library is the only source for generated terrains. Confirm.
3. **Seed file location** in the repo and how it reaches the runtime (content-file copy vs embedded resource). Proposed: embedded resource, so a fresh install never lacks it.
4. **Reset to seed.** Is `ResetTerrainLibrary` wanted at all (destroys admin edits)? Proposed: yes, admin only, with `confirm`.
5. **Replacing behaviours on live rooms.** `ApplyDefinition` does not copy `Behaviors`; fixing it is in card 3. Confirm that replacing behaviors of a generated
   room on re-import is intended (OD8 says "properties"; behaviors are not named). Proposed: yes, same treatment as properties.
6. **All-eight-neighbours wording** ("all around"). Add a `{dir}`-less variant field to phrases, or leave the awkward sentence?
7. **Legend colour per terrain vs per glyph.** Several glyphs can share a template. Proposed: colour is per terrain; first wins with a warning.
8. **Search cooldown on replace.** Replacing properties resets `nextRewardAt` (state inside a replaced element). Alternative: keep cooldowns of
   entries with the same id. Proposed: reset, as the room is regenerated.
9. **Orphans.** Re-import never deletes rooms that left the map (importer rule). Offer a delete/archive action later? Out of scope here.
10. **Map name vs prefix.** Map identity is described as "prefix" (OD10) while `gen.map` is the file name; today they are independent (fact 3). Proposed: warn when different; keys keep following the prefix.
11. **Who performs UPDATE writes.** Proposed: importer `FeaturesOnly` mode (keeps OD11 literal). Alternative: a narrow `GeneratedRoomStore` in core that writes only `Features`.
12. **Undo.** Regen/update lose the previous text by design (OD8); audit rows are bounded summaries. Keep full old values for a short window?
13. **Very large maps.** One unary call may be slow; a job/poll rpc is not specified.
14. **`libVersion` semantics.** Only informational here (section 6.4). Confirm it is not meant to drive an "out of date" listing.
15. **Fresh-install detection.** "No `terrain_meta.library` document" is used as the signal. Confirm it matches the owner's intent (versus a config flag).

## 15. Implementation status: generators (card t_6eb48f02, branch tgregistry)

Implemented in `MUD.Core/Generation/Terrains/` and `MapGenerator.cs`; tests in `MUD.Tests/TerrainGeneratorTests.cs`. Differences from the sketches above:

- Namespace is `MUD.Core.Generation.Terrains` (plural, so `Terrain` is not a namespace/class clash). The library abstraction is named `ILibrary` (sketched as `ITerrainLibrary`); the only implementation here is `InMemoryLibrary` (tests). The Mongo-backed library and seed loader are card 1 of section 13, not built here.
- `GenContext` also carries `Template` (the map-file `templates:` entry). `GeneratedRoom` has no `Properties`/search list yet.
- Opt-in: `MapGenerator.Generate(mapYaml)` is unchanged (no library, no `x-gen`, byte-identical output to before). `MapGenerator.Generate(mapYaml, mapName, library, registry = null)` is the terrain-aware entry point; it emits room `x-gen` (`map`, `terrain`, `seed`, `libVersion`; `genVersion` not emitted yet) and, on generated features, feature `x-gen: {templateId, templateVersion}`.
- Generic `Terrain` draw order: map-template short, map-template long pools (exactly as before), then library fragments only where the template has none (`short` slot, then `Slots`, default `ground`), `Orientation` hook, neighbour phrases, features. Neighbour phrases and features are added whenever a library is supplied, even to templates with map-file text (a library-less run is the only mode that is identical to the old output).
- Neighbour phrase draws: one weighted pick per neighbour terrain that has phrases, in first-`Dir` order, all drawn before the cap is applied; ranking by side count then `Dir` then terrain ordinal. Generic cap `MaxNeighborSentences` = 2, `MaxFeatures` = 2.
- Empty `terrains` rule is enforced twice: `InMemoryLibrary` never returns such entities, and `Terrain` filters again so a misbehaving `ILibrary` cannot leak them.
- Legend: scalar or `{template, color}`. `color` must match `#RRGGBB` (stored upper-case); exposed as `GenerateResult.Colors` (terrain id -> colour), never in the YAML. Two different colours for one terrain: first wins, a message goes to `GenerateResult.Warnings`.
- Not done (other cards): `RoomDefinition.Gen`/`FeatureDefinition.TemplateId` parser fields and storage (the parser still skips `x-` keys, so provenance is emitted but not yet persisted), importer `Replace` mode, `MapFileService`/`genmap` wiring, `TerrainRegistry` bespoke classes (registry ships empty).
