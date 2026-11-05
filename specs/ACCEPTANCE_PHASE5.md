# Phase 5 acceptance: the desert and temple modules run for real (card P5-3)

Status: written by card P5-3. This is the acceptance run of phases 2 to 4, using the desert module (P5-1, `MODULE_DESERT.md`) and the
temple module (P5-2, `MODULE_TEMPLE.md`). It records what was done, what happened, and what was found. It decides nothing: every item
under "Findings" is either a defect (filed as a backlog card) or a question for the owner (also listed in CORE_API section 10).

## What was run, and what was not

Run for real, on one machine, all in one session:

- a private MongoDB 4.4 (`mongod` on 127.0.0.1, an empty database, not the live one);
- the core (`MUD.Main`, published) with the telnet listener on a spare port;
- the two modules, published as `modules/desert/desert` and `modules/temple/temple`, started and supervised by core's own supervisor
  (directory scan, `--describe`, per-spawn token, grants file), not by hand (D67, D69);
- two real telnet clients (a builder who is creator and admin, and an ordinary player) driven by `scripts/telnet_session.py`.

Not run: load; more than two simultaneous players; the web front end; a core crash (only module crashes were exercised); the systemd
units; a module with the `required` flag (neither module sets it).

## Setting up the run

1. Publish core and both modules (`dotnet publish MUD.Main -o <dir>`, `dotnet publish MUD.Modules.Desert -o <dir>/modules/desert`,
   `dotnet publish MUD.Modules.Temple -o <dir>/modules/temple`). Copy `MUD.Main/Content/desert.yaml` and `temple.yaml` to the content
   directory (a publish already does this for `Content\**\*.yaml`).
2. Write a grants file (D65), mode 600, and point `ModuleHost:GrantsFile` at it. The capability lists are in `MODULE_DESERT.md` and
   `MODULE_TEMPLE.md`.
3. Write `desert-settings.json` beside the desert program, naming the start room of the storm. The acceptance run used:
   `{ "startRooms": ["desert.dune_1"], "spawnIntervalSeconds": 20, "stormDurationSeconds": 60, "stepSeconds": 5, "maxSteps": 12, "blindSeconds": 20 }`
   (short numbers so that the run is quick; the module defaults are 1800 s, 240 s, 10 s, 12 steps, 30 s).
4. Start an empty database, create two accounts, and give the builder the `roles` component
   (`{ creator: true, admin: true }`). See finding F1: `scripts/seed-admin.sh` still writes the old properties, so the roles were set
   with one `updateOne` in the mongo shell.
5. Start core with `MUD_MONGO_URI` and `MUD_RUN_DIRECTORY` set. Core prints that both modules started, connected with the granted
   capabilities, reconciled (`0 storm(s)`, `0 spent trap(s)`), and are Ready.

## The builder walkthrough (creator commands)

`scripts/phase5-builder.txt`, run as the builder over telnet. This is "how module-needed content gets created" for now; the open item
in the decisions file stays open.

| Step | Command | Result |
|---|---|---|
| Rooms | `import sample_area`, `import desert`, `import temple` | 3 + 4 + 5 rooms inserted |
| Joins between the areas | `terraform link <room> <exit>` from each side (4 commands) | exits made; see F2 and F3 |
| Storm rooms (4 times, one per dune) | `behavior here add heartbeat desert DesertStormRoom {"next":["desert.dune_2"],"step_s":5}` | Added |
| Scouring (4 times) | `behavior here add living.enter desert DesertStormExposure {"blind_s":20}` | Added |
| Lever | `behavior here add command.entered temple TempleLeverRoom {"verb":"pull","noun":"lever","group":"main","traps":["temple.corridor_2","temple.corridor_3"],"rearm_after_s":60}` in the antechamber | Added |
| Traps (2 times) | `behavior here add living.enter temple DesertTempleTrap {"pit_room":"temple.pit","group":"main","rearm_after_s":60}` in each corridor | Added |
| Check | `behavior here list`, `modules list` | `desert: Ready`, `temple: Ready` |

Why the rooms are in area files and the behaviors are not: the importer carries `behaviors:` entries, but D39 says the database is the
source of truth and the cards promised creator commands, so the files hold rooms only. `MUD.Tests/Phase5ContentTests.cs` keeps the two
files importable and checks that they carry no behavior entries.

Hook choice that matters to a builder: `DesertStormRoom` must be on EVERY dune the storm may reach, because core delivers no heartbeat
to a room without a heartbeat entry (D56). Each dune names the next one in `next`; the module cannot see exits.

## The end-to-end scripted telnet session

`scripts/phase5-player.txt`, run as an ordinary player standing in the starting hall. Real output (colour codes removed, exit lists left
out):

```
>>> east                       the first dune (desert.dune_1)
>>> east                       the second dune (desert.dune_2)
>>> look                       ... A sandstorm rolls in.
>>> east / east / east         third dune, fourth dune, a sandy antechamber (temple.antechamber)
>>> east                       The floor gives way!
                               walker vanishes through the floor!
                               a dusty pit (temple.pit)
>>> up                         a sandy antechamber (temple.antechamber)
>>> pull the lever             Command not recognized.
>>> pull lever                 You haul the lever. Somewhere deep in the stone, gears grind.
                               walker pulls a lever. Gears grind in the walls.
>>> pull lever                 The lever will not budge.
>>> east / east / east         corridor_2, corridor_3, the inner sanctum   (both traps disarmed)
>>> west / west / west         back to the antechamber
```

Seen in other runs of the same world: after a storm arrived in the room, `look` listed `A wall of swirling sand roars through here.` (the
Feature, D71/D72) and `look` in the next room did not; walking into a storm room gave `Stinging sand scours your face.`; the component
`desert.sand_blind` appeared on the living (`components.desert.sand_blind { until, storm_id }` in MongoDB) and a timer removed it.

## The two-player race (G6, D62), live

Two telnet clients stood in the antechamber; both sent `east` into the armed corridor at the same instant (a start time shared by two
threads). Result: the first delivery fell through the floor; the other walked into `temple.corridor_2` and was not trapped. The state
document `temple/traps` afterwards held `temple.corridor_2` once, spent. The trap fired once. G6 behaves as decided: the module
serialises itself, no precondition field was needed. Which of the two players is "first" is arrival order at the module, as D7/D62
allow.

## The crash and restart exercises

Both done with `kill -9` on the module process while core kept running, with state read from MongoDB every 5 seconds.

**Desert, mid-storm.** A storm was in `desert.dune_4`, 3 steps in, 40 s from its end. `kill -9` at T. Core logged
`Module 'desert' disconnected`, `ended (the connection to core was lost). Restart 1 of 4 allowed in 300 s, in 1 s`, and, for the
dead interval, `Dormant behavior entries skipped (4 in this interval...)` (the four storm rooms; fail open, nobody was blocked). The new
process was running at the next 5 s sample (pid changed), logged `Desert reconciled: 1 storm(s), 0 scoured living(s).`, and the storm carried on
from the state document: `steps` continued 4, 5, 6, 7, 9 and the component moved room to room. The storm ended on schedule at its
`ends_at` (T + 40 s) with the component removed from every room, and the spawner began the next storm 300 ms later. No storm was
duplicated, none was lost, no component was left behind.

**Temple, lever pulled.** The lever was pulled (state documents `levers` and `traps` written), `kill -9` 0.1 s later. The new process
reconciled; its first `pull lever` gave `The lever will not budge.` (still pulled, correct). The two corridors were still disarmed and
walkable. After the 60 s rearm time the documents were empty, and the next walk through the corridor sprung the trap. A trap spent before
the kill (rearm 60 s) was likewise rearmed on time after the restart: the rearm timers came back from the state documents (D74).

Two things seen during the desert exercise are in the findings (F4 and F5).

## Findings

Defects found in code outside this card's scope are filed as backlog cards and not fixed here. Questions go to the owner, none is decided.

| Id | What | Where | Action |
|---|---|---|---|
| F1 | `scripts/seed-admin.sh` writes the Creator and Admin `ExtendedProperties`, which core no longer reads (D45 moved them to the `roles` component). A seeded admin is a plain player until `roles` is set by hand. | `scripts/seed-admin.sh` | Backlog card. |
| F2 | `terraform link <room> <exit> <return exit>` checks the return room for an existing exit named like the FORWARD exit (`x.Name == newName`), not like the return exit. Linking `east`/`west` is refused with `already has an east exit` whenever the other room has an east exit, even though it was `west` that was wanted. The builder script therefore links each cross-area exit from its own side with no return name. | `MUD.Core/Commands/Creator/TerraformCommand.cs`, return-exit branch | Backlog card. |
| F3 | An area import refuses an exit to a room that does not exist yet, so two areas that lead into each other cannot both be imported with their joining exits; the joins need `terraform link` afterwards. This is the import collision/validate-plan-apply item the decisions file lists as open. | `AreaImporter` | Recorded; open item, not closed here. |
| F4 | When the offline player's blindness ended, the module's `component.remove` was refused (`target entity not found`), because core can only address entities it has loaded. The state document forgot the player, so `desert.sand_blind` stayed in the player's saved document for good. Harmless to play (nothing reads it) but a stale component. The orphan sweep that would catch it needs `QueryEntities` (F6). | desert module and core's emit path | Recorded; question R2 in CORE_API section 10. |
| F5 | After a restart the module re-sends `feature.add` for a storm Feature that is already there. Core refuses the duplicate by answering false, and logs `Event request feature.add could not be carried out`, which reads like a failure. The world is correct. | `ModuleEmitActions.RunFeatureAdd` and the log line in `EventDispatcher` | Recorded; wording only. |
| F6 | `GetEntity` and `QueryEntities` still answer Unimplemented. Both modules log that their orphan sweep is skipped, on every start. D58 decided these reads; they are not built. | `ModuleHostService` | Open: needs a card. Not filed here. |
| F7 | A storm may be started in a room that does not exist (a wrong `startRooms` entry): every emitted event is refused (`target entity not found`), the module still counts the storm as running, and nothing tells the builder except the core log. The module cannot check without `GetEntity` (F6). | desert module settings | Recorded. |
| F8 | `DesertModuleTests.AfterARestartMidStormTheTimersComeBackAndTheStormEndsOnSchedule` failed twice in 5 full runs (found by the P5-2 review). It passed in the one full run made for this card (one run is too few to say more). Timing-dependent assertion. | `MUD.Tests/DesertModuleTests.cs` | Backlog card. |

Not found, checked on purpose: no player was ever blocked while a module was down or restarting (fail open held); the second player of
a race was never trapped twice; the supervisor restarted a `kill -9` module after its first backoff step (1 s; the new process was seen at the next 5 s sample);
a `modules restart desert` command replaced the module and its storm carried on.

## What the plan budgeted: the revision pass on CORE_API

CORE_API section 10 lists what this run showed about the contract: the points that need an owner answer, with no recommendation
applied. It does not change any decided text.

## Files of this card

`MUD.Main/Content/desert.yaml`, `MUD.Main/Content/temple.yaml`, `MUD.Tests/Phase5ContentTests.cs`, `scripts/telnet_session.py`,
`scripts/phase5-builder.txt`, `scripts/phase5-player.txt`, this file, and `specs/CORE_API.md` (section 10 added).
