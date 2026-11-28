# NPC templates

Status: foundation only. A data library and a placeholder website page. Nothing in the game reads an NPC template.

The owner's area model: areas have terrain types, item templates, rooms, and NPC templates. This card adds the NPC template entity,
its admin API and a read-only placeholder page. The editor is a later card.

## Entity

Collection `npc_template` (`MUD.Core/NpcTemplates/NpcTemplateStore.cs`, `NpcTemplateRules.cs`). Built by copying the item template pattern
(`specs/ITEM_TEMPLATES.md`).

| field | rule |
|---|---|
| `_id` (id) | `<area>.<local>`; area prefix grammar, then `^[a-z0-9_]+$`; the area must have an area record; immutable |
| `area` | must equal the id prefix |
| `version` | 1 on create, +1 on every update that changes something; read-only for callers |
| `short`, `long` | the room text rules (`RoomRules.CheckShort`, `CheckLong`) |
| `handles`, `adjectives`, `plurals` | null-safe lists (null = empty, stored as empty), words, at most 32 each, no repeats |
| `maxHealth`, `maxStamina`, `maxAlacrity` | optional integer, 0-1000 (Living default 1000) |
| `experience` | optional long, 0 or more (Living default 0) |
| `strength`, `dexterity`, `intelligence`, `wisdom`, `constitution`, `charisma` | optional integer, 0-255 (Living default 10) |

A stat that is not set is not stored and is not on the wire (`optional` in the proto). Not set means "the default of `Living` applies when
something later uses the template"; the defaults are `NpcTemplateRules.Default*`. 0 is a value, not "not set". The bounds equal those of
`Living` (vitals 0-1000, ability scores byte, experience >= 0); a test pins them to `Living`'s constants.

## Deliberately missing

This is a data library only. None of the following exists, and none is to be added without the owner's decisions below:

- AI and behaviors (no behavior list, unlike item templates)
- inventory and equipment, loot
- dialogue
- spawning and respawn
- any link to the in-game `NPC` class (`MUD.Core/GameObjects/NPC.cs` stays an empty subclass of `Living`); no code creates an NPC from a template
- current values (health, stamina, alacrity): a template holds maxima only
- delete protection by references: nothing references an NPC template yet, so a delete is never refused for use. An area that still has NPC
  templates cannot be deleted (the area delete counts them, see `specs/ADMIN_API.md` "Areas").

## Open questions for the owner

1. **Spawn rules.** What spawns an NPC from a template: a room list, an area table, a timer? How many, how often, what happens on death?
2. **AI and behaviors.** Do NPC templates carry behavior entries the way item templates do (`BehaviorRef` with hooks), or something else? Which hooks exist for NPCs?
3. **Equipment and inventory.** Do NPCs start with items, by item template id? Loot on death?
4. **How rooms reference a template.** A field on the room, a spawn table owned by the area, or the spawner above? This decides what a delete of a template must refuse.
5. **How templates map to the `NPC` class.** One class for all NPCs with the template as data, or subclasses per template; and does a spawned NPC keep a link to its template (and version) or take a copy, as item rewards do?
6. **Stats beyond the six abilities.** Race, class, level, alignment, skills: wanted on a template?
7. **Copy or link.** When a template changes, do spawned NPCs follow it?

## Admin API and website

Backend: `specs/ADMIN_API.md` "NPC templates". Page: `specs/WEB.md` "NPC templates page (`/creator/npcs`)". The page is a placeholder:
heading, a notice that NPC templates are not available yet, and a read-only list by area.
