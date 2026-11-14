using System;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MUD.Core.Components;
using MUD.Core.GameObjects;
using Xunit;

namespace MUD.Tests
{
    public class ComponentTests
    {
        private static BsonDocument Storm()
        {
            return new BsonDocument
            {
                { "intensity", 3 },
                { "label", "sandstorm" },
                { "nested", new BsonDocument { { "ids", new BsonArray { "a", "b" } }, { "ratio", 0.5 } } }
            };
        }

        [Fact]
        public void NewEntityHasNoComponentsAndStoresNoComponentsElementValues()
        {
            var room = new Room(null);
            Assert.Empty(room.Components);
            Assert.False(room.HasComponent("desert.storm"));
            Assert.Equal(new BsonDocument(), room.ToBsonDocument()["components"].AsBsonDocument);
        }

        [Fact]
        public void ModuleComponentIsStoredUnderComponentsModuleName()
        {
            var room = new Room(null) { Id = "t.hall" };
            room.SetComponent("desert.storm", Storm());

            var doc = room.ToBsonDocument();

            Assert.Equal(Storm(), doc["components"]["desert"]["storm"].AsBsonDocument);
        }

        [Fact]
        public void ComponentsSurviveSerializeAndDeserializeOnRoomAndPlayer()
        {
            var room = new Room(null) { Id = "t.hall" };
            room.SetComponent("desert.storm", Storm());
            room.SetComponent("desert.sand", new BsonDocument("depth", 2));
            room.SetComponent("temple.trap", new BsonDocument("armed", true));

            var reloaded = BsonSerializer.Deserialize<Room>(room.ToBsonDocument());

            Assert.Equal(Storm(), reloaded.Components["desert"]["storm"]);
            Assert.Equal(2, reloaded.Components["desert"].Count);
            Assert.True(reloaded.TryGetComponent("temple.trap", out var trap));
            Assert.True(trap["armed"].AsBoolean);

            var player = new Player(null, null, null) { Id = "p1" };
            player.SetComponent("desert.sand_blind", new BsonDocument("untilTick", 99L));
            var reloadedPlayer = BsonSerializer.Deserialize<Player>(player.ToBsonDocument());
            Assert.Equal(99L, reloadedPlayer.Components["desert"]["sand_blind"]["untilTick"].AsInt64);
        }

        // D10 and D13: data of a module that is not loaded, with a shape core knows nothing about, is never lost.
        [Fact]
        public void UnknownModuleDataIsPreservedExactlyThroughLoadAndSave()
        {
            var stored = new BsonDocument
            {
                { "_id", "t.hall" },
                { "ShortDescription", "A hall" },
                { "components", new BsonDocument
                    {
                        { "futuremodule", new BsonDocument
                            {
                                { "thing", new BsonDocument
                                    {
                                        { "text", "kept" }, { "long", 12345678901L }, { "date", new BsonDateTime(1000) },
                                        { "bytes", new BsonBinaryData(new byte[] { 1, 2, 3 }) }, { "nothing", BsonNull.Value },
                                        { "list", new BsonArray { 1, "two", new BsonDocument("three", 3) } }
                                    } }
                            } }
                    } }
            };

            var room = BsonSerializer.Deserialize<Room>(stored);
            var saved = room.ToBsonDocument();

            Assert.Equal(stored["components"], saved["components"]);
        }

        [Fact]
        public void ComponentSavedByTwoModulesOnTheSameEntityKeepsBoth()
        {
            var room = new Room(null);
            room.SetComponent("desert.storm", Storm());
            room.SetComponent("temple.trap", new BsonDocument("armed", false));
            Assert.True(room.RemoveComponent("desert.storm"));
            Assert.False(room.HasComponent("desert.storm"));
            Assert.True(room.HasComponent("temple.trap"));
        }

        [Fact]
        public void RemovingTheLastComponentOfAModuleDropsTheModuleEntry()
        {
            var room = new Room(null);
            room.SetComponent("desert.storm", Storm());
            Assert.True(room.RemoveComponent("desert.storm"));
            Assert.False(room.Components.ContainsKey("desert"));
            Assert.False(room.RemoveComponent("desert.storm"));
        }

        [Fact]
        public void SetComponentStoresACopy()
        {
            var room = new Room(null);
            var data = Storm();
            room.SetComponent("desert.storm", data);
            data["intensity"] = 99;
            Assert.True(room.TryGetComponent("desert.storm", out var stored));
            Assert.Equal(3, stored["intensity"].AsInt32);
        }

        [Theory]
        [InlineData("storm")]
        [InlineData("")]
        [InlineData(null)]
        [InlineData(".storm")]
        [InlineData("desert.")]
        [InlineData("desert.a.b")]
        [InlineData("desert.$set")]
        [InlineData(" desert.storm")]
        public void InvalidModuleComponentNamesAreRejected(string name)
        {
            var room = new Room(null);
            Assert.Throws<ArgumentException>(() => room.SetComponent(name, new BsonDocument()));
            Assert.False(room.HasComponent(name));
            Assert.False(room.RemoveComponent(name));
            Assert.Empty(room.Components);
        }

        [Fact]
        public void MissingComponentsElementLoadsAsEmptyDictionary()
        {
            var room = BsonSerializer.Deserialize<Room>(new BsonDocument { { "_id", "t.hall" } });
            Assert.NotNull(room.Components);
            Assert.Empty(room.Components);
        }

        [Fact]
        public void ApplyDefinitionCopiesComponents()
        {
            var cached = new Room(null) { Id = "t.hall" };
            var fresh = new Room(null) { Id = "t.hall" };
            fresh.SetComponent("desert.storm", Storm());
            cached.ApplyDefinition(fresh);
            Assert.True(cached.HasComponent("desert.storm"));
        }

        private class Roles { }

        [Fact]
        public void RegistryClassifiesCoreModuleAndUnknownNames()
        {
            var registry = new ComponentRegistry();
            registry.RegisterCore<Roles>("roles");

            Assert.Equal(ComponentKind.Core, registry.Classify("roles"));
            Assert.Equal(ComponentKind.Module, registry.Classify("desert.storm"));
            Assert.Equal(ComponentKind.Unknown, registry.Classify("nothing"));
            Assert.Equal(ComponentKind.Unknown, registry.Classify(null));
            Assert.True(registry.TryGetCoreType("roles", out var type));
            Assert.Equal(typeof(Roles), type);
            Assert.False(registry.TryGetCoreType("desert.storm", out _));
        }

        [Fact]
        public void RegistryRejectsDuplicateAndInvalidCoreNames()
        {
            var registry = new ComponentRegistry();
            registry.RegisterCore<Roles>("roles");
            Assert.Throws<InvalidOperationException>(() => registry.RegisterCore<Roles>("roles"));
            Assert.Throws<ArgumentException>(() => registry.RegisterCore<Roles>("a.b"));
            Assert.Throws<ArgumentException>(() => registry.RegisterCore<Roles>(""));
            Assert.Throws<ArgumentNullException>(() => registry.RegisterCore("x", null));
        }
    }
}
