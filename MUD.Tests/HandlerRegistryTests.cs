using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MUD.Core;
using MUD.Core.Components;
using MUD.Core.Events;
using MUD.Core.GameObjects;
using Xunit;

namespace MUD.Tests
{
    // The in-process (Module, Type) registry, bleeding as its first real behavior, the heartbeat index and the dormant
    // accounting (P3-3; owner decisions D5, D10, D49, D56, D60). No threads, no sleeping, no Mongo.
    [Collection("World construction")] // World registers BSON class maps; constructing it in parallel races
    public class HandlerRegistryTests
    {
        private class Recorder : Living
        {
            public List<string> Messages = new List<string>();

            public Recorder() : base(null) { }

            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private static Room NewRoom(string id)
        {
            return new Room(null) { Id = id, ShortDescription = id };
        }

        private static void AttachHeartbeat(GameObject entity, string module, string type)
        {
            Assert.Null(((IBehaviorHolder)entity).AddBehavior(new BehaviorRef(BehaviorHooks.Heartbeat, module, type)));
        }

        // ---- the registry ----

        [Fact]
        public void RegistryResolvesARegisteredModuleAndTypeAndNothingElse()
        {
            var registry = new InProcessBehaviorRegistry();
            registry.RegisterHeartbeat("m", "Counts", (living, entry) => { });

            IEventHandler handler;
            Assert.True(registry.TryResolve("m", "Counts", out handler));
            Assert.NotNull(handler);
            Assert.False(registry.TryResolve("m", "Other", out handler));
            Assert.Null(handler);
            Assert.False(registry.TryResolve("other", "Counts", out handler));
            Assert.False(registry.TryResolve(null, null, out handler));
        }

        [Fact]
        public void RegisteringTheSameModuleAndTypeTwiceIsRefused()
        {
            var registry = new InProcessBehaviorRegistry();
            registry.RegisterHeartbeat("m", "Counts", (living, entry) => { });

            Assert.Throws<InvalidOperationException>(() => registry.RegisterHeartbeat("m", "Counts", (living, entry) => { }));
        }

        [Fact]
        public void TheSharedRegistryHoldsTheBleedingBehaviorAndTheDispatcherUsesItByDefault()
        {
            IEventHandler handler;
            Assert.True(InProcessBehaviorRegistry.Shared.TryResolve(BleedingStatus.Module, BleedingStatus.Type, out handler));
            Assert.IsType<BleedingBehavior>(handler);
            Assert.Same(InProcessBehaviorRegistry.Shared, new EventDispatcher().Resolver);
        }

        // ---- bleeding as the first real behavior ----

        [Fact]
        public void BleedingRunsThroughTheDispatcherAndEnds()
        {
            var dispatcher = new EventDispatcher(null, null, s => { });
            var living = new Recorder();
            using (dispatcher.Bind())
            {
                Assert.True(BleedingStatus.Apply(living, 2));
                living.Messages.Clear();

                dispatcher.DeliverHeartbeat(living);
                Assert.Equal(new[] { "You continue to bleed from your wounds." }, living.Messages);
                Assert.Equal(1, BleedingStatus.RemainingDuration(living));

                living.Messages.Clear();
                dispatcher.DeliverHeartbeat(living);
                Assert.Equal(2, living.Messages.Count);
                Assert.False(BleedingStatus.IsBleeding(living));
                Assert.Empty(living.Behaviors);
            }
        }

        [Fact]
        public void AnNpcThatBleedsIsTickedByTheIndexWithoutAnyPlayerPresent()
        {
            var dispatcher = new EventDispatcher(null, null, s => { });
            var npc = new NpcRecorder();
            using (dispatcher.Bind())
            {
                Assert.True(BleedingStatus.Apply(npc, 2));
                Assert.True(dispatcher.Heartbeats.Contains(npc));

                Assert.Equal(1, dispatcher.DeliverIndexedHeartbeats());
                Assert.Equal(1, BleedingStatus.RemainingDuration(npc));
                Assert.Equal(1, dispatcher.DeliverIndexedHeartbeats());
                Assert.False(BleedingStatus.IsBleeding(npc));

                // The entry went with the bleeding, so the entity leaves the index the next time it is reached.
                Assert.Equal(0, dispatcher.DeliverIndexedHeartbeats());
                Assert.Equal(0, dispatcher.Heartbeats.Count);
            }
        }

        private class NpcRecorder : NPC
        {
            public List<string> Messages = new List<string>();

            public NpcRecorder() : base(null) { }

            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        // ---- the heartbeat index (D56) ----

        [Fact]
        public void ARoomGainsHeartbeatsOnlyWhenAnEntryIsAttached()
        {
            var registry = new InProcessBehaviorRegistry();
            var seen = new List<string>();
            registry.Register("m", "Pulse", new Lambda(delivery => seen.Add(delivery.Party.Entity.Id + "@" + delivery.Event.Payload["tick"])));
            var dispatcher = new EventDispatcher(registry, null, s => { });
            var plain = NewRoom("plain");
            var pulsing = NewRoom("pulsing");
            using (dispatcher.Bind())
            {
                AttachHeartbeat(pulsing, "m", "Pulse");
                dispatcher.HeartbeatTick = 40;

                Assert.False(dispatcher.Heartbeats.Contains(plain));
                Assert.True(dispatcher.Heartbeats.Contains(pulsing));
                Assert.Equal(1, dispatcher.DeliverIndexedHeartbeats());
            }

            Assert.Equal(new[] { "pulsing@40" }, seen);
        }

        [Fact]
        public void AnEntryForAnotherHookDoesNotPutAnEntityInTheIndex()
        {
            var dispatcher = new EventDispatcher(null, null, s => { });
            var room = NewRoom("a");
            using (dispatcher.Bind())
            {
                Assert.Null(room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "m", "T")));
            }

            Assert.Equal(0, dispatcher.Heartbeats.Count);
            Assert.False(dispatcher.Heartbeats.Track(room));
        }

        [Fact]
        public void AnEntityLoadedWithAStoredEntryJoinsTheIndexWhenTheWorldTracksIt()
        {
            var dispatcher = new EventDispatcher(null, null, s => { });
            var room = NewRoom("stored");
            room.Behaviors.Add(new BehaviorRef(BehaviorHooks.Heartbeat, "m", "T")); // as deserialised: no AddBehavior call

            Assert.False(dispatcher.Heartbeats.Contains(room));
            Assert.True(dispatcher.Heartbeats.Track(room));
            Assert.False(dispatcher.Heartbeats.Track(room)); // already there
            Assert.Equal(1, dispatcher.Heartbeats.Count);
        }

        [Fact]
        public void EnteringARoomIndexesTheRoomAndTheOccupantThatCarryHeartbeatEntries()
        {
            var dispatcher = new EventDispatcher(null, null, s => { });
            var room = NewRoom("hall");
            var npc = new NpcRecorder();
            npc.Id = "npc1";
            room.Behaviors.Add(new BehaviorRef(BehaviorHooks.Heartbeat, "m", "Room"));
            npc.Behaviors.Add(new BehaviorRef(BehaviorHooks.Heartbeat, "m", "Npc"));
            using (dispatcher.Bind())
            {
                room.EnterRoom(npc, "it arrives");
            }

            Assert.True(dispatcher.Heartbeats.Contains(room));
            Assert.True(dispatcher.Heartbeats.Contains(npc));
        }

        [Fact]
        public void PlayersAreNeverInTheIndexBecauseTheWorldTicksThemItself()
        {
            var dispatcher = new EventDispatcher(null, null, s => { });
            var player = new Player(null, null, null) { Id = "p1" };
            using (dispatcher.Bind())
            {
                BleedingStatus.Apply(player, 3);
            }

            Assert.False(dispatcher.Heartbeats.Contains(player));
        }

        [Fact]
        public void AnEntryRemovedBeforeTheTickDropsTheEntityFromTheIndexWithoutDelivering()
        {
            var registry = new InProcessBehaviorRegistry();
            int ticks = 0;
            registry.Register("m", "Pulse", new Lambda(delivery => ticks++));
            var dispatcher = new EventDispatcher(registry, null, s => { });
            var room = NewRoom("a");
            using (dispatcher.Bind())
            {
                AttachHeartbeat(room, "m", "Pulse");
                Assert.True(room.RemoveBehavior(BehaviorHooks.Heartbeat, "m", "Pulse"));

                Assert.Equal(0, dispatcher.DeliverIndexedHeartbeats());
            }

            Assert.Equal(0, ticks);
            Assert.Equal(0, dispatcher.Heartbeats.Count);
        }

        [Fact]
        public void OneFailingEntityDoesNotStopTheOthersAndTheFailureIsCounted()
        {
            var registry = new InProcessBehaviorRegistry();
            int ticks = 0;
            registry.Register("m", "Fails", new Lambda(delivery => throw new InvalidOperationException("scripted")));
            registry.Register("m", "Counts", new Lambda(delivery => ticks++));
            var log = new List<string>();
            var dispatcher = new EventDispatcher(registry, null, log.Add);
            var first = NewRoom("first");
            var second = NewRoom("second");
            using (dispatcher.Bind())
            {
                AttachHeartbeat(first, "m", "Fails");
                AttachHeartbeat(second, "m", "Counts");

                dispatcher.DeliverIndexedHeartbeats();
            }

            Assert.Equal(1, ticks);
            Assert.Equal(1, dispatcher.HandlerFailures);
            Assert.Contains(log, line => line.Contains("Exception in handler m/Fails"));
        }

        [Fact]
        public void AHeartbeatHandlerMayEmitAndTheEventIsCausedByTheHeartbeat()
        {
            var registry = new InProcessBehaviorRegistry();
            registry.Register("m", "Speaks", new Lambda(delivery =>
                delivery.Emit("m.spoke", new[] { new EventParty("target", delivery.Party.Entity) })));
            var dispatcher = new EventDispatcher(registry, null, s => { });
            var observed = new List<GameEvent>();
            dispatcher.AddObserver(new Observed(observed));
            var room = NewRoom("a");
            using (dispatcher.Bind())
            {
                AttachHeartbeat(room, "m", "Speaks");
                observed.Clear();
                dispatcher.DeliverIndexedHeartbeats();
                dispatcher.Pump();
            }

            GameEvent spoke = observed.Single(e => e.Name == "m.spoke");
            Assert.NotEqual(0, spoke.CauseId);
            Assert.Equal(1, spoke.Depth);
        }

        // The cost claim, measured: delivering a heartbeat to an entity with no heartbeat entry allocates nothing.
        [Fact]
        public void DeliveringAHeartbeatToAnEntityWithNoHeartbeatEntryAllocatesNothing()
        {
            var dispatcher = new EventDispatcher(null, null, s => { });
            var plain = NewRoom("plain");
            var otherHook = NewRoom("other");
            otherHook.Behaviors.Add(new BehaviorRef(BehaviorHooks.LivingEnter, "m", "T"));
            dispatcher.DeliverHeartbeat(plain); // warm up
            dispatcher.DeliverIndexedHeartbeats();

            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < 1000; round++)
            {
                dispatcher.DeliverHeartbeat(plain);
                dispatcher.DeliverHeartbeat(otherHook);
                dispatcher.DeliverIndexedHeartbeats();
            }

            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        // ---- dormant entries (D10) ----

        [Fact]
        public void AnUnresolvedHeartbeatEntryIsSkippedCountedAndLeftOnTheEntity()
        {
            var dispatcher = new EventDispatcher(new InProcessBehaviorRegistry(), null, s => { });
            var room = NewRoom("a");
            using (dispatcher.Bind())
            {
                AttachHeartbeat(room, "unloaded", "Gone");
                dispatcher.DeliverIndexedHeartbeats();
                dispatcher.DeliverIndexedHeartbeats();
            }

            Assert.Equal(2, dispatcher.DormantSkips);
            Assert.Equal(2, dispatcher.DormantSinceLastLog[("unloaded", "Gone")]);
            Assert.Single(room.Behaviors); // the entry stays (D10)
            Assert.True(dispatcher.Heartbeats.Contains(room));
        }

        [Fact]
        public void DormantSkipsAreLoggedOncePerIntervalWithTheCountsAndThenReset()
        {
            var clock = new FakeClock();
            var log = new List<string>();
            var dispatcher = new EventDispatcher(new InProcessBehaviorRegistry(), null, log.Add, clock);
            var room = NewRoom("a");
            using (dispatcher.Bind())
            {
                AttachHeartbeat(room, "unloaded", "Gone");
                for (int round = 0; round < 5; round++) { dispatcher.DeliverIndexedHeartbeats(); }

                dispatcher.Pump();
                Assert.Single(log);
                Assert.Contains("unloaded/Gone x5", log[0]);
                Assert.Contains("5 in this interval", log[0]);
                Assert.Equal(0, dispatcher.DormantSinceLastLog.Count);

                // More skips inside the interval are counted but not logged yet.
                for (int round = 0; round < 3; round++) { dispatcher.DeliverIndexedHeartbeats(); }
                clock.Advance(dispatcher.Limits.DormantLogInterval - TimeSpan.FromSeconds(1));
                dispatcher.Pump();
                Assert.Single(log);

                // Once the interval has passed, one more line carries the counts since the last line.
                clock.Advance(TimeSpan.FromSeconds(1));
                dispatcher.Pump();
                Assert.Equal(2, log.Count);
                Assert.Contains("unloaded/Gone x3", log[1]);
                Assert.Contains("8 since start", log[1]);
                Assert.Equal(2, dispatcher.DormantLogLines);

                // Nothing skipped, nothing logged.
                clock.Advance(dispatcher.Limits.DormantLogInterval);
                dispatcher.Pump();
                Assert.Equal(2, log.Count);
            }
        }

        [Fact]
        public void DormantEntriesOfSeveralModulesShareOneLineInAStableOrder()
        {
            var log = new List<string>();
            var dispatcher = new EventDispatcher(new InProcessBehaviorRegistry(), null, log.Add, new FakeClock());
            var first = NewRoom("first");
            var second = NewRoom("second");
            using (dispatcher.Bind())
            {
                AttachHeartbeat(first, "zeta", "B");
                AttachHeartbeat(second, "alpha", "A");
                dispatcher.DeliverIndexedHeartbeats();
                dispatcher.Pump();
            }

            Assert.Single(log);
            Assert.Contains("alpha/A x1, zeta/B x1", log[0]);
        }

        [Fact]
        public void AnUnresolvedAfterEventEntryIsCountedAsDormantToo()
        {
            var dispatcher = new EventDispatcher(new InProcessBehaviorRegistry(), null, s => { });
            var room = NewRoom("a");
            room.Behaviors.Add(new BehaviorRef(BehaviorHooks.LivingEnter, "unloaded", "Gone"));
            dispatcher.Emit("entity.move", EventPhase.After, new[] { new EventParty("to", room, new[] { BehaviorHooks.LivingEnter }) });

            dispatcher.Pump();

            Assert.Equal(1, dispatcher.DormantSkips);
        }

        // ---- the world's heartbeat ----

        private static void RunWorldHeartbeat(World world)
        {
            MethodInfo method = typeof(World).GetMethod("doHeartbeat", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(world, new object[] { null });
        }

        [Fact]
        public void TheWorldHeartbeatReachesPlayersAndIndexedRoomsAndNpcsAndNothingElse()
        {
            var clock = new FakeClock();
            var world = new World(new MongoDB.Driver.MongoClient("mongodb://127.0.0.1:1"), new WorldSettings(), clock);
            var seen = new List<string>();
            var registry = new InProcessBehaviorRegistry();
            registry.Register("m", "Pulse", new Lambda(delivery => seen.Add(delivery.Party.Entity.Id)));
            world.Events.Resolver = registry;
            var room = NewRoom("room1");
            var npc = new NpcRecorder { Id = "npc1" };
            var quiet = NewRoom("quiet");
            var player = new Player(world, null, null) { Id = "player1", PlayerName = "ann" };
            using (world.Events.Bind())
            {
                AttachHeartbeat(room, "m", "Pulse");
                AttachHeartbeat(npc, "m", "Pulse");
                world.Players.Add(player);
                AttachHeartbeat(player, "m", "Pulse");

                RunWorldHeartbeat(world);
            }

            Assert.Equal(new[] { "player1", "room1", "npc1" }, seen);
            Assert.DoesNotContain("quiet", seen);
            Assert.False(world.Events.Heartbeats.Contains(quiet));
        }

        private class Lambda : IEventHandler
        {
            private readonly Action<EventDelivery> _action;
            public Lambda(Action<EventDelivery> action) { _action = action; }
            public void Handle(EventDelivery delivery) { _action(delivery); }
        }

        private class Observed : IEventObserver
        {
            private readonly List<GameEvent> _seen;
            public Observed(List<GameEvent> seen) { _seen = seen; }
            public void Observe(GameEvent gameEvent) { _seen.Add(gameEvent); }
        }
    }
}
