# Item templates

Card t_ae0cd319. Status: built (library, admin rpcs, website pages). Nothing references a template yet.

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
- No new commands (`proto` and `clone` remain empty stubs) and no new behaviors.
- Delete is NOT yet refused for a referenced template: nothing can reference one yet. The reference card must add that check (and a count in
  `DeleteArea`).

## Open questions

1. **References.** How do rooms, area-file features and search rewards point at a template (a `template:` key beside the inline definition, copy at
   import time, or live reference)? Until this exists, templates are only a catalog. The version field is there to support "this copy is stale".
2. **`proto` / `clone`.** Should these creator commands create an item from a template (or save one)? Left as stubs.
3. **Deleting an area with templates.** `DeleteArea` counts only rooms, so an area can be deleted while it still has templates, leaving them in a
   group without an area record (the list still shows them, named by prefix). Should delete be refused like it is for rooms?
   `IItemTemplateStore.CountInArea` exists for this.
4. **File form.** ROOM_FILES.md open question 5: should templates also be YAML files (import and export)? Not built.
5. **Component data schema.** It is free JSON today. Should the known component types be offered or checked?
6. **Moving a template to another area** means a new id (the id is the key); there is no move rpc, as the area is the prefix.
7. **Concurrent edits.** Last write wins; the version only increases. The website does not keep unsaved drafts for this page.
