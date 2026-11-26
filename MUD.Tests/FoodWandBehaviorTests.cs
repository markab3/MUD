using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.Components;
using MUD.Core.GameObjects;
using Xunit;

namespace MUD.Tests
{
    // Food and Wand as behaviors any item can carry: a command.entered entry gives the holder the verb, a component
    // holds the state. Everything runs on fakes; there is no Mongo.
    [Collection("World construction")] // World registers BSON class maps; constructing it in parallel races
    public class FoodWandBehaviorTests
    {
        private readonly FakeClock _clock = new FakeClock();

        private class FoodWandPlayer : Player
        {
            public readonly List<string> Messages = new List<string>();
            public FoodWandPlayer(World world, CommandQueue queue, FakePlayerRepo repo) : base(world, queue, repo) { }
            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private class Watcher : Living
        {
            public readonly List<string> Messages = new List<string>();
            public Watcher(World world) : base(world) { ShortDescription = "bob"; Id = "living.bob"; }
            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private class Scene
        {
            public World World;
            public Room Room;
            public FoodWandPlayer Player;
            public Watcher Watcher;
            public FakePlayerRepo Repo;
        }

        private Scene NewScene()
        {
            var world = new World(new MongoDB.Driver.MongoClient("mongodb://127.0.0.1:1"), new WorldSettings(), _clock);
            var room = new Room(new FakeRoomRepo()) { Id = "t.hall", ShortDescription = "a hall", LongDescription = "A hall." };
            var repo = new FakePlayerRepo();
            var player = new FoodWandPlayer(world, new CommandQueue(_clock), repo) { Id = "p.ann", PlayerName = "ann", ShortDescription = "ann", KnownCommands = new string[0] };
            room.EnterRoom(player, "ann arrives");
            player.CurrentLocation = room;
            player.CurrentLocationId = room.Id;
            player.ConnectionStatus = PlayerConnectionStatuses.LoggedIn;
            var watcher = new Watcher(world);
            room.EnterRoom(watcher, "bob arrives");
            watcher.CurrentLocation = room;
            return new Scene { World = world, Room = room, Player = player, Watcher = watcher, Repo = repo };
        }

        private static InventoryItem Bread(int servings)
        {
            var item = new InventoryItem { Id = "i.bread", ShortDescription = "loaf of bread", Handles = new List<string> { "bread" }, Plurals = new List<string>() };
            FoodBehavior.Attach(item, servings);
            return item;
        }

        private static InventoryItem Wand(int current, int max)
        {
            var item = new InventoryItem { Id = "i.wand", ShortDescription = "wand of missiles", Handles = new List<string> { "wand" }, Plurals = new List<string>() };
            WandBehavior.Attach(item, current, max);
            return item;
        }

        // ---- attaching and persisting ----

        [Fact]
        public void AttachingFoodStoresTheComponentAndOneCommandEnteredEntry()
        {
            var item = Bread(3);

            Assert.Equal(3, FoodBehavior.Servings(item));
            Assert.Equal(new BsonDocument("servings", 3), item.Components["food"]["servings"]);
            BehaviorRef entry = Assert.Single(item.Behaviors);
            Assert.Equal(BehaviorHooks.CommandEntered, entry.Hook);
            Assert.Equal("food", entry.Module);
            Assert.Equal("Food", entry.Type);

            FoodBehavior.Attach(item, 5); // attaching again resets the state but does not duplicate the entry
            Assert.Single(item.Behaviors);
            Assert.Equal(5, FoodBehavior.Servings(item));
        }

        [Fact]
        public void AttachingAWandKeepsCurrentWithinZeroAndMax()
        {
            var item = Wand(8, 10);
            Assert.Equal(8, WandBehavior.Charges(item));
            Assert.Equal(10, WandBehavior.MaxCharges(item));
            Assert.Equal(new BsonDocument { { "current", 8 }, { "max", 10 } }, item.Components["wand"]["charges"]);

            WandBehavior.Attach(item, 50, 10);
            Assert.Equal(10, WandBehavior.Charges(item));
            WandBehavior.Attach(item, -3, 10);
            Assert.Equal(0, WandBehavior.Charges(item));
        }

        [Fact]
        public void FoodAndWandSurviveSerializeAndDeserialize()
        {
            var bread = Bread(2);
            var wand = Wand(4, 7);

            var reloadedBread = BsonSerializer.Deserialize<InventoryItem>(bread.ToBsonDocument());
            var reloadedWand = BsonSerializer.Deserialize<InventoryItem>(wand.ToBsonDocument());

            Assert.Equal(2, FoodBehavior.Servings(reloadedBread));
            Assert.Equal("Food", Assert.Single(reloadedBread.Behaviors).Type);
            Assert.Equal(4, WandBehavior.Charges(reloadedWand));
            Assert.Equal(7, WandBehavior.MaxCharges(reloadedWand));
            Assert.Equal("Wand", Assert.Single(reloadedWand.Behaviors).Type);
        }

        [Fact]
        public void BothBehaviorsAreRegisteredInTheSharedRegistry()
        {
            Assert.True(InProcessBehaviorRegistry.Shared.TryResolve("food", "Food", out var food));
            Assert.IsType<FoodBehavior>(food);
            Assert.True(InProcessBehaviorRegistry.Shared.TryResolve("wand", "Wand", out var wand));
            Assert.IsType<WandBehavior>(wand);
        }

        // ---- eating ----

        [Fact]
        public void EatingUsesAServingAndTellsThePlayerAndTheRoom()
        {
            Scene scene = NewScene();
            InventoryItem bread = Bread(3);
            scene.Player.Inventory.Add(bread);

            scene.Player.DataReceivedHandler(null, "eat bread");

            Assert.Equal(2, FoodBehavior.Servings(bread));
            Assert.Contains("You eat some of the loaf of bread.", scene.Player.Messages);
            Assert.Contains("ann eats some of the loaf of bread.", scene.Watcher.Messages);
            Assert.DoesNotContain("ann eats some of the loaf of bread.", scene.Player.Messages);
            Assert.DoesNotContain("Command not recognized.", scene.Player.Messages);
            Assert.Contains(bread, scene.Player.Inventory);
        }

        [Fact]
        public void EatingTheLastServingRemovesTheItemAndSavesThePlayer()
        {
            Scene scene = NewScene();
            InventoryItem bread = Bread(1);
            scene.Player.Inventory.Add(bread);

            scene.Player.DataReceivedHandler(null, "eat bread");

            Assert.Empty(scene.Player.Inventory);
            Assert.Contains("You eat some of the loaf of bread.", scene.Player.Messages);
            Assert.Contains("You finish the loaf of bread.", scene.Player.Messages);
            // Persisted: the stored player no longer holds the item.
            BsonDocument stored = scene.Repo.Docs["p.ann"];
            Assert.Empty(stored["Inventory"].AsBsonArray);
        }

        [Fact]
        public void FoodWithNoServingsLeftStaysAndSaysThereIsNothingLeft()
        {
            Scene scene = NewScene();
            InventoryItem bread = Bread(0);
            scene.Player.Inventory.Add(bread);

            scene.Player.DataReceivedHandler(null, "eat bread");

            Assert.Contains("There is nothing left of the loaf of bread.", scene.Player.Messages);
            Assert.Contains(bread, scene.Player.Inventory);
            Assert.Equal(0, FoodBehavior.Servings(bread));
            Assert.Empty(scene.Watcher.Messages);
        }

        [Fact]
        public void EatWithoutAnObjectAsksWhat()
        {
            Scene scene = NewScene();
            InventoryItem bread = Bread(2);
            scene.Player.Inventory.Add(bread);

            scene.Player.DataReceivedHandler(null, "eat");

            Assert.Contains("Eat what? Syntax: eat <object>", scene.Player.Messages);
            Assert.Equal(2, FoodBehavior.Servings(bread));
        }

        [Fact]
        public void TheVerbIsOnlyAvailableWhileTheItemIsCarried()
        {
            Scene scene = NewScene();
            InventoryItem bread = Bread(3);
            scene.Room.Items.Add(bread); // on the floor, not in the inventory

            scene.Player.DataReceivedHandler(null, "eat bread");

            Assert.Contains("Command not recognized.", scene.Player.Messages);
            Assert.Equal(3, FoodBehavior.Servings(bread));
        }

        [Fact]
        public void EatingSomethingElseOrAnotherVerbIsNotTaken()
        {
            Scene scene = NewScene();
            InventoryItem bread = Bread(3);
            scene.Player.Inventory.Add(bread);

            scene.Player.DataReceivedHandler(null, "eat cheese");
            scene.Player.DataReceivedHandler(null, "zap bread");

            Assert.Equal(2, scene.Player.Messages.Count(m => m == "Command not recognized."));
            Assert.Equal(3, FoodBehavior.Servings(bread));
        }

        [Fact]
        public void ServingsAndInventoryReloadAfterAPlayerSave()
        {
            Scene scene = NewScene();
            scene.Player.Inventory.Add(Bread(3));
            scene.Player.DataReceivedHandler(null, "eat bread");
            scene.Player.Save(true);

            var reloaded = BsonSerializer.Deserialize<Player>(scene.Repo.Docs["p.ann"]);

            InventoryItem item = Assert.Single(reloaded.Inventory);
            Assert.Equal(2, FoodBehavior.Servings(item));
            Assert.Equal("Food", Assert.Single(item.Behaviors).Type);
        }

        // ---- zapping ----

        [Theory]
        [InlineData("zap wand")]
        [InlineData("use wand")]
        public void ZapAndUseSpendACharge(string line)
        {
            Scene scene = NewScene();
            InventoryItem wand = Wand(2, 5);
            scene.Player.Inventory.Add(wand);

            scene.Player.DataReceivedHandler(null, line);

            Assert.Equal(1, WandBehavior.Charges(wand));
            Assert.Equal(5, WandBehavior.MaxCharges(wand));
            Assert.Contains("You zap the wand of missiles. Nothing happens.", scene.Player.Messages);
            Assert.DoesNotContain("Command not recognized.", scene.Player.Messages);
        }

        [Fact]
        public void ADepletedWandFizzlesAndStaysInTheInventory()
        {
            Scene scene = NewScene();
            InventoryItem wand = Wand(1, 5);
            scene.Player.Inventory.Add(wand);

            scene.Player.DataReceivedHandler(null, "zap wand");
            scene.Player.DataReceivedHandler(null, "zap wand");
            scene.Player.DataReceivedHandler(null, "zap wand");

            Assert.Equal(1, scene.Player.Messages.Count(m => m == "You zap the wand of missiles. Nothing happens."));
            Assert.Equal(2, scene.Player.Messages.Count(m => m == "The wand of missiles fizzles; it has no charges left."));
            Assert.Equal(0, WandBehavior.Charges(wand));
            Assert.Equal(5, WandBehavior.MaxCharges(wand));
            Assert.Contains(wand, scene.Player.Inventory);
        }

        [Fact]
        public void ZapWithoutAnObjectAsksWhatAndKeepsTheCharge()
        {
            Scene scene = NewScene();
            InventoryItem wand = Wand(2, 5);
            scene.Player.Inventory.Add(wand);

            scene.Player.DataReceivedHandler(null, "zap");

            Assert.Contains("Use what? Syntax: zap <object>", scene.Player.Messages);
            Assert.Equal(2, WandBehavior.Charges(wand));
        }

        [Fact]
        public void ChargesReloadAfterAPlayerSave()
        {
            Scene scene = NewScene();
            scene.Player.Inventory.Add(Wand(3, 6));
            scene.Player.DataReceivedHandler(null, "zap wand");
            scene.Player.Save(true);

            var reloaded = BsonSerializer.Deserialize<Player>(scene.Repo.Docs["p.ann"]);

            InventoryItem item = Assert.Single(reloaded.Inventory);
            Assert.Equal(2, WandBehavior.Charges(item));
            Assert.Equal(6, WandBehavior.MaxCharges(item));
        }

        [Fact]
        public void TheItemInTheLineDecidesWhichBehaviorActs()
        {
            Scene scene = NewScene();
            InventoryItem bread = Bread(2);
            InventoryItem wand = Wand(2, 2);
            scene.Player.Inventory.Add(bread);
            scene.Player.Inventory.Add(wand);

            scene.Player.DataReceivedHandler(null, "zap wand");
            scene.Player.DataReceivedHandler(null, "eat bread");

            Assert.Equal(1, WandBehavior.Charges(wand));
            Assert.Equal(1, FoodBehavior.Servings(bread));
        }
    }
}
