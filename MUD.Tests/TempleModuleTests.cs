using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MUD.Core.Components;
using MUD.Core.Events;
using MUD.Core.GameObjects;
using MUD.Modules.Sdk;
using MUD.Modules.Temple;
using Xunit;
using Mover = MUD.Tests.ModuleTestHarness.Mover;

namespace MUD.Tests
{
    /// <summary>
    /// Card P5-2: the temple module against core's real module host over a real Unix domain socket (see ModuleTestHarness). MongoDB is not
    /// involved: state documents are an in-memory fake repository, and the clock is the harness's fake clock.
    /// </summary>
    [Collection("World construction")] // World registers BSON class maps; constructing it in parallel races
    public class TempleModuleTests
    {
        private const string AllCapabilities = "emit:entity.move emit:component.set emit:message.send state timer.schedule read";

        private sealed class Temple
        {
            public ModuleTestHarness Harness;
            public TempleModule Module;
            public Room Hall, Trapped, Second, Pit, Antechamber;
        }

        private static async Task<Temple> StartTempleAsync(ModuleTestHarness harness)
        {
            var temple = new Temple { Harness = harness };
            temple.Module = new TempleModule(() => new DateTimeOffset(harness.Clock.Now));
            await harness.StartAsync(TempleModule.Define(temple.Module));
            temple.Hall = harness.NewRoom("temple.hall");
            temple.Trapped = harness.NewRoom("temple.corridor_2");
            temple.Second = harness.NewRoom("temple.corridor_3");
            temple.Pit = harness.NewRoom("temple.pit");
            temple.Antechamber = harness.NewRoom("temple.antechamber");
            harness.Attach(temple.Trapped, BehaviorHooks.LivingEnter, TempleModule.TrapType, new BsonDocument
            {
                { "pit_room", "temple.pit" }, { "group", "main" }, { "rearm_after_s", 600 }
            });
            harness.Attach(temple.Antechamber, BehaviorHooks.CommandEntered, TempleModule.LeverType, new BsonDocument
            {
                { "verb", "pull" }, { "noun", "lever" }, { "group", "main" }, { "rearm_after_s", 600 },
                { "traps", new BsonArray { "temple.corridor_2", "temple.corridor_3" } }
            });
            return temple;
        }

        private static Room Where(ModuleTestHarness harness, Mover mover)
        {
            return harness.OnGameThread(() => mover.CurrentLocation);
        }

        private static bool Armed(ModuleTestHarness harness, Room room)
        {
            return harness.OnGameThread(() => { BsonDocument data; return room.TryGetComponent(TempleModule.TrapComponent, out data) && data["armed"].AsBoolean; });
        }

        private static bool HasTrapComponent(ModuleTestHarness harness, Room room)
        {
            return harness.OnGameThread(() => room.HasComponent(TempleModule.TrapComponent));
        }

        private static async Task WaitForWorldAsync(ModuleTestHarness harness, Func<bool> condition, string what)
        {
            await harness.WaitForAsync(() => harness.Pump() >= 0 && condition(), what);
        }

        // Types a line at the room's lever the way core's command.entered chain does: a sequential chain, asked one behavior at a time.
        private static Task<BeforeOutcome> TypeLine(ModuleTestHarness harness, Mover actor, Room room, string line)
        {
            var done = new TaskCompletionSource<BeforeOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
            string[] hook = { BehaviorHooks.CommandEntered };
            var parties = new List<EventParty> { new EventParty("actor", actor, hook, 0), new EventParty("room", room, hook, 2) };
            harness.OnGameThread(() =>
            {
                harness.World.Events.BeginBefore(EventNames.CommandEntered, parties, new Dictionary<string, object> { { "line", line } }, true, outcome => done.TrySetResult(outcome));
                return 0;
            });
            return done.Task;
        }

        // ---- the manifest ----

        [Fact]
        public void TheManifestDeclaresTheTwoBehaviorTypesOnTheirHooksAndNeverRequiresAnything()
        {
            var manifest = TempleModule.Define().BuildManifest();
            Assert.Equal("temple", manifest.Name);
            Assert.Equal(new[] { "living.enter" }, manifest.Behaviors.Single(b => b.Type == "DesertTempleTrap").Hooks.ToArray());
            Assert.Equal(new[] { "command.entered" }, manifest.Behaviors.Single(b => b.Type == "TempleLeverRoom").Hooks.ToArray());
            Assert.Contains("emit:entity.move", manifest.Capabilities);
            Assert.Contains("emit:component.set", manifest.Capabilities);
            Assert.Contains("state", manifest.Capabilities);
            Assert.Contains("timer.schedule", manifest.Capabilities);
            Assert.Empty(manifest.RequiredCapabilities); // flavor module: fail open (D60)
        }

        // ---- the trap ----

        [Fact]
        public async Task AnArmedTrapDeniesTheWalkSendsTheWalkerToThePitAndIsSpent()
        {
            using (var harness = new ModuleTestHarness("temple", AllCapabilities))
            {
                Temple temple = await StartTempleAsync(harness);
                Mover ann = harness.Place(temple.Hall, "ann");
                Mover bob = harness.Place(temple.Hall, "bob");

                harness.StartMove(ann, temple.Trapped);
                await WaitForWorldAsync(harness, () => ReferenceEquals(Where(harness, ann), temple.Pit), "ann in the pit");

                Assert.Contains(TempleModule.DefaultActorMessage, ann.MessagesSoFar());
                Assert.Contains("ann " + TempleModule.DefaultRoomMessage, bob.MessagesSoFar());
                await WaitForWorldAsync(harness, () => HasTrapComponent(harness, temple.Trapped), "the trap component");
                Assert.False(Armed(harness, temple.Trapped));
                await harness.WaitForAsync(() => harness.StateRepository.Docs.ContainsKey("temple/traps"), "the traps document");
                Assert.True(harness.StateRepository.Docs["temple/traps"]["Document"].AsBsonDocument.Contains("temple.corridor_2"));
                Assert.Contains("temple.corridor_2", temple.Module.SpentTraps);
            }
        }

        [Fact]
        public async Task ASpentTrapLetsTheNextWalkerThrough()
        {
            using (var harness = new ModuleTestHarness("temple", AllCapabilities))
            {
                Temple temple = await StartTempleAsync(harness);
                Mover ann = harness.Place(temple.Hall, "ann");
                Mover bob = harness.Place(temple.Hall, "bob");
                harness.StartMove(ann, temple.Trapped);
                await WaitForWorldAsync(harness, () => ReferenceEquals(Where(harness, ann), temple.Pit), "ann in the pit");

                await harness.MoveAsync(bob, temple.Trapped);
                Assert.Same(temple.Trapped, Where(harness, bob));
            }
        }

        [Fact]
        public async Task TwoPlayersEnteringAtOnceOnlyOneFallsTheModuleSerialisesItself()
        {
            using (var harness = new ModuleTestHarness("temple", AllCapabilities))
            {
                Temple temple = await StartTempleAsync(harness);
                Mover ann = harness.Place(temple.Hall, "ann");
                Mover bob = harness.Place(temple.Hall, "bob");

                // Both Before chains start before either is answered: both deliveries carry the same armed trap.
                harness.StartMove(ann, temple.Trapped);
                harness.StartMove(bob, temple.Trapped);

                await WaitForWorldAsync(harness, () =>
                {
                    Room a = Where(harness, ann), b = Where(harness, bob);
                    return (ReferenceEquals(a, temple.Pit) && ReferenceEquals(b, temple.Trapped)) || (ReferenceEquals(b, temple.Pit) && ReferenceEquals(a, temple.Trapped));
                }, "one in the pit and one in the corridor");

                int inPit = new[] { ann, bob }.Count(each => ReferenceEquals(Where(harness, each), temple.Pit));
                int inCorridor = new[] { ann, bob }.Count(each => ReferenceEquals(Where(harness, each), temple.Trapped));
                Assert.Equal(1, inPit);
                Assert.Equal(1, inCorridor);
            }
        }

        [Fact]
        public async Task AFallenWalkerIsNotTrappedAgainWhenThePitItselfHasATrap()
        {
            using (var harness = new ModuleTestHarness("temple", AllCapabilities))
            {
                Temple temple = await StartTempleAsync(harness);
                // The pit leads back into the trapped room by config: the redirect carries reason "trap" and is never trapped itself.
                harness.Attach(temple.Pit, BehaviorHooks.LivingEnter, TempleModule.TrapType, new BsonDocument { { "pit_room", "temple.corridor_2" } });
                Mover ann = harness.Place(temple.Hall, "ann");

                harness.StartMove(ann, temple.Trapped);
                await WaitForWorldAsync(harness, () => ReferenceEquals(Where(harness, ann), temple.Pit), "ann in the pit");
                await Task.Delay(300);
                harness.Pump();
                Assert.Same(temple.Pit, Where(harness, ann));
            }
        }

        [Fact]
        public async Task ATrapWithoutAPitRoomDoesNothing()
        {
            using (var harness = new ModuleTestHarness("temple", AllCapabilities))
            {
                Temple temple = await StartTempleAsync(harness);
                Room bare = harness.NewRoom("temple.bare");
                harness.Attach(bare, BehaviorHooks.LivingEnter, TempleModule.TrapType, new BsonDocument());
                Mover ann = harness.Place(temple.Hall, "ann");

                await harness.MoveAsync(ann, bare);
                Assert.Same(bare, Where(harness, ann));
                Assert.Contains(harness.LogLines(), line => line.Contains("no usable pit_room"));
            }
        }

        [Fact]
        public async Task TheTrapRearmsWhenItsTimerFires()
        {
            using (var harness = new ModuleTestHarness("temple", AllCapabilities))
            {
                Temple temple = await StartTempleAsync(harness);
                Mover ann = harness.Place(temple.Hall, "ann");
                harness.StartMove(ann, temple.Trapped);
                await WaitForWorldAsync(harness, () => ReferenceEquals(Where(harness, ann), temple.Pit), "ann in the pit");
                await WaitForWorldAsync(harness, () => HasTrapComponent(harness, temple.Trapped) && !Armed(harness, temple.Trapped), "the spent trap");

                harness.AdvanceClock(TimeSpan.FromSeconds(601));
                await WaitForWorldAsync(harness, () => Armed(harness, temple.Trapped), "the trap rearmed");
                Assert.Empty(temple.Module.SpentTraps);
                Assert.Equal(0, harness.StateRepository.Docs["temple/traps"]["Document"].AsBsonDocument.ElementCount);

                // Armed again: the next walker falls.
                Mover bob = harness.Place(temple.Hall, "bob");
                harness.StartMove(bob, temple.Trapped);
                await WaitForWorldAsync(harness, () => ReferenceEquals(Where(harness, bob), temple.Pit), "bob in the pit");
            }
        }

        // ---- the lever ----

        [Fact]
        public async Task PullingTheLeverDisarmsEveryTrapOfTheGroupAndTheLineIsConsumed()
        {
            using (var harness = new ModuleTestHarness("temple", AllCapabilities))
            {
                Temple temple = await StartTempleAsync(harness);
                Mover ann = harness.Place(temple.Antechamber, "ann");
                Mover bob = harness.Place(temple.Antechamber, "bob");

                BeforeOutcome outcome = await TypeLine(harness, ann, temple.Antechamber, "pull lever");
                Assert.True(outcome.Handled);
                await WaitForWorldAsync(harness, () => HasTrapComponent(harness, temple.Trapped) && HasTrapComponent(harness, temple.Second), "both trap components");

                Assert.False(Armed(harness, temple.Trapped));
                Assert.False(Armed(harness, temple.Second));
                Assert.True(harness.OnGameThread(() => { BsonDocument data; temple.Antechamber.TryGetComponent(TempleModule.LeverComponent, out data); return data["pulled"].AsBoolean; }));
                Assert.Contains(TempleModule.LeverPulledMessage, ann.MessagesSoFar().Concat(new[] { outcome.Message ?? string.Empty }));
                Assert.Contains("ann pulls a lever. Gears grind in the walls.", bob.MessagesSoFar());

                // A disarmed trap lets a walker through.
                Mover cat = harness.Place(temple.Hall, "cat");
                await harness.MoveAsync(cat, temple.Trapped);
                Assert.Same(temple.Trapped, Where(harness, cat));
            }
        }

        [Fact]
        public async Task ALineThatIsNotTheLeverIsNotHandledAndCoreGoesOn()
        {
            using (var harness = new ModuleTestHarness("temple", AllCapabilities))
            {
                Temple temple = await StartTempleAsync(harness);
                Mover ann = harness.Place(temple.Antechamber, "ann");

                foreach (string line in new[] { "look", "pull", "pull rope", "pull lever now", "push lever" })
                {
                    BeforeOutcome outcome = await TypeLine(harness, ann, temple.Antechamber, line);
                    Assert.False(outcome.Handled, line);
                }
                Assert.Empty(temple.Module.PulledLevers);
                Assert.False(HasTrapComponent(harness, temple.Trapped));
            }
        }

        [Fact]
        public async Task ASecondPullBeforeTheRearmIsRefusedAndTwoPullersAtOnceDisarmOnce()
        {
            using (var harness = new ModuleTestHarness("temple", AllCapabilities))
            {
                Temple temple = await StartTempleAsync(harness);
                Mover ann = harness.Place(temple.Antechamber, "ann");
                Mover bob = harness.Place(temple.Antechamber, "bob");

                Task<BeforeOutcome> first = TypeLine(harness, ann, temple.Antechamber, "pull lever");
                Task<BeforeOutcome> second = TypeLine(harness, bob, temple.Antechamber, "PULL Lever");
                BeforeOutcome[] outcomes = await Task.WhenAll(first, second);
                Assert.True(outcomes.All(outcome => outcome.Handled));
                await WaitForWorldAsync(harness, () => bob.MessagesSoFar().Concat(ann.MessagesSoFar()).Contains(TempleModule.LeverRefusedMessage), "the refusal");

                int refused = ann.MessagesSoFar().Concat(bob.MessagesSoFar()).Count(text => text == TempleModule.LeverRefusedMessage);
                Assert.Equal(1, refused);
                Assert.Single(temple.Module.PulledLevers);
            }
        }

        [Fact]
        public async Task TheLeverAndTheTrapsRearmTogetherWhenTheirTimersFire()
        {
            using (var harness = new ModuleTestHarness("temple", AllCapabilities))
            {
                Temple temple = await StartTempleAsync(harness);
                Mover ann = harness.Place(temple.Antechamber, "ann");
                await TypeLine(harness, ann, temple.Antechamber, "pull lever");
                await WaitForWorldAsync(harness, () => HasTrapComponent(harness, temple.Second) && !Armed(harness, temple.Second), "disarmed");

                harness.AdvanceClock(TimeSpan.FromSeconds(601));
                await WaitForWorldAsync(harness, () => Armed(harness, temple.Trapped) && Armed(harness, temple.Second)
                    && !harness.OnGameThread(() => { BsonDocument data; temple.Antechamber.TryGetComponent(TempleModule.LeverComponent, out data); return data["pulled"].AsBoolean; }), "everything rearmed");
                Assert.Empty(temple.Module.SpentTraps);
                Assert.Empty(temple.Module.PulledLevers);

                // The lever can be pulled again.
                BeforeOutcome again = await TypeLine(harness, ann, temple.Antechamber, "pull lever");
                Assert.True(again.Handled);
                Assert.Single(temple.Module.PulledLevers);
            }
        }

        // ---- restart and reconciliation (D13, D74) ----

        [Fact]
        public async Task AfterARestartWhileSpentTheTimersComeBackAndTheTrapRearmsOnSchedule()
        {
            using (var harness = new ModuleTestHarness("temple", AllCapabilities))
            {
                Temple first = await StartTempleAsync(harness);
                Mover ann = harness.Place(first.Hall, "ann");
                harness.StartMove(ann, first.Trapped);
                await WaitForWorldAsync(harness, () => ReferenceEquals(Where(harness, ann), first.Pit) && HasTrapComponent(harness, first.Trapped), "the sprung trap");

                await harness.StopModuleAsync();
                harness.Clock.Advance(TimeSpan.FromSeconds(100));
                var second = new TempleModule(() => new DateTimeOffset(harness.Clock.Now));
                await harness.StartAsync(TempleModule.Define(second));

                Assert.Contains("temple.corridor_2", second.SpentTraps);
                await harness.WaitForAsync(() => harness.Dispatcher.TimerCount(harness.Connection) >= 1, "the rearm timer recreated");
                await WaitForWorldAsync(harness, () => !Armed(harness, first.Trapped), "still spent after the restart");

                harness.AdvanceClock(TimeSpan.FromSeconds(600));
                await WaitForWorldAsync(harness, () => Armed(harness, first.Trapped), "rearmed on schedule");
            }
        }

        [Fact]
        public async Task AfterAnOutageThatOutlastsTheRearmTimeTheRestartRearmsAtOnce()
        {
            using (var harness = new ModuleTestHarness("temple", AllCapabilities))
            {
                Temple first = await StartTempleAsync(harness);
                Mover ann = harness.Place(first.Antechamber, "ann");
                await TypeLine(harness, ann, first.Antechamber, "pull lever");
                await WaitForWorldAsync(harness, () => HasTrapComponent(harness, first.Trapped) && !Armed(harness, first.Trapped), "disarmed");

                await harness.StopModuleAsync();
                harness.Clock.Advance(TimeSpan.FromSeconds(5000));
                var second = new TempleModule(() => new DateTimeOffset(harness.Clock.Now));
                await harness.StartAsync(TempleModule.Define(second));

                await WaitForWorldAsync(harness, () => Armed(harness, first.Trapped) && Armed(harness, first.Second), "rearmed by reconciliation");
                Assert.Empty(second.SpentTraps);
                Assert.Empty(second.PulledLevers);
            }
        }

        [Fact]
        public async Task ReconciliationIsIdempotentRunningItTwiceChangesNothing()
        {
            using (var harness = new ModuleTestHarness("temple", AllCapabilities))
            {
                Temple first = await StartTempleAsync(harness);
                Mover ann = harness.Place(first.Antechamber, "ann");
                await TypeLine(harness, ann, first.Antechamber, "pull lever");
                await WaitForWorldAsync(harness, () => HasTrapComponent(harness, first.Trapped) && !Armed(harness, first.Trapped), "disarmed");

                for (int round = 0; round < 2; round++)
                {
                    await harness.StopModuleAsync();
                    var again = new TempleModule(() => new DateTimeOffset(harness.Clock.Now));
                    await harness.StartAsync(TempleModule.Define(again));
                    harness.Pump();
                    Assert.Equal(2, again.SpentTraps.Count);
                    Assert.Single(again.PulledLevers);
                    Assert.False(Armed(harness, first.Trapped));
                    Assert.False(Armed(harness, first.Second));
                }
            }
        }

        [Fact]
        public async Task ARearmTimerForATrapThatIsNotSpentDoesNothing()
        {
            using (var harness = new ModuleTestHarness("temple", AllCapabilities))
            {
                Temple temple = await StartTempleAsync(harness);
                Mover ann = harness.Place(temple.Hall, "ann");
                harness.StartMove(ann, temple.Trapped);
                await WaitForWorldAsync(harness, () => ReferenceEquals(Where(harness, ann), temple.Pit), "ann in the pit");
                harness.AdvanceClock(TimeSpan.FromSeconds(601));
                await WaitForWorldAsync(harness, () => Armed(harness, temple.Trapped), "rearmed");

                harness.AdvanceClock(TimeSpan.FromSeconds(601)); // nothing is scheduled any more; nothing breaks
                harness.Pump();
                Assert.True(Armed(harness, temple.Trapped));
            }
        }

        [Fact]
        public async Task WithoutStateAndTimerCapabilitiesTheModuleStartsButNothingWorks()
        {
            using (var harness = new ModuleTestHarness("temple", "emit:message.send"))
            {
                Temple temple = await StartTempleAsync(harness);
                Assert.Contains(harness.LogLines(), line => line.Contains("needs the 'state' and 'timer.schedule' capabilities"));
                Mover ann = harness.Place(temple.Hall, "ann");

                await harness.MoveAsync(ann, temple.Trapped);
                Assert.Same(temple.Trapped, Where(harness, ann)); // fail open: the walk goes through
            }
        }

        [Fact]
        public async Task TheOrphanSweepIsSkippedWhenCoreDoesNotAnswerQueryEntitiesAndTheModuleStillStarts()
        {
            using (var harness = new ModuleTestHarness("temple", AllCapabilities))
            {
                await StartTempleAsync(harness);
                Assert.Contains(harness.LogLines(), line => line.Contains("Temple reconciled: 0 spent trap(s), 0 pulled lever(s)."));
            }
        }
    }
}
