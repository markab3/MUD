using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using MUD.Core.Properties;
using MUD.Core.Properties.Interfaces;
using MUD.Core.Repositories.Interfaces;
using Xunit;

namespace MUD.Tests
{
    public class ConcurrencySafetyTests
    {
        // Builds a World without running its constructor. The constructor calls MongoDBClassMapManager.ClearClassMaps,
        // which mutates process-wide BSON class maps and races with other test classes running in parallel.
        // These tests only use World.Players and World.GetRoom(null), and GetRoom returns before touching services.
        private static World NewWorld()
        {
            var world = (World)RuntimeHelpers.GetUninitializedObject(typeof(World));
            world.Players = new List<Player>();
            return world;
        }

        // A Living that records every message it receives.
        private class RecordingLiving : Living
        {
            public List<string> Messages = new List<string>();

            public RecordingLiving(World world = null) : base(world) { }

            public override void ReceiveMessage(string message)
            {
                Messages.Add(message);
            }
        }

        // A Player whose heartbeat always fails.
        private class ThrowingPlayer : Player
        {
            public ThrowingPlayer(IPlayerRepository repo) : base(null, null, repo) { }

            public override void DoHeartbeat()
            {
                throw new InvalidOperationException("heartbeat failure");
            }
        }

        // A Player whose heartbeat counts ticks.
        private class CountingPlayer : Player
        {
            public int Ticks;

            public CountingPlayer(IPlayerRepository repo) : base(null, null, repo) { }

            public override void DoHeartbeat()
            {
                Ticks++;
            }
        }

        // A Player whose heartbeat removes another player from the world, mimicking a disconnect mid-tick.
        private class RemovingPlayer : Player
        {
            private readonly World _world;
            private readonly Player _victim;

            public RemovingPlayer(World world, Player victim, IPlayerRepository repo) : base(world, null, repo)
            {
                _world = world;
                _victim = victim;
            }

            public override void DoHeartbeat()
            {
                _world.Players.Remove(_victim);
            }
        }

        private class CountingProperty : ExtendedProperty, IHeartbeat
        {
            public int Ticks;

            public void DoHeartbeat() { Ticks++; }

            public override void InitializeProperty() { }

            public override void RemoveProperty() { }
        }

        private class PlainProperty : ExtendedProperty
        {
            public override void InitializeProperty() { }

            public override void RemoveProperty() { }
        }

        private static void RunHeartbeat(World world)
        {
            var method = typeof(World).GetMethod("doHeartbeat", BindingFlags.NonPublic | BindingFlags.Instance);
            Assert.NotNull(method);
            method.Invoke(world, new object[] { null });
        }

        private static InventoryItem Item(string name)
        {
            return new InventoryItem { ShortDescription = name, Handles = new List<string> { name }, Plurals = new List<string>() };
        }

        // Item 1: one player's failing heartbeat must not stop the others or escape the timer callback.
        [Fact]
        public void WorldHeartbeatContinuesPastAFailingPlayer()
        {
            var world = NewWorld();
            var repo = new FakePlayerRepo();
            var counting = new CountingPlayer(repo) { PlayerName = "counter" };
            world.Players.Add(new ThrowingPlayer(repo) { PlayerName = "thrower" });
            world.Players.Add(counting);

            RunHeartbeat(world);

            Assert.Equal(1, counting.Ticks);
        }

        // Item 1: players removed during the tick must not break enumeration.
        [Fact]
        public void WorldHeartbeatToleratesPlayerRemovedDuringTick()
        {
            var world = NewWorld();
            var repo = new FakePlayerRepo();
            var victim = new CountingPlayer(repo) { PlayerName = "victim" };
            world.Players.Add(new RemovingPlayer(world, victim, repo) { PlayerName = "remover" });
            world.Players.Add(victim);

            var ex = Record.Exception(() => RunHeartbeat(world));

            Assert.Null(ex);
        }

        // Item 2: only IHeartbeat properties tick; plain properties are ignored.
        [Fact]
        public void LivingHeartbeatTicksOnlyIHeartbeatProperties()
        {
            var living = new RecordingLiving();
            var heartbeat = new CountingProperty();
            living.ExtendedProperties.Add(new PlainProperty());
            living.ExtendedProperties.Add(heartbeat);

            living.DoHeartbeat();

            Assert.Equal(1, heartbeat.Ticks);
        }

        // Item 3: a recipient that leaves the room while receiving must not break TellRoom.
        [Fact]
        public void TellRoomToleratesOccupantLeavingDuringDelivery()
        {
            var roomRepo = new FakeRoomRepo();
            var room = new Room(roomRepo) { Id = "test.room", ShortDescription = "A room", LongDescription = "Desc" };
            var leaver = new LeavingLiving(room);
            var stayer = new RecordingLiving();
            room.EnterRoom(leaver, "leaver arrives");
            room.EnterRoom(stayer, "stayer arrives");

            var ex = Record.Exception(() => room.TellRoom("hello"));

            Assert.Null(ex);
            Assert.Contains("hello", stayer.Messages);
        }

        private class LeavingLiving : RecordingLiving
        {
            private readonly Room _room;

            public LeavingLiving(Room room) { _room = room; }

            public override void ReceiveMessage(string message)
            {
                base.ReceiveMessage(message);
                _room.ExitRoom(this);
            }
        }

        // Item 3: Examine must still list other occupants with the snapshot in place.
        [Fact]
        public void ExamineListsOtherOccupants()
        {
            var room = new Room(new FakeRoomRepo()) { Id = "test.room", ShortDescription = "A room", LongDescription = "Desc" };
            var examiner = new RecordingLiving();
            var other = new RecordingLiving { ShortDescription = "a stranger" };
            room.EnterRoom(examiner, "examiner arrives");
            room.EnterRoom(other, "other arrives");

            string text = room.Examine(examiner);

            Assert.Contains("a stranger", text);
        }

        // Item 4: a null destination must tell the mover the way is blocked and leave them where they are.
        [Fact]
        public void MoveToRoomWithNoDestinationLeavesMoverInPlace()
        {
            var world = NewWorld();
            var repo = new FakePlayerRepo();
            var room = new Room(new FakeRoomRepo()) { Id = "test.room", ShortDescription = "A room", LongDescription = "Desc" };
            var mover = new RecordingLiving(world) { ShortDescription = "mover" };
            room.EnterRoom(mover, "mover arrives");
            mover.CurrentLocation = room;
            mover.CurrentLocationId = room.Id;

            // An empty id makes World.GetRoom return null without touching the database.
            mover.MoveToRoom(null);

            Assert.Same(room, mover.CurrentLocation);
            Assert.Contains(mover, room.Occupants);
            Assert.Contains("The way is blocked.", mover.Messages);
        }

        // Item 5: "get" with no argument stops after the prompt and moves nothing.
        [Fact]
        public void GetWithoutArgumentMovesNothing()
        {
            var room = new Room(new FakeRoomRepo()) { Id = "test.room", ShortDescription = "A room", LongDescription = "Desc" };
            room.Items.Add(Item("sword"));
            var player = new RecordingLiving { ShortDescription = "tester" };
            player.CurrentLocation = room;

            new GetCommand().DoCommand(player, new object[] { "" });

            Assert.Single(player.Messages);
            Assert.Equal("Get what? Syntax: get <object>", player.Messages[0]);
            Assert.Single(room.Items);
            Assert.Empty(player.Inventory);
        }

        // Item 5: a successful get moves the item and reports it.
        [Fact]
        public void GetMovesItemFromRoomToInventory()
        {
            var room = new Room(new FakeRoomRepo()) { Id = "test.room", ShortDescription = "A room", LongDescription = "Desc" };
            var sword = Item("sword");
            room.Items.Add(sword);
            var player = new RecordingLiving { ShortDescription = "tester" };
            player.CurrentLocation = room;

            new GetCommand().DoCommand(player, new object[] { "sword" });

            Assert.Empty(room.Items);
            Assert.Contains(sword, player.Inventory);
            Assert.Contains(player.Messages, m => m.StartsWith("You get ") && m.Contains("sword"));
        }

        // Item 5: drop mirrors get; a dropped item leaves the inventory and lands in the room.
        [Fact]
        public void DropMovesItemFromInventoryToRoom()
        {
            var room = new Room(new FakeRoomRepo()) { Id = "test.room", ShortDescription = "A room", LongDescription = "Desc" };
            var sword = Item("sword");
            var player = new RecordingLiving { ShortDescription = "tester" };
            player.Inventory.Add(sword);
            player.CurrentLocation = room;

            new DropCommand().DoCommand(player, new object[] { "sword" });

            Assert.Empty(player.Inventory);
            Assert.Contains(sword, room.Items);
        }

        // Item 6: command errors reach the player as a generic message, never the exception text.
        [Fact]
        public void NotifyExceptionSendsGenericMessageOnly()
        {
            var player = new RecordingLiving { ShortDescription = "tester" };
            var execution = new CommandExecution(player, "boom", new FailingParseCommand());

            execution.NotifyException(new InvalidOperationException("secret internal detail"));

            Assert.Single(player.Messages);
            Assert.Equal("There was an error executing your command.", player.Messages[0]);
            Assert.DoesNotContain("secret internal detail", player.Messages[0]);
        }

        private class FailingParseCommand : ICommand
        {
            public string[] CommandKeywords { get => new string[] { "boom" }; }
            public CommandCategories CommandCategory { get => CommandCategories.Default; }
            public string HelpText { get => "boom"; }
            public object[] ParseCommand(Living commandIssuer, string input) { return new object[0]; }
            public void DoCommand(Living commandIssuer, object[] commandArgs) { }
        }
    }
}
