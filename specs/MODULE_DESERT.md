# The desert module (card P5-1)

Status: built in phase 5 card P5-1. Implements MODULES section 7 (example A) as a real module, with the changes the later decisions made
(D50, D57, D58, D59, D60, D61, D62, D65, D67, D69, D71, D72, D73, D74). Project `MUD.Modules.Desert`, assembly name `desert`, so it
publishes as `modules/desert/desert` (D67).

## What it does

A sandstorm appears now and then, drifts from room to room along an authored list, scours the livings who walk into its room, and ends by
itself. It is flavor only: nothing is ever denied, and when the module is down nobody is blocked (D60).

| Behavior type | Hook | Config | What it does |
|---|---|---|---|
| `DesertStormRoom` | `heartbeat` | `{ "next": ["desert.dune_4", ...], "step_s": 10 }` | When the storm is in this room and its step is due, the storm moves to the next room of the list (cycling). `next` is authored adjacency: the module cannot see exits. |
| `DesertStormExposure` | `living.enter` (After phase) | `{ "blind_s": 30 }` | A living who arrives in the storm's room gets a message (channel `combat`), the component `desert.sand_blind`, and a timer that removes it again. A second scour moves the end time forward. |

Attach them to exterior rooms with the creator command (how module-needed content gets created is still an open item; this is the
documented way until it is decided):

```
behavior here add heartbeat desert DesertStormRoom {"next":["desert.dune_4"],"step_s":10}
behavior here add living.enter desert DesertStormExposure {"blind_s":30}
```

## Where state lives (D13)

| Data | Home |
|---|---|
| `desert.storm` `{ storm_id, step_at, intensity }` on the room holding the storm | room component |
| `desert.sand_blind` `{ until, storm_id }` on a scoured living | living component |
| a Feature `a wall of swirling sand` on the storm room, so `look` shows the storm | room Feature (D71, D72); identified by its short text |
| state document `storms`: `{ "<storm id>": { room, ends_at, step_at, steps, intensity } }` | module-state document (times are Unix milliseconds) |
| state document `spawner`: `{ next_spawn_at }` | module-state document |
| state document `blind`: `{ "<living id>": until }` | module-state document |
| start rooms, spawn interval, storm length, step length, maximum steps, blind length | `desert-settings.json` beside the program (module-private config); defaults 1800 s, 240 s, 10 s, 12 steps, 30 s; with no start rooms no storm spawns |

The module keeps its decisions in its own state documents because snapshots carry core components only (D57). Every state write is made
before the components that mirror it, so after a crash the world is behind state and reconciliation brings it level.

## Start-up reconciliation (idempotent, D13, D74)

On every start (first run, crash restart, core restart) `OnStart`:

1. reads `storms`, `blind` and `spawner`;
2. for a storm past its end: removes `desert.storm` and the Feature; for a running storm: re-sets the component and Feature (set and add are
   idempotent: a Feature with the same short text is not added twice) and recreates the end timer (timers do not survive, D74);
3. for a scoured living past due: removes `desert.sand_blind`; otherwise recreates its timer;
4. sweeps `QueryEntities` for `desert.storm` and `desert.sand_blind` on entities state does not know and removes them (D58). Core answers
   `QueryEntities` with Unimplemented today, so the sweep is logged and skipped. A component left behind by a lost state document therefore
   stays until core answers the query;
5. recreates the spawn timer and saves the three documents.

`timer.fired`: `storm-spawn` starts a storm (one at a time) and schedules the next spawn; `storm-<id>-end` ends a storm (a storm that is already
gone does nothing); `blind-<living id>` removes the blindness unless a later scour moved the end time forward.

## Capabilities (D65)

```yaml
modules:
  desert:
    - emit:component.set
    - emit:component.remove
    - emit:message.send
    - emit:feature.add
    - emit:feature.remove
    - state
    - timer.schedule
    - read
```

Without `state` or `timer.schedule` the module starts, logs why, and does nothing. Without `read` the orphan sweep is skipped.

## Tests

`MUD.Tests/DesertModuleTests.cs` runs the real module through the real SDK against core's real module host over a Unix domain socket (the
P4-5 harness). Covered: manifest, settings file, spawn, drift, scour and its end, double scour, storm end and its repeat, maximum steps,
restart mid-storm, restart after the storm should have ended, repeated reconciliation, partial grants. Not covered: MongoDB, a published
module started by core's supervisor, telnet play, load.

## Flagged choices (the owner has not set these)

- Settings defaults (spawn interval 1800 s, storm 240 s, step 10 s, 12 steps, blind 30 s) come from the worked example.
- The Feature text and wording of the messages are the implementer's.
- A storm ends early when it has taken `maxSteps` steps; its end timer then finds nothing to do.
- Core fires no heartbeat for a room unless the room carries a heartbeat entry (D56), so every exterior room that the storm may reach carries
  `DesertStormRoom` (about 15 deliveries per second for 30 rooms, mostly empty; the `behavior.add` variant of MODULES 7.2 is not used).
- `DesertStormExposure` is on `living.enter`; the module has no Before work and answers Allow without reading anything.
