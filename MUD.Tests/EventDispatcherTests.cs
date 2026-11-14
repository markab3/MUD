using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Core;
using MUD.Core.Components;
using MUD.Core.Events;
using MUD.Core.GameObjects;
using MUD.CoreApi;
using Xunit;

namespace MUD.Tests
{
    // The event record, the After phase, the guards and the change events (P3-1). No threads, no sleeping, no Mongo.
    public class EventDispatcherTests
    {
        private class Handlers : IEventHandlerResolver
        {
            public Dictionary<string, IEventHandler> ByKey = new Dictionary<string, IEventHandler>();

            public void Register(string module, string type, Action<EventDelivery> action)
            {
                ByKey[module + "/" + type] = new Lambda(action);
            }

            public bool TryResolve(string module, string type, out IEventHandler handler)
            {
                return ByKey.TryGetValue(module + "/" + type, out handler);
            }
        }

        private class Lambda : IEventHandler
        {
            private readonly Action<EventDelivery> _action;
            public Lambda(Action<EventDelivery> action) { _action = action; }
            public void Handle(EventDelivery delivery) { _action(delivery); }
        }

        private class Recorder : IEventObserver
        {
            public List<GameEvent> Seen = new List<GameEvent>();
            public void Observe(GameEvent gameEvent) { Seen.Add(gameEvent); }
        }

        private static readonly string[] EnterHooks = { BehaviorHooks.LivingEnter };

        private static Room NewRoom(string id)
        {
            return new Room(null) { Id = id };
        }

        private static EventParty[] Parties(Room room)
        {
            return new[] { new EventParty("to", room, EnterHooks) };
        }

        // ---- the record ----

        [Fact]
        public void RootEventHasUniqueIdTraceOfItsOwnIdAndNoCause()
        {
            var dispatcher = new EventDispatcher(null, null, s => { });
            GameEvent first = dispatcher.Emit("entity.move", EventPhase.After, Parties(NewRoom("a")));
            GameEvent second = dispatcher.Emit("entity.move", EventPhase.After, Parties(NewRoom("a")));

            Assert.NotEqual(first.EventId, second.EventId);
            Assert.Equal(first.EventId.ToString(), first.TraceId);
            Assert.Equal(0, first.CauseId);
            Assert.Equal(0, first.Depth);
            Assert.Equal(EventPhase.After, first.Phase);
            Assert.Equal("a", first.FindParty("to").Entity.Id);
            Assert.Null(first.FindParty("from"));
        }

        [Fact]
        public void EmittedEventCarriesCauseIdAndTraceOfTheEventThatProducedIt()
        {
            var handlers = new Handlers();
            var room = NewRoom("a");
            room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "m", "T"));
            handlers.Register("m", "T", d => d.Emit("x.child", d.Event.Parties));
            var dispatcher = new EventDispatcher(handlers, null, s => { });
            var recorder = new Recorder();
            dispatcher.AddObserver(recorder);

            GameEvent root = dispatcher.Emit("entity.move", EventPhase.After, Parties(room), new Dictionary<string, object> { { "reason", "walk" } });
            Assert.Equal(1, dispatcher.Pump(1));   // the child was queued, not run inside the handler
            Assert.Equal(1, dispatcher.QueuedCount);
            Assert.Equal(1, dispatcher.Pump(1));

            Assert.Equal(new[] { "entity.move", "x.child" }, recorder.Seen.Select(e => e.Name).ToArray());
            GameEvent seenChild = recorder.Seen[1];
            Assert.Equal(root.EventId, seenChild.CauseId);
            Assert.Equal(root.TraceId, seenChild.TraceId);
            Assert.Equal(1, seenChild.Depth);
            Assert.Equal("m", seenChild.Origin.Module);
            Assert.Equal("a", seenChild.Origin.EntityId);
            Assert.Equal("walk", root.Payload["reason"]);
        }

        // ---- After phase delivery ----

        [Fact]
        public void AfterPhaseDeliversOnlyToEntriesWithTheHookNamedForThatParty()
        {
            var handlers = new Handlers();
            var calls = new List<string>();
            handlers.Register("m", "Enter", d => calls.Add("enter@" + d.Party.Entity.Id + " role " + d.Party.Role + " hook " + d.Hook));
            handlers.Register("m", "Exit", d => calls.Add("exit@" + d.Party.Entity.Id));
            var from = NewRoom("from");
            var to = NewRoom("to");
            from.AddBehavior(new BehaviorRef(BehaviorHooks.LivingExit, "m", "Exit"));
            to.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "m", "Enter", 0, new BsonDocument("k", 1)));
            to.AddBehavior(new BehaviorRef(BehaviorHooks.Heartbeat, "m", "Exit")); // other hook: not delivered
            var dispatcher = new EventDispatcher(handlers, null, s => { });

            dispatcher.Emit("entity.move", EventPhase.After, new[]
            {
                new EventParty("from", from, new[] { BehaviorHooks.LivingExit }),
                new EventParty("to", to, new[] { BehaviorHooks.LivingEnter })
            });
            dispatcher.Pump();

            Assert.Equal(new[] { "exit@from", "enter@to role to hook living.enter" }, calls);
        }

        [Fact]
        public void DeliveryCarriesTheEntryItsConfig()
        {
            var handlers = new Handlers();
            BsonDocument seen = null;
            handlers.Register("m", "T", d => seen = d.Entry.Config);
            var room = NewRoom("a");
            room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "m", "T", 0, new BsonDocument("damage", 3)));
            var dispatcher = new EventDispatcher(handlers, null, s => { });

            dispatcher.Emit("entity.move", EventPhase.After, Parties(room));
            dispatcher.Pump();

            Assert.Equal(3, seen["damage"].ToInt32());
        }

        [Fact]
        public void UnresolvedEntryIsSkippedAndTheRestStillRun()
        {
            var handlers = new Handlers();
            int ran = 0;
            handlers.Register("m", "Live", d => ran++);
            var room = NewRoom("a");
            room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "gone", "Missing"));
            room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "m", "Live"));
            var dispatcher = new EventDispatcher(handlers, null, s => { });

            dispatcher.Emit("entity.move", EventPhase.After, Parties(room));
            dispatcher.Pump();

            Assert.Equal(1, ran);
        }

        [Fact]
        public void ThrowingHandlerIsLoggedAndDoesNotStopOtherHandlersOrEvents()
        {
            var handlers = new Handlers();
            int ran = 0;
            handlers.Register("m", "Bad", d => { throw new InvalidOperationException("boom"); });
            handlers.Register("m", "Good", d => ran++);
            var room = NewRoom("a");
            room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "m", "Bad"));
            room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "m", "Good"));
            var log = new List<string>();
            var dispatcher = new EventDispatcher(handlers, null, log.Add);

            dispatcher.Emit("entity.move", EventPhase.After, Parties(room));
            dispatcher.Emit("entity.move", EventPhase.After, Parties(room));
            Assert.Equal(2, dispatcher.Pump());

            Assert.Equal(2, ran);
            Assert.Equal(2, dispatcher.HandlerFailures);
            Assert.Contains(log, line => line.Contains("boom"));
        }

        [Fact]
        public void ObserverFailureIsLoggedAndDoesNotStopTheQueue()
        {
            var log = new List<string>();
            var dispatcher = new EventDispatcher(null, null, log.Add);
            dispatcher.AddObserver(new ThrowingObserver());
            dispatcher.Emit("a", EventPhase.After, null);
            dispatcher.Emit("b", EventPhase.After, null);

            Assert.Equal(2, dispatcher.Pump());
            Assert.Equal(2, dispatcher.HandlerFailures);
        }

        private class ThrowingObserver : IEventObserver
        {
            public void Observe(GameEvent gameEvent) { throw new InvalidOperationException("observer"); }
        }

        [Fact]
        public void BeforePhaseIsNotAcceptedYet()
        {
            var dispatcher = new EventDispatcher(null, null, s => { });
            Assert.Throws<NotSupportedException>(() => dispatcher.Emit("entity.move", EventPhase.Before, null));
        }

        // ---- guards ----

        [Fact]
        public void CascadeDepthLimitRefusesTheEventAndCountsIt()
        {
            var handlers = new Handlers();
            var room = NewRoom("a");
            room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "m", "Chain"));
            int depthReached = -1;
            // Each level's name differs, so the re-entrancy guard does not fire; only depth stops the chain.
            handlers.Register("m", "Chain", d =>
            {
                depthReached = d.Event.Depth;
                d.Emit("level." + (d.Event.Depth + 1), d.Event.Parties);
            });
            var log = new List<string>();
            var dispatcher = new EventDispatcher(handlers, new EventLimits { MaxCascadeDepth = 3 }, log.Add);

            dispatcher.Emit("level.0", EventPhase.After, Parties(room));
            while (dispatcher.Pump(100) > 0) { }

            Assert.Equal(3, depthReached);
            Assert.Equal(1, dispatcher.RejectedCascadeDepth);
            Assert.Single(log);
            Assert.Contains("cascade depth 3", log[0]);
            Assert.Contains("trace", log[0]);
            Assert.Contains("cause", log[0]);
        }

        [Fact]
        public void ReentrancyGuardRefusesAnEventWhoseCauseChainHasTheSameBehaviorEntityAndEventName()
        {
            var handlers = new Handlers();
            var room = NewRoom("a");
            room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "m", "Loop"));
            int calls = 0;
            handlers.Register("m", "Loop", d =>
            {
                calls++;
                d.Emit("entity.move", d.Event.Parties); // same name, same behavior, same entity
            });
            var log = new List<string>();
            var dispatcher = new EventDispatcher(handlers, new EventLimits { MaxCascadeDepth = 50 }, log.Add);

            dispatcher.Emit("entity.move", EventPhase.After, Parties(room));
            while (dispatcher.Pump(100) > 0) { }

            // The root has no origin, so the first emit is allowed; the second (origin now in the chain) is refused.
            Assert.Equal(2, calls);
            Assert.Equal(1, dispatcher.RejectedReentrancy);
            Assert.Contains(log, line => line.Contains("re-entrancy guard"));
        }

        [Fact]
        public void ReentrancyGuardAllowsADifferentEventNameOrEntity()
        {
            var handlers = new Handlers();
            var roomA = NewRoom("a");
            var roomB = NewRoom("b");
            roomA.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "m", "T"));
            roomB.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "m", "T"));
            var seen = new List<string>();
            handlers.Register("m", "T", d =>
            {
                seen.Add(d.Event.Name + "@" + d.Party.Entity.Id);
                if (d.Event.Name == "start") { d.Emit("next", new[] { new EventParty("to", roomB, EnterHooks) }); }
            });
            var dispatcher = new EventDispatcher(handlers, null, s => { });

            dispatcher.Emit("start", EventPhase.After, Parties(roomA));
            while (dispatcher.Pump(100) > 0) { }

            Assert.Equal(new[] { "start@a", "next@b" }, seen);
            Assert.Equal(0, dispatcher.RejectedReentrancy);
        }

        [Fact]
        public void PumpProcessesAtMostTheBudgetAndKeepsTheRestQueuedInOrder()
        {
            var dispatcher = new EventDispatcher(null, null, s => { });
            var recorder = new Recorder();
            dispatcher.AddObserver(recorder);
            for (int index = 0; index < 5; index++) { dispatcher.Emit("e" + index, EventPhase.After, null); }

            Assert.Equal(2, dispatcher.Pump(2));
            Assert.Equal(3, dispatcher.QueuedCount);
            Assert.Equal(2, dispatcher.Pump(2));
            Assert.Equal(1, dispatcher.Pump(2));
            Assert.Equal(new[] { "e0", "e1", "e2", "e3", "e4" }, recorder.Seen.Select(e => e.Name).ToArray());
        }

        [Fact]
        public void PumpInsideAHandlerDoesNothing()
        {
            var handlers = new Handlers();
            var room = NewRoom("a");
            room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "m", "T"));
            int nested = -1;
            EventDispatcher dispatcher = null;
            handlers.Register("m", "T", d =>
            {
                d.Emit("later", d.Event.Parties);
                nested = dispatcher.Pump(10);
            });
            dispatcher = new EventDispatcher(handlers, null, s => { });

            dispatcher.Emit("first", EventPhase.After, Parties(room));
            dispatcher.Pump(1);

            Assert.Equal(0, nested);
            Assert.Equal(1, dispatcher.QueuedCount);
        }

        // ---- the loop's share ----

        [Fact]
        public void GameLoopRunsEventProcessingAfterInboundItemsAndAfterATick()
        {
            var clock = new FakeClock();
            var order = new List<string>();
            var loop = new GameLoop(clock, item => order.Add("item"), () => order.Add("tick"), () => { }, s => { }, () => order.Add("events"));
            loop.Post(InboundItem.ForEvent(() => { }));

            loop.RunOnce(TimeSpan.Zero);
            Assert.Equal(new[] { "item", "events" }, order);

            order.Clear();
            clock.Advance(GameLoop.TickInterval);
            loop.RunOnce(TimeSpan.Zero);
            Assert.Equal(new[] { "events", "tick", "events" }, order);
        }

        [Fact]
        public void EventsOverTheBudgetWaitForTheNextLoopPass()
        {
            var clock = new FakeClock();
            var dispatcher = new EventDispatcher(null, new EventLimits { MaxEventsPerPass = 2 }, s => { });
            var loop = new GameLoop(clock, item => { }, () => { }, () => { }, s => { }, () => dispatcher.Pump());
            for (int index = 0; index < 5; index++) { dispatcher.Emit("e", EventPhase.After, null); }

            loop.RunOnce(TimeSpan.Zero);
            Assert.Equal(3, dispatcher.QueuedCount);
            loop.RunOnce(TimeSpan.Zero);
            Assert.Equal(1, dispatcher.QueuedCount);
            loop.RunOnce(TimeSpan.Zero);
            Assert.Equal(0, dispatcher.QueuedCount);
        }

        [Fact]
        public void InboundItemsStillRunWhileEventsAreQueuedOverTheBudget()
        {
            var clock = new FakeClock();
            var handled = 0;
            var dispatcher = new EventDispatcher(null, new EventLimits { MaxEventsPerPass = 1 }, s => { });
            var loop = new GameLoop(clock, item => handled++, () => { }, () => { }, s => { }, () => dispatcher.Pump());
            for (int index = 0; index < 10; index++) { dispatcher.Emit("e", EventPhase.After, null); }
            loop.Post(InboundItem.ForEvent(() => { }));
            loop.Post(InboundItem.ForEvent(() => { }));

            loop.RunOnce(TimeSpan.Zero);

            Assert.Equal(2, handled);
            Assert.Equal(9, dispatcher.QueuedCount);
        }

        [Fact]
        public void ThrowingEventProcessingIsLoggedAndTheLoopCarriesOn()
        {
            var clock = new FakeClock();
            var log = new List<string>();
            var loop = new GameLoop(clock, item => { }, () => { }, () => { }, log.Add, () => { throw new InvalidOperationException("pump"); });

            loop.RunOnce(TimeSpan.Zero);

            Assert.Contains(log, line => line.Contains("pump"));
        }

        // ---- change events from the phase 2 APIs ----

        [Fact]
        public void ComponentAndBehaviorEditsEmitChangeEventsWhenBoundAndObserved()
        {
            var dispatcher = new EventDispatcher(null, null, s => { });
            var recorder = new Recorder();
            dispatcher.AddObserver(recorder);
            var room = NewRoom("a");

            using (dispatcher.Bind())
            {
                room.SetComponent("desert.storm", new BsonDocument("strength", 2));
                room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "temple", "Trap", 4, new BsonDocument("damage", 3)));
                room.RemoveBehavior(BehaviorHooks.LivingEnter, "temple", "Trap");
                room.RemoveComponent("desert.storm");
            }
            dispatcher.Pump();

            Assert.Equal(new[] { "component.set", "behavior.add", "behavior.remove", "component.remove" }, recorder.Seen.Select(e => e.Name).ToArray());
            GameEvent set = recorder.Seen[0];
            Assert.Equal("desert.storm", set.Payload["component"]);
            Assert.Equal("a", set.FindParty("target").Entity.Id);
            var data = (IDictionary<string, object>)set.Payload["data"];
            Assert.Equal(2, Convert.ToInt32(data["strength"]));
            GameEvent add = recorder.Seen[1];
            Assert.Equal("temple", add.Payload["module"]);
            Assert.Equal(4, add.Payload["priority"]);
            Assert.Equal("Trap", recorder.Seen[2].Payload["type"]);
            Assert.Equal("desert.storm", recorder.Seen[3].Payload["component"]);
        }

        [Fact]
        public void FailedOrNoOpEditsEmitNothing()
        {
            var dispatcher = new EventDispatcher(null, null, s => { });
            dispatcher.AddObserver(new Recorder());
            var room = NewRoom("a");
            var entry = new BehaviorRef(BehaviorHooks.LivingEnter, "m", "T");

            using (dispatcher.Bind())
            {
                room.AddBehavior(entry);
                dispatcher.Pump();
                Assert.NotNull(room.AddBehavior(entry));                 // duplicate refused
                Assert.NotNull(room.AddBehavior(new BehaviorRef("nope", "m", "T"))); // unknown hook refused
                Assert.False(room.RemoveBehavior(BehaviorHooks.LivingExit, "m", "T"));
                Assert.False(room.RemoveComponent("desert.missing"));
                Assert.Equal(0, dispatcher.QueuedCount);
            }
        }

        [Fact]
        public void EditsWithNoBoundDispatcherOrNoObserverEmitNothingAndStillWork()
        {
            var room = NewRoom("a");
            room.SetComponent("desert.storm", new BsonDocument("strength", 1)); // nothing bound on this thread
            Assert.True(room.HasComponent("desert.storm"));

            var dispatcher = new EventDispatcher(null, null, s => { });        // bound but nobody observing
            using (dispatcher.Bind())
            {
                room.SetComponent("desert.storm", new BsonDocument("strength", 2));
            }
            Assert.Equal(0, dispatcher.QueuedCount);
        }

        [Fact]
        public void BindingIsPerThreadAndDisposeRestoresThePreviousOne()
        {
            var outer = new EventDispatcher(null, null, s => { });
            var inner = new EventDispatcher(null, null, s => { });
            Assert.Null(EventDispatcher.Current);
            using (outer.Bind())
            {
                Assert.Same(outer, EventDispatcher.Current);
                using (inner.Bind()) { Assert.Same(inner, EventDispatcher.Current); }
                Assert.Same(outer, EventDispatcher.Current);

                EventDispatcher onOtherThread = outer;
                var thread = new System.Threading.Thread(() => onOtherThread = EventDispatcher.Current);
                thread.Start();
                thread.Join();
                Assert.Null(onOtherThread);
            }
            Assert.Null(EventDispatcher.Current);
        }

        [Fact]
        public void ChangeEventsHaveNoHooksSoNoBehaviorEntryCanInterceptThem()
        {
            // Owner decision D59: change events are not hookable in version 1.
            var handlers = new Handlers();
            int ran = 0;
            foreach (string hook in BehaviorHooks.All) { handlers.Register("m", hook, d => ran++); }
            var dispatcher = new EventDispatcher(handlers, null, s => { });
            dispatcher.AddObserver(new Recorder());
            var room = NewRoom("a");
            foreach (string hook in BehaviorHooks.All) { room.AddBehavior(new BehaviorRef(hook, "m", hook)); }

            using (dispatcher.Bind())
            {
                room.SetComponent("desert.storm", new BsonDocument("strength", 2));
                room.RemoveComponent("desert.storm");
                room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "m", "other"));
                room.RemoveBehavior(BehaviorHooks.LivingEnter, "m", "other");
            }
            while (dispatcher.Pump(100) > 0) { }

            Assert.Equal(0, ran);
            Assert.Equal(0, EventPartyHooksOnChangeEvents());
        }

        private static int EventPartyHooksOnChangeEvents()
        {
            var dispatcher = new EventDispatcher(null, null, s => { });
            var recorder = new Recorder();
            dispatcher.AddObserver(recorder);
            using (dispatcher.Bind())
            {
                var room = NewRoom("a");
                room.SetComponent("desert.storm", new BsonDocument("strength", 2));
                room.AddBehavior(new BehaviorRef(BehaviorHooks.Heartbeat, "m", "T"));
            }
            dispatcher.Pump();
            return recorder.Seen.SelectMany(e => e.Parties).Sum(p => p.Hooks.Length);
        }

        [Fact]
        public void BleedingFromTheHeartbeatEmitsItsChangeEventsAndStillWorks()
        {
            var dispatcher = new EventDispatcher(null, null, s => { });
            var recorder = new Recorder();
            dispatcher.AddObserver(recorder);
            var player = new BleedingPlayer();

            using (dispatcher.Bind())
            {
                Assert.True(BleedingStatus.Apply(player, 1));
                player.DoHeartbeat();
            }
            dispatcher.Pump();

            Assert.False(BleedingStatus.IsBleeding(player));
            Assert.Equal(new[] { "component.set", "behavior.add", "component.remove", "behavior.remove" }, recorder.Seen.Select(e => e.Name).ToArray());
        }

        private class BleedingPlayer : Player
        {
            public BleedingPlayer() : base(null, null, new FakePlayerRepo())
            {
                PlayerName = "Bleeder";
                NameKey = "bleeder";
                ShortDescription = "Bleeder";
                Race = "human";
                Plurals = new List<string>();
            }

            public override void ReceiveMessage(string message) { }
        }

        // ---- the fast path ----

        // What is measured: the bytes the current thread allocates while the dispatcher delivers events whose parties
        // carry no matching behavior entries (empty lists, entries for other hooks, and parties with no hook names).
        // Only the result of this measurement is claimed; nothing else about allocation is.
        [Fact]
        public void DeliveryToPartiesWithNoMatchingEntriesAllocatesNothing()
        {
            var plain = NewRoom("plain");
            var otherHook = NewRoom("other");
            otherHook.AddBehavior(new BehaviorRef(BehaviorHooks.Heartbeat, "m", "T"));
            var parties = new[]
            {
                new EventParty("from", plain, new[] { BehaviorHooks.LivingExit }),
                new EventParty("to", otherHook, EnterHooks),
                new EventParty("note", plain)
            };
            var dispatcher = new EventDispatcher(null, null, s => { });
            const int rounds = 1000;
            dispatcher.Emit("entity.move", EventPhase.After, parties);
            dispatcher.Pump(); // warm up: JIT and first-call costs are not the subject
            for (int round = 0; round < rounds; round++) { dispatcher.Emit("entity.move", EventPhase.After, parties); } // outside the measured region

            long before = GC.GetAllocatedBytesForCurrentThread();
            int processed = dispatcher.Pump(rounds);
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.Equal(rounds, processed);
            Assert.Equal(0, allocated);
        }

        [Fact]
        public void PumpingAnEmptyQueueAllocatesNothing()
        {
            var dispatcher = new EventDispatcher(null, null, s => { });
            dispatcher.Pump();
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < 1000; round++) { dispatcher.Pump(); }
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        [Fact]
        public void ChangeEventHelpersAllocateNothingWhenNoDispatcherIsBoundOrNobodyObserves()
        {
            var room = NewRoom("a");
            var data = new BsonDocument("strength", 2);
            var entry = new BehaviorRef(BehaviorHooks.Heartbeat, "m", "T");
            var observerless = new EventDispatcher(null, null, s => { });

            ChangeEvents.EmitComponentSet(room, "desert.storm", data); // warm up
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int round = 0; round < 1000; round++)
            {
                ChangeEvents.EmitComponentSet(room, "desert.storm", data);
                ChangeEvents.EmitComponentRemove(room, "desert.storm");
                ChangeEvents.EmitBehaviorAdd(room, entry);
                ChangeEvents.EmitBehaviorRemove(room, "heartbeat", "m", "T");
            }
            Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);

            using (observerless.Bind())
            {
                before = GC.GetAllocatedBytesForCurrentThread();
                for (int round = 0; round < 1000; round++)
                {
                    ChangeEvents.EmitComponentSet(room, "desert.storm", data);
                    ChangeEvents.EmitBehaviorRemove(room, "heartbeat", "m", "T");
                }
                Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
                Assert.Equal(0, observerless.QueuedCount);
            }
        }
    }
}
