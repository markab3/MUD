# Living stats: vitals, experience and ability scores

## Vitals and conditions

**Status: stored values only. None of the behaviour below is implemented yet.** There is no death, exhaustion, stun or
unconsciousness behaviour, no damage, no regeneration, no stat effects and no command that changes these values. The fields
exist so a later card can build on them. The only reader is the `status` command, which prints them.

Meanings the owner has set (for the future behaviour):

- **Health** works as HP does. Health at 0 means **death**.
- **Stamina** is how physically tired the living is at that moment; **Alacrity** is how mentally tired it is.
  - Stamina and Alacrity both at 0: completely worn out and **UNCONSCIOUS**.
  - Stamina at 0 alone: **EXHAUSTED**.
  - Alacrity at 0 alone: **STUNNED**.

## Fields (on `Living`, so Players, NPCs and every other Living have them)

| Field | Type | Range and default |
|---|---|---|
| `Health`, `Stamina`, `Alacrity` | `short` | 0..its Max. Default: its Max |
| `MaxHealth`, `MaxStamina`, `MaxAlacrity` | `short` | 1..1000. Default 1000 |
| `Experience` | `long` | 0 or more. Default 0 |
| `Strength`, `Dexterity`, `Intelligence`, `Wisdom`, `Constitution`, `Charisma` | `byte` | 0..255 by type. Default 10 |

Assumptions the owner accepted: the vitals are on a 0-1000 scale (essentially percentages with no decimals) with a
default maximum of 1000; experience is a `long`; the six ability scores are bytes defaulting to 10 (a neutral mid value, easy to change).

## Invariants and persistence

- Setters clamp instead of throwing. A current value is kept within 0..its Max; a Max is kept within 1..1000; setting a Max
  re-clamps the current value (lowering a Max lowers the current value, raising it does not heal); experience below 0 becomes 0.
- Stored as plain fields of the player or NPC document (`Health`, `MaxHealth`, ..., `Experience`, `Strength`, ...). `short` and `byte`
  are BSON int32, `Experience` is int64; no custom serializer.
- A stored document that lacks the fields loads with the defaults above (not zero, which would mean everyone is dead): each
  property carries `[BsonDefaultValue]`. Extra elements are still ignored (`BsonIgnoreExtraElements`, unchanged).
- The Admin API and the website do not carry these fields yet; showing them on the Character page and in the player editor is follow-up work.
