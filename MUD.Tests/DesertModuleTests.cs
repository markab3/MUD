using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using MongoDB.Bson;
using MUD.Core.Components;
using MUD.Core.GameObjects;
using MUD.CoreApi;
using MUD.Modules.Desert;
using MUD.Modules.Sdk;
using Xunit;
using Mover = MUD.Tests.ModuleTestHarness.Mover;

namespace MUD.Tests
{
    /// <summary>
    /// Card P5-1: the desert module against core's real module host over a real Unix domain socket (see ModuleTestHarness). MongoDB is not
    /// involved: state documents are an in-memory fake repository, and the clock is the harness's fake clock.
    /// </summary>
    [Collection("World construction")] // World registers BSON class maps; constructing it in parallel races
    public class DesertModuleTests
    {
        private const string AllCapabilities = "emit:component.set emit:component.remove emit:message.send emit:feature.add emit:feature.remove state timer.schedule read";

        private static DesertSettings Settings(params string[] startRooms)
        {
            return new DesertSettings
            {
                StartRooms = startRooms.ToList(),
                SpawnIntervalSeconds = 60,
                StormDurationSeconds = 120,
                StepSeconds = 10,
                MaxSteps = 12,
                BlindSeconds = 30
            };
        }

        private sealed class Desert
        {
            public ModuleTestHarness Harness;
            public DesertModule Module;
            public Room Dune3, Dune4;
        }

        private static async Task<Desert> StartDesertAsync(ModuleTestHarness harness, DesertSettings settings, bool attachRoomBehavior = true)
        {
            var desert = new Desert { Harness = harness };
            desert.Module = new DesertModule(settings, () => new DateTimeOffset(harness.Clock.Now));
            await harness.StartAsync(DesertModule.Define(desert.Module));
            return desert;
        }

        private static void Build(Desert desert)
        {
            ModuleTestHarness harness = desert.Harness;
            desert.Dune3 = harness.NewRoom("desert.dune_3");
            desert.Dune4 = harness.NewRoom("desert.dune_4");
            var config3 = new BsonDocument { { "next", new BsonArray { "desert.dune_4" } }, { "step_s", 10 } };
            var config4 = new BsonDocument { { "next", new BsonArray { "desert.dune_3" } }, { "step_s", 10 } };
            harness.Attach(desert.Dune3, BehaviorHooks.Heartbeat, DesertModule.StormRoomType, config3);
            harness.Attach(desert.Dune4, BehaviorHooks.Heartbeat, DesertModule.StormRoomType, config4);
            harness.Attach(desert.Dune3, BehaviorHooks.LivingEnter, DesertModule.StormExposureType, new BsonDocument { { "blind_s", 30 } });
            harness.Attach(desert.Dune4, BehaviorHooks.LivingEnter, DesertModule.StormExposureType, new BsonDocument { { "blind_s", 30 } });
        }

        private static bool HasStorm(ModuleTestHarness harness, Room room)
        {
            return harness.OnGameThread(() => room.HasComponent(DesertModule.StormComponent));
        }

        private static bool HasFeature(ModuleTestHarness harness, Room room)
        {
            return harness.OnGameThread(() => room.Features.Any(feature => feature.ShortDescription == DesertModule.StormFeatureText));
        }

        private static async Task WaitForWorldAsync(ModuleTestHarness harness, Func<bool> condition, string what)
        {
            await harness.WaitForAsync(() => harness.Pump() >= 0 && condition(), what);
        }

        private static void Beat(ModuleTestHarness harness, Room room)
        {
            harness.OnGameThread(() => { harness.World.Events.DeliverHeartbeat(room); return 0; });
        }

        // ---- the manifest ----

        [Fact]
        public void TheManifestDeclaresTheTwoBehaviorTypesOnTheirHooksAndTheCapabilitiesItNeeds()
        {
            var manifest = DesertModule.Define().BuildManifest();
            Assert.Equal("desert", manifest.Name);
            Assert.Equal(new[] { "heartbeat" }, manifest.Behaviors.Single(b => b.Type == "DesertStormRoom").Hooks.ToArray());
            Assert.Equal(new[] { "living.enter" }, manifest.Behaviors.Single(b => b.Type == "DesertStormExposure").Hooks.ToArray());
            Assert.Contains("emit:component.set", manifest.Capabilities);
            Assert.Contains("emit:feature.add", manifest.Capabilities);
            Assert.Contains("state", manifest.Capabilities);
            Assert.Contains("timer.schedule", manifest.Capabilities);
            Assert.Empty(manifest.RequiredCapabilities); // a flavor module: it is never required (D60)
        }

        [Fact]
        public void TheSettingsFileIsReadAndABrokenOneGivesTheDefaults()
        {
            string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ds" + Guid.NewGuid().ToString("N").Substring(0, 8));
            System.IO.Directory.CreateDirectory(directory);
            try
            {
                string problem;
                Assert.Empty(DesertSettings.Load(directory, out problem).StartRooms);
                Assert.Null(problem);

                System.IO.File.WriteAllText(System.IO.Path.Combine(directory, DesertSettings.FileName), "{ \"startRooms\": [\"desert.dune_3\"], \"spawnIntervalSeconds\": 99 }");
                DesertSettings loaded = DesertSettings.Load(directory, out problem);
                Assert.Equal(new[] { "desert.dune_3" }, loaded.StartRooms.ToArray());
                Assert.Equal(99, loaded.SpawnIntervalSeconds);

                System.IO.File.WriteAllText(System.IO.Path.Combine(directory, DesertSettings.FileName), "{ not json");
                Assert.Equal(1800, DesertSettings.Load(directory, out problem).SpawnIntervalSeconds);
                Assert.NotNull(problem);
            }
            finally { System.IO.Directory.Delete(directory, true); }
        }

        // ---- spawn, drift, scour, end ----

        [Fact]
        public async Task AStormSpawnsWhenTheSpawnTimerFiresAndShowsAsComponentFeatureMessageAndStateDocument()
        {
            using (var harness = new ModuleTestHarness("desert", AllCapabilities))
            {
                Desert desert = await StartDesertAsync(harness, Settings("desert.dune_3"));
                Build(desert);
                Mover ann = harness.Place(desert.Dune3, "ann");

                harness.AdvanceClock(TimeSpan.FromSeconds(61));
                await WaitForWorldAsync(harness, () => HasStorm(harness, desert.Dune3), "the storm component");

                Assert.True(HasFeature(harness, desert.Dune3));
                Assert.Contains("A sandstorm rolls in.", ann.MessagesSoFar());
                Assert.False(HasStorm(harness, desert.Dune4));
                await harness.WaitForAsync(() => harness.StateRepository.Docs.ContainsKey("desert/storms"), "the storms document");
                var storms = harness.StateRepository.Docs["desert/storms"]["Document"].AsBsonDocument;
                Assert.Equal(1, storms.ElementCount);
                Assert.Equal("desert.dune_3", storms[0]["room"].AsString);
                Assert.Equal("moderate", harness.OnGameThread(() => { BsonDocument data; desert.Dune3.TryGetComponent(DesertModule.StormComponent, out data); return data["intensity"].AsString; }));
                // The next spawn is scheduled again and the end timer is set.
                Assert.True(harness.StateRepository.Docs["desert/spawner"]["Document"]["next_spawn_at"].ToInt64() > 0);
            }
        }

        [Fact]
        public async Task WithNoStartRoomsNoStormEverSpawnsButTheSpawnerKeepsRunning()
        {
            using (var harness = new ModuleTestHarness("desert", AllCapabilities))
            {
                Desert desert = await StartDesertAsync(harness, Settings());
                Build(desert);
                harness.AdvanceClock(TimeSpan.FromSeconds(61));
                await harness.WaitForAsync(() => harness.LogLines().Any(line => line.Contains("No start rooms")), "the log line");
                Assert.False(HasStorm(harness, desert.Dune3));
                Assert.Empty(desert.Module.StormRooms);
            }
        }

        [Fact]
        public async Task TheStormMovesOnWhenTheRoomHeartbeatFindsItsStepDueAndNeverIsInTwoRooms()
        {
            using (var harness = new ModuleTestHarness("desert", AllCapabilities))
            {
                Desert desert = await StartDesertAsync(harness, Settings("desert.dune_3"));
                Build(desert);
                Mover ann = harness.Place(desert.Dune3, "ann");
                Mover bob = harness.Place(desert.Dune4, "bob");
                harness.AdvanceClock(TimeSpan.FromSeconds(61));
                await WaitForWorldAsync(harness, () => HasStorm(harness, desert.Dune3), "the storm");

                // Not due yet: the heartbeat changes nothing.
                Beat(harness, desert.Dune3);
                await Task.Delay(300);
                harness.Pump();
                Assert.True(HasStorm(harness, desert.Dune3));
                Assert.False(HasStorm(harness, desert.Dune4));

                harness.Clock.Advance(TimeSpan.FromSeconds(11));
                Beat(harness, desert.Dune4); // a room that does not hold the storm does nothing
                Beat(harness, desert.Dune3);
                await WaitForWorldAsync(harness, () => HasStorm(harness, desert.Dune4), "the storm in the next room");

                Assert.False(HasStorm(harness, desert.Dune3));
                Assert.False(HasFeature(harness, desert.Dune3));
                Assert.True(HasFeature(harness, desert.Dune4));
                Assert.Contains("The storm moves on.", ann.MessagesSoFar());
                Assert.Contains("A sandstorm rolls in.", bob.MessagesSoFar());
                Assert.Equal("desert.dune_4", desert.Module.StormRooms.Values.Single());
            }
        }

        [Fact]
        public async Task ALivingWhoWalksIntoTheStormIsScouredAndTheEffectEndsByItself()
        {
            using (var harness = new ModuleTestHarness("desert", AllCapabilities))
            {
                Desert desert = await StartDesertAsync(harness, Settings("desert.dune_3"));
                Build(desert);
                Mover ann = harness.Place(harness.NewRoom("desert.oasis"), "ann");
                harness.AdvanceClock(TimeSpan.FromSeconds(61));
                await WaitForWorldAsync(harness, () => HasStorm(harness, desert.Dune3), "the storm");

                await harness.MoveAsync(ann, desert.Dune3);
                await WaitForWorldAsync(harness, () => harness.OnGameThread(() => ann.HasComponent(DesertModule.SandBlindComponent)), "sand blindness");

                Assert.Contains("Stinging sand scours your face.", ann.MessagesSoFar());
                Assert.Equal(OutputChannel.Combat, ann.Channels[ann.MessagesSoFar().ToList().IndexOf("Stinging sand scours your face.")]);
                await harness.WaitForAsync(() => harness.StateRepository.Docs.ContainsKey("desert/blind"), "the blind document");
                Assert.True(harness.StateRepository.Docs["desert/blind"]["Document"].AsBsonDocument.Contains("living.ann"));

                harness.AdvanceClock(TimeSpan.FromSeconds(31));
                await WaitForWorldAsync(harness, () => !harness.OnGameThread(() => ann.HasComponent(DesertModule.SandBlindComponent)), "sand blindness removed");
                Assert.Equal(0, harness.StateRepository.Docs["desert/blind"]["Document"].AsBsonDocument.ElementCount);
            }
        }

        [Fact]
        public async Task ALivingWhoWalksIntoAQuietRoomIsLeftAlone()
        {
            using (var harness = new ModuleTestHarness("desert", AllCapabilities))
            {
                Desert desert = await StartDesertAsync(harness, Settings("desert.dune_3"));
                Build(desert);
                Mover ann = harness.Place(harness.NewRoom("desert.oasis"), "ann");

                await harness.MoveAsync(ann, desert.Dune4);
                await Task.Delay(300);
                harness.Pump();
                Assert.False(harness.OnGameThread(() => ann.HasComponent(DesertModule.SandBlindComponent)));
                Assert.DoesNotContain("Stinging sand scours your face.", ann.MessagesSoFar());
            }
        }

        [Fact]
        public async Task AScourTwiceMovesTheEndTimeForwardAndTheOldTimerDoesNotCutItShort()
        {
            using (var harness = new ModuleTestHarness("desert", AllCapabilities))
            {
                Desert desert = await StartDesertAsync(harness, Settings("desert.dune_3"));
                Build(desert);
                Mover ann = harness.Place(harness.NewRoom("desert.oasis"), "ann");
                harness.AdvanceClock(TimeSpan.FromSeconds(61));
                await WaitForWorldAsync(harness, () => HasStorm(harness, desert.Dune3), "the storm");

                await harness.MoveAsync(ann, desert.Dune3);
                await WaitForWorldAsync(harness, () => harness.OnGameThread(() => ann.HasComponent(DesertModule.SandBlindComponent)), "first scour");
                long firstEnd = 0;
                await harness.WaitForAsync(() => harness.StateRepository.Docs.TryGetValue("desert/blind", out BsonDocument document) && document["Document"].AsBsonDocument.Contains("living.ann")
                    && (firstEnd = document["Document"]["living.ann"].ToInt64()) > 0, "the first end time");
                harness.AdvanceClock(TimeSpan.FromSeconds(20));
                await harness.MoveAsync(ann, desert.Dune4);
                await harness.MoveAsync(ann, desert.Dune3);
                await harness.WaitForAsync(() => harness.StateRepository.Docs["desert/blind"]["Document"]["living.ann"].ToInt64() > firstEnd, "the second end time");

                harness.AdvanceClock(TimeSpan.FromSeconds(15)); // the first end time has passed, the second has not
                await Task.Delay(300);
                harness.Pump();
                Assert.True(harness.OnGameThread(() => ann.HasComponent(DesertModule.SandBlindComponent)));

                harness.AdvanceClock(TimeSpan.FromSeconds(20));
                await WaitForWorldAsync(harness, () => !harness.OnGameThread(() => ann.HasComponent(DesertModule.SandBlindComponent)), "the second end");
            }
        }

        [Fact]
        public async Task TheEndTimerRemovesTheStormAndASecondEndIsANoOp()
        {
            using (var harness = new ModuleTestHarness("desert", AllCapabilities))
            {
                Desert desert = await StartDesertAsync(harness, Settings("desert.dune_3"));
                Build(desert);
                Mover ann = harness.Place(desert.Dune3, "ann");
                harness.AdvanceClock(TimeSpan.FromSeconds(61));
                await WaitForWorldAsync(harness, () => HasStorm(harness, desert.Dune3), "the storm");

                await harness.WaitForAsync(() => harness.Dispatcher.TimerCount(harness.Connection) >= 2, "the spawn and end timers");
                harness.AdvanceClock(TimeSpan.FromSeconds(121));
                await WaitForWorldAsync(harness, () => !HasStorm(harness, desert.Dune3) && ann.MessagesSoFar().Contains("The sandstorm dies away."), "the storm gone and the message");

                Assert.False(HasFeature(harness, desert.Dune3));
                Assert.Empty(desert.Module.StormRooms);
                Assert.Equal(0, harness.StateRepository.Docs["desert/storms"]["Document"].AsBsonDocument.ElementCount);
                Assert.Equal(1, ann.MessagesSoFar().Count(m => m == "The sandstorm dies away."));
            }
        }

        [Fact]
        public async Task AStormThatHasDriftedItsMaximumNumberOfStepsEndsThere()
        {
            using (var harness = new ModuleTestHarness("desert", AllCapabilities))
            {
                DesertSettings settings = Settings("desert.dune_3");
                settings.MaxSteps = 2;
                Desert desert = await StartDesertAsync(harness, settings);
                Build(desert);
                harness.AdvanceClock(TimeSpan.FromSeconds(61));
                await WaitForWorldAsync(harness, () => HasStorm(harness, desert.Dune3), "the storm");

                harness.Clock.Advance(TimeSpan.FromSeconds(11));
                Beat(harness, desert.Dune3);
                await WaitForWorldAsync(harness, () => HasStorm(harness, desert.Dune4), "step one");
                harness.Clock.Advance(TimeSpan.FromSeconds(11));
                Beat(harness, desert.Dune4);
                await WaitForWorldAsync(harness, () => !HasStorm(harness, desert.Dune4), "the end after step two");
                Assert.False(HasFeature(harness, desert.Dune4));
                Assert.Empty(desert.Module.StormRooms);
            }
        }

        // ---- restart reconciliation (D13, D74) ----

        [Fact]
        public async Task AfterARestartMidStormTheTimersComeBackAndTheStormEndsOnSchedule()
        {
            using (var harness = new ModuleTestHarness("desert", AllCapabilities))
            {
                Desert first = await StartDesertAsync(harness, Settings("desert.dune_3"));
                Build(first);
                Mover ann = harness.Place(first.Dune3, "ann");
                harness.AdvanceClock(TimeSpan.FromSeconds(61));
                await WaitForWorldAsync(harness, () => HasStorm(harness, first.Dune3), "the storm");
                string stormId = first.Module.StormRooms.Keys.Single();

                await harness.StopModuleAsync(); // the module goes away mid-storm: the component stays (D11)
                Assert.True(HasStorm(harness, first.Dune3));

                harness.Clock.Advance(TimeSpan.FromSeconds(30)); // still inside the storm's life
                var second = new DesertModule(Settings("desert.dune_3"), () => new DateTimeOffset(harness.Clock.Now));
                await harness.StartAsync(DesertModule.Define(second));

                Assert.Equal(stormId, second.StormRooms.Keys.Single());
                await harness.WaitForAsync(() => harness.Dispatcher.TimerCount(harness.Connection) >= 2, "the spawn and end timers recreated");
                Assert.True(HasStorm(harness, first.Dune3));
                Assert.True(HasFeature(harness, first.Dune3), "the Feature is added once, not twice");
                Assert.Equal(1, harness.OnGameThread(() => first.Dune3.Features.Count(f => f.ShortDescription == DesertModule.StormFeatureText)));

                harness.AdvanceClock(TimeSpan.FromSeconds(100));
                await WaitForWorldAsync(harness, () => !HasStorm(harness, first.Dune3), "the storm ends on schedule");
                Assert.Contains("The sandstorm dies away.", ann.MessagesSoFar());
            }
        }

        [Fact]
        public async Task AfterAnOutageThatOutlastsTheStormTheRestartCleansItUpAndRemovesPastDueBlindness()
        {
            using (var harness = new ModuleTestHarness("desert", AllCapabilities))
            {
                Desert first = await StartDesertAsync(harness, Settings("desert.dune_3"));
                Build(first);
                Mover ann = harness.Place(harness.NewRoom("desert.oasis"), "ann");
                harness.AdvanceClock(TimeSpan.FromSeconds(61));
                await WaitForWorldAsync(harness, () => HasStorm(harness, first.Dune3), "the storm");
                await harness.MoveAsync(ann, first.Dune3);
                await WaitForWorldAsync(harness, () => harness.OnGameThread(() => ann.HasComponent(DesertModule.SandBlindComponent)), "blindness");

                await harness.StopModuleAsync();
                harness.Clock.Advance(TimeSpan.FromSeconds(500)); // far past the end of the storm and of the blindness

                var second = new DesertModule(Settings("desert.dune_3"), () => new DateTimeOffset(harness.Clock.Now));
                await harness.StartAsync(DesertModule.Define(second));

                await WaitForWorldAsync(harness, () => !HasStorm(harness, first.Dune3) && !harness.OnGameThread(() => ann.HasComponent(DesertModule.SandBlindComponent)), "cleanup");
                Assert.False(HasFeature(harness, first.Dune3));
                Assert.Empty(second.StormRooms);
            }
        }

        [Fact]
        public async Task ReconciliationIsIdempotentRunningItTwiceChangesNothing()
        {
            using (var harness = new ModuleTestHarness("desert", AllCapabilities))
            {
                Desert first = await StartDesertAsync(harness, Settings("desert.dune_3"));
                Build(first);
                harness.AdvanceClock(TimeSpan.FromSeconds(61));
                await WaitForWorldAsync(harness, () => HasStorm(harness, first.Dune3), "the storm");

                for (int round = 0; round < 2; round++)
                {
                    await harness.StopModuleAsync();
                    var again = new DesertModule(Settings("desert.dune_3"), () => new DateTimeOffset(harness.Clock.Now));
                    await harness.StartAsync(DesertModule.Define(again));
                    harness.Pump();
                    Assert.Equal(1, again.StormRooms.Count);
                    Assert.True(HasStorm(harness, first.Dune3));
                    Assert.Equal(1, harness.OnGameThread(() => first.Dune3.Features.Count(f => f.ShortDescription == DesertModule.StormFeatureText)));
                }
            }
        }

        [Fact]
        public async Task WithoutTheReadAnswerFromCoreTheOrphanSweepIsSkippedAndTheModuleStillStarts()
        {
            using (var harness = new ModuleTestHarness("desert", AllCapabilities))
            {
                Desert desert = await StartDesertAsync(harness, Settings("desert.dune_3"));
                Assert.Contains(harness.LogLines(), line => line.Contains("QueryEntities") || line.Contains("Desert reconciled"));
                Assert.Contains(harness.LogLines(), line => line.Contains("Desert reconciled: 0 storm(s)"));
            }
        }

        [Fact]
        public async Task WithoutStateAndTimerCapabilitiesTheModuleStartsButDoesNothing()
        {
            using (var harness = new ModuleTestHarness("desert", "emit:message.send"))
            {
                Desert desert = await StartDesertAsync(harness, Settings("desert.dune_3"));
                Build(desert);
                harness.Place(desert.Dune3, "ann");
                Assert.Contains(harness.LogLines(), line => line.Contains("needs the 'state' and 'timer.schedule' capabilities"));
                harness.AdvanceClock(TimeSpan.FromSeconds(61));
                await Task.Delay(300);
                harness.Pump();
                Assert.False(HasStorm(harness, desert.Dune3));
            }
        }
    }
}
