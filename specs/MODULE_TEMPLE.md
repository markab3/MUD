# The temple module (card P5-2)

Status: built in phase 5 card P5-2. Implements MODULES section 8 (example B) as a real module, with the changes the later decisions made
(D54, D57, D58, D60, D62, D63, D64, D65, D67, D69, D73, D74). Project `MUD.Modules.Temple`, assembly name `temple`, so it publishes as
`modules/temple/temple` (D67).

## What it does

Walking into a trapped room drops you into a pit and spends the trap; a lever in the antechamber disarms a group of traps; everything
rearms after a while. It is flavor only: when the module is down nothing is blocked and no trap fires (D60, fail open).

| Behavior type | Hook | Config | What it does |
|---|---|---|---|
| `DesertTempleTrap` | `living.enter` (Before phase) | `{ "pit_room": "temple.pit", "group": "main", "actor_msg": "The floor gives way!", "room_msg": "vanishes through the floor!", "rearm_after_s": 600 }` | On a trapped room. An armed trap answers Deny with `actor_msg` and emits a redirect (D6): `entity.move` of the walker to `pit_room` (reason `trap`), `component.set temple.trap {armed:false}`, and a room message to the room being left (`<name> <room_msg>`). A spent trap answers Allow. |
| `TempleLeverRoom` | `command.entered` | `{ "verb": "pull", "noun": "lever", "group": "main", "traps": ["temple.corridor_2", "temple.corridor_3"], "rearm_after_s": 600 }` | On the room that holds the lever (room level, D14). A line that is exactly `verb noun` (any case) is taken with the handled flag (D63); every other line is answered Allow, not handled, so core carries on. The first pull disarms every room in `traps`; a pull while the lever is pulled gets `The lever will not budge.` |

Defaults when a Config field is missing: `rearm_after_s` 600, `actor_msg` and `room_msg` as above, `verb` `pull`, `noun` `lever`. A trap with no
`pit_room` (or one that names its own room) logs a line and does nothing.

Why the trap is on `living.enter` and not on a separate move hook: D54 split the move into `living.exit` and `living.enter`, and the trapped room
is the room being entered. A redirect carries reason `trap` and the trap ignores such a move, so a pit that itself carries a trap cannot loop.

Attach them with the creator command (how module-needed content gets created is still an open item; this is the documented way until it is decided):

```
behavior here add living.enter temple DesertTempleTrap {"pit_room":"temple.pit","group":"main","rearm_after_s":600}
behavior here add command.entered temple TempleLeverRoom {"verb":"pull","noun":"lever","group":"main","traps":["temple.corridor_2","temple.corridor_3"],"rearm_after_s":600}
```

The antechamber's long description should mention the lever, because Features are inert (D14) and the lever is not one.

## The two-player race (G6, D62)

A module is one process and the SDK handles one delivery at a time. The trap marks itself spent in memory before its first await, so when two
players enter at once (both Before chains were started before either was answered) the second delivery finds the trap spent and answers Allow;
that player walks in. The lever behaves the same way: two simultaneous pulls disarm once and the second puller is told the lever will not budge.
No precondition field on component writes and no contract change. Tested with two moves started before either is answered.

## Where state lives (D13)

Snapshots carry core components only (D57) and core answers `GetEntity` and `QueryEntities` with Unimplemented today, so the module decides from
its own state documents and never reads a component back.

| Data | Home |
|---|---|
| `temple.trap { armed, group, fired_by? }` on a trap room | room component (shown to builders and other behaviors) |
| `temple.lever { pulled }` on the lever room | room component |
| state document `traps`: `{ "<trap room id>": { group, rearm_at } }` | module-state document; a room listed here is spent, absent means armed (times are Unix milliseconds) |
| state document `levers`: `{ "<lever room id>": { group, rearm_at } }` | module-state document |

State is written before the components that mirror it. A trap that was never touched has no `temple.trap` component and counts as armed.

## Start-up reconciliation (idempotent, D13, D74)

On every start the module reads both documents; a spent trap or pulled lever past its rearm time is put back to armed or ready at once, the
others are shown as spent again and get their rearm timer back (timers do not survive a restart, D74); then it sweeps `QueryEntities` for
`temple.trap` and `temple.lever` on rooms the documents do not know and resets them (skipped and logged while core answers Unimplemented); then
it saves the documents. Running it twice changes nothing.

`timer.fired`: `rearm-trap-<room id>` rearms a trap, `rearm-lever-<room id>` resets a lever. A timer for something not spent does nothing; a timer that
fires before a later rearm time (spent again since) schedules itself again for the later time.

Pulling the lever while a trap is already spent keeps the later of the two rearm times for that trap.

## Capabilities (D65)

```yaml
modules:
  temple:
    - emit:entity.move
    - emit:component.set
    - emit:message.send
    - state
    - timer.schedule
    - read
```

Without `state` or `timer.schedule` the module starts, logs why, and does nothing (every entry answers Allow). Without `read` the sweep is skipped.

## Tests

`MUD.Tests/TempleModuleTests.cs` runs the real module through the real SDK against core's real module host over a Unix domain socket (the P4-5
harness). Covered: manifest, trap spring and its effects, spent trap, the two-player race, no loop through a trapped pit, missing pit room, trap
rearm, lever disarm and consumed line, lines that are not the lever, double pull, joint rearm, restart while spent, restart after the rearm time,
repeated reconciliation, stray timer, missing capabilities, skipped sweep. Not covered: MongoDB, a published module started by core's
supervisor, real telnet play (the lever is tested through a sequential command.entered chain, not through a Player), load.

## Flagged choices (the owner has not set these)

- Default rearm time 600 s and all message wording come from the worked example; they are the implementer's.
- The trap is on `living.enter`, not a single `living.move` (D54).
- The lever matches the exact two-word line `verb noun`; `pull the lever` is not taken.
- The lever is room level, not an item (D14); an immovable-item lever needs no module change, only the entry attached to the item, because
  command.entered offers items in the room (D63), but the module reads the lever room from the `room` party and would need the item's room instead. Not built.
- A taken lever line is answered Allow with the handled flag and its text goes out as a message event; Deny is not used for it (D63).
- The trap's component carries `fired_by` (the walker's id) while spent.
