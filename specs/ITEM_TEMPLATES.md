# Item templates

Cards t_ae0cd319 (library, admin rpcs, website pages) and t_0c2a2723 (references: search rewards, `clone`, delete protection, stale report). Status: built.

Owner request: "Maintain a library of item templates that I can reference. These should be organised by area." The decisions below were made by the
concierge, not the owner; the owner can change any of them.

## Entity

An item template is a Mongo document (collection `item_template`) holding the authored definition of an item. It is not an item: it is not loaded
into the game, no `InventoryItem` is created from it, and the inventory runtime classes are unchanged.

| field | rule |
|---|---|
| `id` (`_id`) | `<area>.<local>`, like a room key: area prefix grammar (`RoomRules.CheckAreaPrefix`), a dot, a local key `^[a-z0-9_]+$`; at most `RoomRules.MaxKey` characters; unique; never changes |
| `area` | must equal the id's prefix and have an area record |
| `short`, `long` | the room rules (`RoomRules.CheckShort`, `CheckLong`): short one line up to 100, long up to 4000 |
| `handles`, `adjectives`, `plurals` | lists of words, null-safe (null = empty, never a NullReference; see the Plurals bug fixed in PR 109), at most 32 entries, entry at most 64 characters, no repeats |
| `components` | optional free JSON object, at most `BehaviorRef.MaxConfigBytes` (4096) BSON bytes; stored as a document |
| `behaviors` | optional list, the `BehaviorRef` shape: `hook`, `module`, `type`, `priority`, `config` (+ `schemaVersion`), at most 20, each validated by `BehaviorRef.Validate`; not checked against the running modules |
| `version` | integer, 1 on create, +1 on every update that changes a field; so a later copy of a template can be recognised as stale |

Field names and words in this document follow `InventoryItem` / `GameObject` (short, long, handles, adjectives, plurals, components, behaviors).

## Relation to areas

The area is the key prefix, exactly as for rooms, so templates are organised by area without a separate grouping field: the list groups by `area`, and a
new template picks its area from the area dropdown (`ListAreas`) and types only the local key. Creating a template whose area has no record is refused
(`INVALID`, field `Area`), the same backstop as `CreateRoom`. Maps are not areas (the generator's map names are separate).

## Rules in short

- Admin rpcs (`ListItemTemplates` with an optional area filter, `Get`, `Create`, `Update`, `Delete`) for a creator or admin, audited first; see
  specs/ADMIN_API.md "Item templates". The website is `/creator/items` and `/creator/items/edit`; see specs/WEB.md "Item templates pages".
- Nothing is seeded automatically.
- No new behaviors, and no new command classes: `clone` (below) is the existing stub; `proto` is still an empty stub.

## References (card t_0c2a2723)

"Reference" means content and creators point at a template by id instead of repeating the item definition. The decision (the concierge's, the
owner can change it) is **copy, not live link**: whatever refers to a template gets a copy of its fields, so gameplay never depends on a lookup.

- **Search rewards in area files.** `reward: {template: <area>.<local>, short?, long?}`. The importer copies the template's current fields,
  components and behaviors into the stored reward and records `templateId` and `templateVersion` on it. Unknown template = a phase 1 validation
  error naming the id. Full rules: specs/ROOM_FILES.md section 4.5 "Reward from an item template". Stored on `search.entries[].reward` as
  `templateId` / `templateVersion` (the `InventoryItem` fields of the same names; absent for an inline reward).
- **`clone <area>.<local>`** (creator command, the existing `CloneCommand`): puts a new item copied from the template into the creator's
  inventory (short, long, handles, adjectives, plurals, components and behaviors) and saves the player. An unknown id answers `No such item
  template.`; a non-creator is refused as before; no id gives the syntax line. The clone records no `templateId`: it is a loose item, not a
  tracked reference. The command reads the library through a lookup function (`Func<string, ItemTemplateEntry>`): the public constructor
  builds it from `IMongoDatabase` (World's DI scan supplies the database, as for `import`), an `internal` constructor takes the function
  directly (the seam of `regen` / `update`), so tests need no Mongo. Core could read the Mongo library from a command cleanly this way; no
  other seam was needed.
- **Referenced by.** The references are the stored search rewards that carry the template's id. `DeleteItemTemplate` is refused (`REFUSED`,
  "This item template is still used by N search rewards. Nothing was changed.") while any exists; the count comes from a scan of the rooms'
  `search.entries[].reward.templateId` (`IAdminQueries.RoomsWithTemplateReward`, read by `ItemTemplateReferences.Scan`). If the rooms cannot be
  read the delete answers `FAILED` (an unknown count is not "none"). The only reference kind that exists is the search reward; "other stored
  references" do not exist yet, and a new kind must be added to the scan.
- **Stale report.** `UpdateItemTemplate` bumps the version as before. `ListItemTemplateReferences` returns each copy with the version it holds
  and `stale` (its version is lower than the template's current one; a reward with no version counts as 0). The template edit page shows it
  read-only in a "Used by" section (rooms linked to their edit page).
- **Out of scope: refreshing.** Nothing re-copies a template into existing rewards. The only way today is to re-import the area file (which
  takes the template as it is). An `update`-style refresh of stale copies (like `update` for generated features) is a separate card
  (filed as a backlog card, see the card's report).

## Open questions

1. **Other references.** Features of area files, room items and shop stock still embed full definitions; only the search reward can point at a
   template. A feature `template:` would need a shape decision (features are not items).
2. **`proto`.** Should it save an item as a template? Left as a stub.
3. **Deleting an area with templates.** `DeleteArea` counts only rooms, so an area can be deleted while it still has templates, leaving them in a
   group without an area record (the list still shows them, named by prefix). Should delete be refused like it is for rooms?
   `IItemTemplateStore.CountInArea` exists for this.
4. **File form.** ROOM_FILES.md open question 5: should templates also be YAML files (import and export)? Not built.
5. **Component data schema.** It is free JSON today. Should the known component types be offered or checked?
6. **Moving a template to another area** means a new id (the id is the key); there is no move rpc, as the area is the prefix.
7. **Concurrent edits.** Last write wins; the version only increases. The website does not keep unsaved drafts for this page.
