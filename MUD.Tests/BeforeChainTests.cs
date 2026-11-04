using System;
using System.Collections.Generic;
using System.Linq;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.Components;
using MUD.Core.Events;
using MUD.Core.GameObjects;
using Xunit;

namespace MUD.Tests
{
    // The Before chain, parking, re-validation and verdicts (P3-2). Handlers are test doubles whose answers arrive after
    // scripted delays on a fake clock; no threads, no sleeping, no Mongo.
    [Collection("World construction")] // World registers BSON class maps; constructing it in parallel races
    public class BeforeChainTests
    {
        private readonly FakeClock _clock = new FakeClock();

        // Answers after a scripted delay. Deliveries wait in Pending until the test moves the clock and calls Release.
        private class ScriptedHandler : IEventHandler, IBeforeHandler
        {
            private readonly FakeClock _clock;
            public Func<BeforeDelivery, Verdict> Script;
            public TimeSpan Delay = TimeSpan.Zero;
            public readonly List<(DateTime Due, BeforeDelivery Delivery, Verdict Verdict)> Pending = new List<(DateTime, BeforeDelivery, Verdict)>();
            public readonly List<BeforeDelivery> Received = new List<BeforeDelivery>();
            public readonly List<VerdictAcknowledgement> Acknowledgements = new List<VerdictAcknowledgement>();
            public bool ThrowOnBegin;

            public ScriptedHandler(FakeClock clock, Verdict verdict = null, TimeSpan? delay = null)
            {
                _clock = clock;
                Script = d => verdict ?? Verdict.Allow();
                Delay = delay ?? TimeSpan.Zero;
            }

            public void Handle(EventDelivery delivery) { }

            public void Begin(BeforeDelivery delivery)
            {
                Received.Add(delivery);
                if (ThrowOnBegin) { throw new InvalidOperationException("scripted failure"); }
                Verdict verdict = Script(delivery);
                if (Delay == TimeSpan.Zero) { Acknowledgements.Add(delivery.Reply(verdict)); }
                else { Pending.Add((_clock.Now + Delay, delivery, verdict)); }
            }

            // Sends every scripted answer whose time has come.
            public void Release()
            {
                foreach (var entry in Pending.Where(p => p.Due <= _clock.Now).ToList())
                {
                    Pending.Remove(entry);
                    Acknowledgements.Add(entry.Delivery.Reply(entry.Verdict));
                }
            }
        }

        private class Resolver : IEventHandlerResolver
        {
            public readonly Dictionary<string, IEventHandler> ByKey = new Dictionary<string, IEventHandler>();
            public void Register(string module, string type, IEventHandler handler) { ByKey[module + "/" + type] = handler; }
            public bool TryResolve(string module, string type, out IEventHandler handler) { return ByKey.TryGetValue(module + "/" + type, out handler); }
        }

        private class Mover : Living
        {
            public readonly List<string> Messages = new List<string>();
            public Mover(World world, string name) : base(world) { ShortDescription = name; Id = "living." + name; }
            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private World NewWorld(out Resolver resolver)
        {
            var world = new World(new MongoDB.Driver.MongoClient("mongodb://127.0.0.1:1"), new WorldSettings(), _clock);
            resolver = new Resolver();
            world.Events.Resolver = resolver;
            return world;
        }

        private static Room NewRoom(string id, params Exit[] exits)
        {
            var room = new Room(new FakeRoomRepo()) { Id = id, ShortDescription = id, LongDescription = id + " long" };
            room.Exits.AddRange(exits);
            return room;
        }

        private static Mover Place(World world, Room room, string name)
        {
            var mover = new Mover(world, name);
            room.EnterRoom(mover, name + " arrives");
            mover.CurrentLocation = room;
            mover.CurrentLocationId = room.Id;
            return mover;
        }

        private static void Hook(GameObject entity, string hook, string module, string type, int priority = 0)
        {
            ((IBehaviorHolder)entity).Behaviors.Add(new BehaviorRef(hook, module, type, priority));
        }

        // ---- fast path ----

        [Fact]
        public void UnhookedMoveCommitsAtOnceAndNeverParks()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            var mover = Place(world, a, "ann");

            mover.MoveToRoom(b, "ann leaves.", null);

            Assert.Same(b, mover.CurrentLocation);
            Assert.False(mover.IsParked);
            Assert.Equal(0, world.Events.WaitingChains);
            Assert.Contains(mover, b.Occupants);
            Assert.DoesNotContain(mover, a.Occupants);
        }

        // ---- all handlers wait; deadline; first Deny ----

        [Fact]
        public void MoveWaitsForEveryHandlerThenCommitsWhenAllAllow()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            var mover = Place(world, a, "ann");
            var slow = new ScriptedHandler(_clock, Verdict.Allow(), TimeSpan.FromMilliseconds(100));
            var fast = new ScriptedHandler(_clock, Verdict.Allow());
            resolver.Register("m", "Slow", slow);
            resolver.Register("m", "Fast", fast);
            Hook(a, BehaviorHooks.LivingExit, "m", "Slow");
            Hook(b, BehaviorHooks.LivingEnter, "m", "Fast");

            mover.MoveToRoom(b, "ann leaves.", null);

            // Fast has answered; the chain still waits for Slow (no short-circuit), so ann has not moved and is parked.
            Assert.Same(a, mover.CurrentLocation);
            Assert.True(mover.IsParked);
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            slow.Release();
            Assert.Same(b, mover.CurrentLocation);
            Assert.False(mover.IsParked);
        }

        [Fact]
        public void AnyDenyVetoesAndTheFirstDenyToArriveGivesTheMessage()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            var mover = Place(world, a, "ann");
            var late = new ScriptedHandler(_clock, Verdict.Deny("late message"), TimeSpan.FromMilliseconds(80));
            var early = new ScriptedHandler(_clock, Verdict.Deny("early message"), TimeSpan.FromMilliseconds(20));
            var allow = new ScriptedHandler(_clock, Verdict.Allow());
            resolver.Register("m", "Late", late);
            resolver.Register("m", "Early", early);
            resolver.Register("m", "Allow", allow);
            // Listed late-first: arrival order, not list order, decides the message (D7).
            Hook(a, BehaviorHooks.LivingExit, "m", "Late");
            Hook(b, BehaviorHooks.LivingEnter, "m", "Early");
            Hook(mover, BehaviorHooks.LivingEnter, "m", "Allow");

            mover.MoveToRoom(b, "ann leaves.", null);
            _clock.Advance(TimeSpan.FromMilliseconds(20));
            early.Release();
            Assert.True(mover.IsParked); // the chain still waits for Late (no short-circuit on the first Deny)
            _clock.Advance(TimeSpan.FromMilliseconds(60));
            late.Release();

            Assert.Same(a, mover.CurrentLocation);
            Assert.Contains("early message", mover.Messages);
            Assert.DoesNotContain("late message", mover.Messages);
            Assert.False(mover.IsParked);
        }

        [Fact]
        public void MissedDeadlineFailsOpenAndTheMoveCommits()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            var mover = Place(world, a, "ann");
            var hung = new ScriptedHandler(_clock, Verdict.Deny("never seen"), TimeSpan.FromSeconds(60));
            resolver.Register("m", "Hung", hung);
            Hook(b, BehaviorHooks.LivingEnter, "m", "Hung");

            mover.MoveToRoom(b, "ann leaves.", null);
            Assert.True(mover.IsParked);

            _clock.Advance(world.Events.Limits.BeforeChainDeadline - TimeSpan.FromMilliseconds(1));
            world.Events.Pump();
            Assert.True(mover.IsParked); // not yet

            _clock.Advance(TimeSpan.FromMilliseconds(1));
            world.Events.Pump();
            Assert.Same(b, mover.CurrentLocation);
            Assert.False(mover.IsParked);
            Assert.Equal(1, world.Events.BeforeMisses);
            Assert.Equal(0, world.Events.BeforeClosedMisses);
        }

        [Fact]
        public void MissedDeadlineFailsClosedOnlyForAnEntryThePolicyNames()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            var mover = Place(world, a, "ann");
            var hung = new ScriptedHandler(_clock, Verdict.Allow(), TimeSpan.FromSeconds(60));
            resolver.Register("m", "Hung", hung);
            Hook(b, BehaviorHooks.LivingEnter, "m", "Hung");
            world.Events.FailPolicy.MarkClosed("m", "Hung");

            mover.MoveToRoom(b, "ann leaves.", null);
            _clock.Advance(world.Events.Limits.BeforeChainDeadline);
            world.Events.Pump();

            Assert.Same(a, mover.CurrentLocation);
            Assert.Contains(FailPolicy.ClosedMessage, mover.Messages);
            Assert.Equal(1, world.Events.BeforeClosedMisses);
        }

        [Fact]
        public void UnresolvedAndThrowingHandlersFailOpenAndDoNotBlockTheMove()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            var mover = Place(world, a, "ann");
            var throwing = new ScriptedHandler(_clock) { ThrowOnBegin = true };
            resolver.Register("m", "Throws", throwing);
            Hook(b, BehaviorHooks.LivingEnter, "m", "Throws");
            Hook(b, BehaviorHooks.LivingEnter, "gone", "Unloaded"); // nothing registered: dormant

            mover.MoveToRoom(b, "ann leaves.", null);

            Assert.Same(b, mover.CurrentLocation);
            Assert.Equal(2, world.Events.BeforeMisses);
            Assert.Equal(1, world.Events.HandlerFailures);
        }

        [Fact]
        public void LateVerdictIsDroppedAndCounted()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            var mover = Place(world, a, "ann");
            var handler = new ScriptedHandler(_clock, Verdict.Deny("too late"), TimeSpan.FromSeconds(1));
            resolver.Register("m", "Slow", handler);
            Hook(b, BehaviorHooks.LivingEnter, "m", "Slow");

            mover.MoveToRoom(b, "ann leaves.", null);
            _clock.Advance(world.Events.Limits.BeforeChainDeadline);
            world.Events.Pump();
            Assert.Same(b, mover.CurrentLocation);

            _clock.Advance(TimeSpan.FromSeconds(1));
            handler.Release();

            Assert.Equal(1, world.Events.LateVerdicts);
            Assert.False(handler.Acknowledgements.Single().Counted);
            Assert.DoesNotContain("too late", mover.Messages);
        }

        // ---- events emitted by handlers (D8, D61) ----

        [Fact]
        public void EventsEmittedByAHandlerApplyEvenWhenTheMoveIsDenied()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            var mover = Place(world, a, "ann");
            var seen = new List<string>();
            var noticer = new ScriptedHandler(_clock);
            resolver.Register("m", "Noticer", new AfterRecorder(seen));
            Hook(a, BehaviorHooks.Heartbeat, "m", "Noticer");
            var emitted = new[] { new EmittedEvent("m.noise", new[] { new EventParty("target", a, new[] { BehaviorHooks.Heartbeat }) }) };
            var denier = new ScriptedHandler(_clock, Verdict.Deny("no", emitted));
            resolver.Register("m", "Denier", denier);
            Hook(b, BehaviorHooks.LivingEnter, "m", "Denier");

            mover.MoveToRoom(b, "ann leaves.", null);
            world.Events.Pump();

            Assert.Same(a, mover.CurrentLocation);
            Assert.Equal(new[] { "m.noise" }, seen);
        }

        private class AfterRecorder : IEventHandler
        {
            private readonly List<string> _seen;
            public AfterRecorder(List<string> seen) { _seen = seen; }
            public void Handle(EventDelivery delivery) { _seen.Add(delivery.Event.Name); }
        }

        [Fact]
        public void RefusedEmittedEventIsSkippedAndTheRestApplyAndTheVerdictCounts()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            world.Events.Limits.MaxCascadeDepth = 0; // every emitted event is deeper than allowed
            var a = NewRoom("a");
            var b = NewRoom("b");
            var mover = Place(world, a, "ann");
            var emitted = new[] { new EmittedEvent("m.one", new[] { new EventParty("t", a) }), new EmittedEvent("m.two", new[] { new EventParty("t", a) }) };
            var denier = new ScriptedHandler(_clock, Verdict.Deny("no", emitted));
            resolver.Register("m", "Denier", denier);
            Hook(b, BehaviorHooks.LivingEnter, "m", "Denier");

            mover.MoveToRoom(b, "ann leaves.", null);

            var ack = denier.Acknowledgements.Single();
            Assert.True(ack.Counted);
            Assert.Equal(2, ack.RefusedEvents.Count);
            Assert.Equal(0, ack.AppliedEventCount);
            Assert.Same(a, mover.CurrentLocation); // the Deny still counted
        }

        [Fact]
        public void RedirectIsADenyPlusANewMoveWithItsOwnChain()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            var c = NewRoom("c");
            var mover = Place(world, a, "ann");
            var redirect = new[]
            {
                new EmittedEvent(EventNames.EntityMove, new[] { new EventParty("mover", mover), new EventParty("to", c) },
                    new Dictionary<string, object> { { "reason", "trap" } })
            };
            resolver.Register("m", "Redirector", new ScriptedHandler(_clock, Verdict.Deny("The floor gives way.", redirect)));
            Hook(b, BehaviorHooks.LivingEnter, "m", "Redirector");
            var watcher = new ScriptedHandler(_clock);
            resolver.Register("m", "Watcher", watcher);
            Hook(c, BehaviorHooks.LivingEnter, "m", "Watcher");

            mover.MoveToRoom(b, "ann leaves.", null);
            Assert.Same(a, mover.CurrentLocation); // denied; the redirect is queued, not run inline
            world.Events.Pump();

            Assert.Same(c, mover.CurrentLocation);
            Assert.Contains("The floor gives way.", mover.Messages);
            // The new move ran its own chain (the watcher on c was asked), caused by the first move's delivery.
            var second = watcher.Received.Single().Event;
            Assert.Equal(EventNames.EntityMove, second.Name);
            Assert.Equal(1, second.Depth);
        }

        // ---- parking, stop, re-validation ----

        [Fact]
        public void ParkedLivingQueuesNewCommandsAndStillReceivesOutput()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            var mover = Place(world, a, "ann");
            var handler = new ScriptedHandler(_clock, Verdict.Allow(), TimeSpan.FromMilliseconds(100));
            resolver.Register("m", "Slow", handler);
            Hook(b, BehaviorHooks.LivingEnter, "m", "Slow");
            var queue = new CommandQueue(_clock);
            var ran = new List<string>();
            var command = new AnonymousCommand { CommandKeywords = new[] { "wave" }, ParseCommandHandler = (l, i) => new object[0], DoCommandHandler = (l, x) => ran.Add("wave") };

            mover.MoveToRoom(b, "ann leaves.", null);
            queue.AddCommand(new CommandExecution(mover, "wave", command));
            queue.Dispatch();

            Assert.Empty(ran);
            Assert.Contains("Command queued.", mover.Messages);
            mover.ReceiveMessage("Someone shouts."); // output still arrives
            Assert.Contains("Someone shouts.", mover.Messages);

            _clock.Advance(TimeSpan.FromMilliseconds(100));
            handler.Release();
            queue.Dispatch();
            Assert.Equal(new[] { "wave" }, ran);
        }

        [Fact]
        public void StopCancelsTheWaitAndTheMoveDoesNotHappen()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            var mover = Place(world, a, "ann");
            var handler = new ScriptedHandler(_clock, Verdict.Allow(), TimeSpan.FromMilliseconds(100));
            resolver.Register("m", "Slow", handler);
            Hook(b, BehaviorHooks.LivingEnter, "m", "Slow");
            var queue = new CommandQueue(_clock);

            mover.MoveToRoom(b, "ann leaves.", null);
            Assert.True(mover.IsParked);
            queue.AddCommand(new CommandExecution(mover, "stop", new StopCommand()));

            Assert.False(mover.IsParked);
            Assert.Same(a, mover.CurrentLocation);
            Assert.Contains("You stop.", mover.Messages);

            // The handler's answer arrives afterwards: dropped and counted, the move still does not happen.
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            handler.Release();
            Assert.Same(a, mover.CurrentLocation);
            Assert.Equal(1, world.Events.LateVerdicts);
        }

        [Fact]
        public void MoverMovedByAThirdPartyWhileParkedAbortsWithAGenericMessage()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            var elsewhere = NewRoom("elsewhere");
            var mover = Place(world, a, "ann");
            var handler = new ScriptedHandler(_clock, Verdict.Allow(), TimeSpan.FromMilliseconds(100));
            resolver.Register("m", "Slow", handler);
            Hook(b, BehaviorHooks.LivingEnter, "m", "Slow");

            mover.MoveToRoom(b, "ann leaves.", null);
            // A third party moves ann (an unhooked destination commits at once): D62 allows this while parked.
            mover.MoveToRoom(elsewhere, "ann is whisked away.", null);
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            handler.Release();

            Assert.Same(elsewhere, mover.CurrentLocation);
            Assert.Contains("Things changed while you waited, so you did not do that.", mover.Messages);
        }

        [Fact]
        public void ExitRemovedWhileParkedAbortsTheWalk()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var b = NewRoom("b");
            var a = NewRoom("a", new Exit { Name = "north", DestinationId = "b" });
            var mover = Place(world, a, "ann");
            var handler = new ScriptedHandler(_clock, Verdict.Allow(), TimeSpan.FromMilliseconds(100));
            resolver.Register("m", "Slow", handler);
            Hook(b, BehaviorHooks.LivingEnter, "m", "Slow");

            // MoveToRoom(string) resolves the room through the repository; use the resolved overload with the exit name.
            mover.MoveToRoom(b, "ann leaves.", null, "walk", "north");
            a.Exits.Clear();
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            handler.Release();

            Assert.Same(a, mover.CurrentLocation);
            Assert.Contains("Things changed while you waited, so you did not do that.", mover.Messages);
        }

        [Fact]
        public void LivingWhoQuitWhileParkedDoesNotMove()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            var player = new Player(world, null, new FakePlayerRepo()) { Id = "p.ann", PlayerName = "ann", ShortDescription = "ann" };
            a.EnterRoom(player, "ann arrives");
            player.CurrentLocation = a;
            player.CurrentLocationId = a.Id;
            player.ConnectionStatus = PlayerConnectionStatuses.LoggedIn;
            var handler = new ScriptedHandler(_clock, Verdict.Allow(), TimeSpan.FromMilliseconds(100));
            resolver.Register("m", "Slow", handler);
            Hook(b, BehaviorHooks.LivingEnter, "m", "Slow");

            player.MoveToRoom(b, "ann leaves.", null);
            player.ConnectionStatus = PlayerConnectionStatuses.Offline;
            _clock.Advance(TimeSpan.FromMilliseconds(100));
            handler.Release();

            Assert.Same(a, player.CurrentLocation);
        }

        // ---- two simultaneous movers ----

        [Fact]
        public void TwoSimultaneousMoversEachGetTheirOwnChainAndAnswersAreNotMixedUp()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            var ann = Place(world, a, "ann");
            var bob = Place(world, a, "bob");
            // The trap lets the first arrival through and turns the second one back, like a module that serialises itself.
            int arrivals = 0;
            var trap = new ScriptedHandler(_clock);
            trap.Script = delivery => ++arrivals == 1 ? Verdict.Allow() : Verdict.Deny("The trap has already sprung.");
            trap.Delay = TimeSpan.FromMilliseconds(40);
            resolver.Register("m", "Trap", trap);
            Hook(b, BehaviorHooks.LivingEnter, "m", "Trap");

            ann.MoveToRoom(b, "ann leaves.", null);
            bob.MoveToRoom(b, "bob leaves.", null);
            Assert.True(ann.IsParked);
            Assert.True(bob.IsParked);
            Assert.Equal(2, world.Events.WaitingChains);
            Assert.NotEqual(trap.Received[0].DeliveryId, trap.Received[1].DeliveryId);

            _clock.Advance(TimeSpan.FromMilliseconds(40));
            trap.Release();

            Assert.Same(b, ann.CurrentLocation);
            Assert.Same(a, bob.CurrentLocation);
            Assert.Contains("The trap has already sprung.", bob.Messages);
            Assert.DoesNotContain("The trap has already sprung.", ann.Messages);
            Assert.False(ann.IsParked);
            Assert.False(bob.IsParked);
        }

        // ---- the mover's own entries (D54) ----

        [Fact]
        public void MoversOwnEntriesAreAskedUnderBothMoveHooks()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            var mover = Place(world, a, "ann");
            var handler = new ScriptedHandler(_clock);
            resolver.Register("m", "Own", handler);
            Hook(mover, BehaviorHooks.LivingExit, "m", "Own");
            Hook(mover, BehaviorHooks.LivingEnter, "m", "Own");
            Hook(a, BehaviorHooks.LivingEnter, "m", "Own"); // wrong hook for the room being left: not asked
            Hook(b, BehaviorHooks.LivingExit, "m", "Own");  // wrong hook for the room being entered: not asked

            mover.MoveToRoom(b, "ann leaves.", null);

            Assert.Equal(new[] { BehaviorHooks.LivingExit, BehaviorHooks.LivingEnter }, handler.Received.Select(d => d.Hook).ToArray());
            Assert.Same(b, mover.CurrentLocation);
        }

        [Fact]
        public void SnapshotsCarryCoreDataOnlyAndTheEntrysOwnConfig()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            b.SetComponent("desert.storm", new MongoDB.Bson.BsonDocument("strength", 3));
            var mover = Place(world, a, "ann");
            var handler = new ScriptedHandler(_clock);
            resolver.Register("m", "Reader", handler);
            b.Behaviors.Add(new BehaviorRef(BehaviorHooks.LivingEnter, "m", "Reader", 0, new MongoDB.Bson.BsonDocument("level", 7)));

            mover.MoveToRoom(b, "ann leaves.", null);

            var delivery = handler.Received.Single();
            Assert.Equal(7, delivery.Entry.Config["level"].AsInt32);
            Assert.Equal(new[] { "mover", "from", "to" }, delivery.Snapshots.Select(s => s.Role).ToArray());
            Assert.Equal("room", delivery.Snapshots.Single(s => s.Role == "to").Kind);
        }

        [Fact]
        public void AfterNotificationsGoToTheMoverAndBothRoomsOnceTheMoveCommitted()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var a = NewRoom("a");
            var b = NewRoom("b");
            var mover = Place(world, a, "ann");
            var seen = new List<string>();
            var recorder = new AfterRecorder(seen);
            resolver.Register("m", "Watcher", recorder);
            Hook(mover, BehaviorHooks.LivingEnter, "m", "Watcher");
            Hook(a, BehaviorHooks.LivingExit, "m", "Watcher");
            Hook(b, BehaviorHooks.LivingEnter, "m", "Watcher");
            resolver.Register("m", "Allow", new ScriptedHandler(_clock));
            Hook(b, BehaviorHooks.LivingEnter, "m", "Allow"); // makes the move a Before chain

            mover.MoveToRoom(b, "ann leaves.", null);
            Assert.Empty(seen); // After runs from the queue, after the commit
            world.Events.Pump();

            Assert.Equal(3, seen.Count); // mover (enter), from-room (exit), to-room (enter)
            Assert.All(seen, name => Assert.Equal(EventNames.EntityMove, name));
        }

        // ---- command.entered (D63, D64) ----

        private Player NewPlayer(World world, Room room)
        {
            var queue = new CommandQueue(_clock);
            var player = new Player(world, queue, new FakePlayerRepo()) { Id = "p.ann", PlayerName = "ann", ShortDescription = "ann" };
            room.EnterRoom(player, "ann arrives");
            player.CurrentLocation = room;
            player.CurrentLocationId = room.Id;
            player.ConnectionStatus = PlayerConnectionStatuses.LoggedIn;
            player.KnownCommands = new string[0];
            return player;
        }

        private class ProbePlayer : Player
        {
            public readonly List<string> Messages = new List<string>();
            public ProbePlayer(World world, CommandQueue queue) : base(world, queue, new FakePlayerRepo()) { }
            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        [Fact]
        public void CommandEnteredAsksNearestFirstAndTheFirstHandledStopsTheChain()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var room = NewRoom("a");
            var queue = new CommandQueue(_clock);
            var player = new ProbePlayer(world, queue) { Id = "p.ann", PlayerName = "ann", ShortDescription = "ann", KnownCommands = new string[0] };
            room.EnterRoom(player, "ann arrives");
            player.CurrentLocation = room;
            player.CurrentLocationId = room.Id;
            player.ConnectionStatus = PlayerConnectionStatuses.LoggedIn;
            var order = new List<string>();
            var roomItem = new InventoryItem { Id = "i.floor", ShortDescription = "floor thing" };
            var carried = new InventoryItem { Id = "i.carried", ShortDescription = "carried thing" };
            room.Items.Add(roomItem);
            player.Inventory.Add(carried);

            Func<string, Verdict, ScriptedHandler> make = (name, verdict) =>
            {
                var handler = new ScriptedHandler(_clock);
                handler.Script = d => { order.Add(name); return verdict; };
                resolver.Register("m", name, handler);
                return handler;
            };
            make("Own", Verdict.Allow());
            make("Carried", Verdict.Allow());
            make("Room", Verdict.Handled("The lever clanks."));
            make("Floor", Verdict.Handled("never reached"));
            // Stored in the opposite order of closeness, to show that closeness decides the order.
            Hook(roomItem, BehaviorHooks.CommandEntered, "m", "Floor");
            Hook(room, BehaviorHooks.CommandEntered, "m", "Room");
            Hook(carried, BehaviorHooks.CommandEntered, "m", "Carried");
            Hook(player, BehaviorHooks.CommandEntered, "m", "Own");

            player.DataReceivedHandler(null, "pull lever");

            Assert.Equal(new[] { "Own", "Carried", "Room" }, order);
            Assert.Contains("The lever clanks.", player.Messages);
            Assert.DoesNotContain("Command not recognized.", player.Messages); // handled: no built-in command ran
        }

        [Fact]
        public void CommandEnteredNotHandledFallsThroughToTheBuiltInCommand()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var room = NewRoom("a");
            var queue = new CommandQueue(_clock);
            var player = new ProbePlayer(world, queue) { Id = "p.ann", PlayerName = "ann", ShortDescription = "ann", KnownCommands = new string[0] };
            room.EnterRoom(player, "ann arrives");
            player.CurrentLocation = room;
            player.CurrentLocationId = room.Id;
            resolver.Register("m", "Pass", new ScriptedHandler(_clock));
            Hook(room, BehaviorHooks.CommandEntered, "m", "Pass");

            player.DataReceivedHandler(null, "xyzzy");

            Assert.Contains("Command not recognized.", player.Messages);
        }

        [Fact]
        public void CommandEnteredMissOnOneHopMovesOnToTheNextBehavior()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var room = NewRoom("a");
            var queue = new CommandQueue(_clock);
            var player = new ProbePlayer(world, queue) { Id = "p.ann", PlayerName = "ann", ShortDescription = "ann", KnownCommands = new string[0] };
            room.EnterRoom(player, "ann arrives");
            player.CurrentLocation = room;
            player.CurrentLocationId = room.Id;
            var hung = new ScriptedHandler(_clock, Verdict.Handled("hung"), TimeSpan.FromSeconds(30));
            var good = new ScriptedHandler(_clock, Verdict.Handled("The room answers."));
            resolver.Register("m", "Hung", hung);
            resolver.Register("m", "Good", good);
            Hook(player, BehaviorHooks.CommandEntered, "m", "Hung");
            Hook(room, BehaviorHooks.CommandEntered, "m", "Good");

            player.DataReceivedHandler(null, "chant");
            Assert.True(player.IsParked);
            Assert.Empty(good.Received); // sequential: the next behavior is not asked while one hop waits

            _clock.Advance(world.Events.Limits.BeforeChainDeadline);
            world.Events.Pump();

            Assert.Contains("The room answers.", player.Messages);
            Assert.DoesNotContain("hung", player.Messages);
            Assert.False(player.IsParked);
        }

        [Fact]
        public void CommandEnteredOrdersAGroupByPriorityThenStoredOrder()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var room = NewRoom("a");
            var queue = new CommandQueue(_clock);
            var player = new ProbePlayer(world, queue) { Id = "p.ann", PlayerName = "ann", ShortDescription = "ann", KnownCommands = new string[0] };
            room.EnterRoom(player, "ann arrives");
            player.CurrentLocation = room;
            player.CurrentLocationId = room.Id;
            var order = new List<string>();
            Action<string> make = name =>
            {
                var handler = new ScriptedHandler(_clock);
                handler.Script = d => { order.Add(name); return Verdict.Allow(); };
                resolver.Register("m", name, handler);
            };
            foreach (var name in new[] { "First", "Second", "Low", "High" }) { make(name); }
            Hook(player, BehaviorHooks.CommandEntered, "m", "High", 5);
            Hook(player, BehaviorHooks.CommandEntered, "m", "First", 1);
            Hook(player, BehaviorHooks.CommandEntered, "m", "Second", 1);
            Hook(player, BehaviorHooks.CommandEntered, "m", "Low", 0);

            player.DataReceivedHandler(null, "xyzzy");

            Assert.Equal(new[] { "Low", "First", "Second", "High" }, order);
        }

        [Fact]
        public void QuitIsProtectedAndNeverOffered()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var room = NewRoom("a");
            var queue = new CommandQueue(_clock);
            var player = new ProbePlayer(world, queue) { Id = "p.ann", PlayerName = "ann", ShortDescription = "ann", KnownCommands = new string[0] };
            room.EnterRoom(player, "ann arrives");
            player.CurrentLocation = room;
            player.CurrentLocationId = room.Id;
            var handler = new ScriptedHandler(_clock, Verdict.Handled("swallowed"));
            resolver.Register("m", "Swallow", handler);
            Hook(room, BehaviorHooks.CommandEntered, "m", "Swallow");

            player.DataReceivedHandler(null, "quit");

            Assert.Empty(handler.Received);
            Assert.Contains("Thanks for playing!", player.Messages);
        }

        [Fact]
        public void LinesTypedWhileACommandChainWaitsAreHeldInOrderAndStopEndsTheWait()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var room = NewRoom("a");
            var queue = new CommandQueue(_clock);
            var player = new ProbePlayer(world, queue) { Id = "p.ann", PlayerName = "ann", ShortDescription = "ann", KnownCommands = new string[0] };
            room.EnterRoom(player, "ann arrives");
            player.CurrentLocation = room;
            player.CurrentLocationId = room.Id;
            var slow = new ScriptedHandler(_clock, Verdict.Allow(), TimeSpan.FromSeconds(30));
            resolver.Register("m", "Slow", slow);
            Hook(room, BehaviorHooks.CommandEntered, "m", "Slow");

            player.DataReceivedHandler(null, "first");
            player.DataReceivedHandler(null, "second");
            Assert.Contains("Command queued.", player.Messages);

            // stop is not held behind the chain: it ends the wait (D62).
            queue.AddCommand(new CommandExecution(player, "stop", new StopCommand()));

            Assert.False(player.IsParked);
            Assert.Contains("You stop.", player.Messages);
        }

        [Fact]
        public void NoCommandEnteredEntryMeansNoEventAndNoDelay()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var room = NewRoom("a");
            var queue = new CommandQueue(_clock);
            var player = new ProbePlayer(world, queue) { Id = "p.ann", PlayerName = "ann", ShortDescription = "ann", KnownCommands = new string[0] };
            room.EnterRoom(player, "ann arrives");
            player.CurrentLocation = room;
            player.CurrentLocationId = room.Id;

            player.DataReceivedHandler(null, "xyzzy");

            Assert.Contains("Command not recognized.", player.Messages);
            Assert.False(player.IsParked);
            Assert.Equal(0, world.Events.Processed);
        }

        // ---- get and drop (item.take, item.drop) ----

        private static InventoryItem Thing(string name)
        {
            return new InventoryItem { Id = "i." + name, ShortDescription = name, Handles = new List<string> { name }, Plurals = new List<string>() };
        }

        [Fact]
        public void GetWithAnItemTakeDenyLeavesTheItemWhereItWasAndSaysWhy()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var room = NewRoom("a");
            var mover = Place(world, room, "ann");
            var statue = Thing("statue");
            room.Items.Add(statue);
            resolver.Register("m", "Immovable", new ScriptedHandler(_clock, Verdict.Deny("The statue will not budge.")));
            Hook(statue, BehaviorHooks.ItemTake, "m", "Immovable");

            new GetCommand().DoCommand(mover, new object[] { "statue" });

            Assert.Contains(statue, room.Items);
            Assert.Empty(mover.Inventory);
            Assert.Contains("The statue will not budge.", mover.Messages);
        }

        [Fact]
        public void GetWaitsForTheChainThenMovesTheItem()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var room = NewRoom("a");
            var mover = Place(world, room, "ann");
            var coin = Thing("coin");
            room.Items.Add(coin);
            var handler = new ScriptedHandler(_clock, Verdict.Allow(), TimeSpan.FromMilliseconds(50));
            resolver.Register("m", "Slow", handler);
            Hook(coin, BehaviorHooks.ItemTake, "m", "Slow");

            new GetCommand().DoCommand(mover, new object[] { "coin" });
            Assert.True(mover.IsParked);
            Assert.Contains(coin, room.Items);

            _clock.Advance(TimeSpan.FromMilliseconds(50));
            handler.Release();

            Assert.DoesNotContain(coin, room.Items);
            Assert.Contains(coin, mover.Inventory);
            Assert.Contains("You get coin.", mover.Messages);
            Assert.False(mover.IsParked);
        }

        [Fact]
        public void DropIsOfferedToTheRoomUnderItemDropAndADenyKeepsTheItem()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var room = NewRoom("a");
            var mover = Place(world, room, "ann");
            var coin = Thing("coin");
            mover.Inventory.Add(coin);
            resolver.Register("m", "Altar", new ScriptedHandler(_clock, Verdict.Deny("The altar rejects it.")));
            Hook(room, BehaviorHooks.ItemDrop, "m", "Altar");

            new DropCommand().DoCommand(mover, new object[] { "coin" });

            Assert.Contains(coin, mover.Inventory);
            Assert.DoesNotContain(coin, room.Items);
            Assert.Contains("The altar rejects it.", mover.Messages);
        }

        [Fact]
        public void GetAndDropWithNoEntriesBehaveAsBefore()
        {
            Resolver resolver;
            var world = NewWorld(out resolver);
            var room = NewRoom("a");
            var mover = Place(world, room, "ann");
            var coin = Thing("coin");
            room.Items.Add(coin);

            new GetCommand().DoCommand(mover, new object[] { "coin" });
            Assert.Contains(coin, mover.Inventory);
            Assert.False(mover.IsParked);

            new DropCommand().DoCommand(mover, new object[] { "coin" });
            Assert.Contains(coin, room.Items);
            Assert.Empty(mover.Inventory);
        }
    }
}
