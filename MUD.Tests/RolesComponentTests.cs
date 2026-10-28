using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MUD.Core;
using MUD.Core.Components;
using MUD.Core.GameObjects;
using Xunit;

namespace MUD.Tests
{
    // The persisted shape of the roles component. The website (MUD.Web) reads the same document in another process,
    // so these field names are a contract (owner decisions D45, G30).
    public class RolesComponentTests
    {
        [Fact]
        public void PlayerIsStoredWithAnEmbeddedRolesDocument()
        {
            var world = TestWorld.Create();
            var player = new RecordingPlayer(world, "Ann");
            player.Roles.Creator = true;
            player.Roles.Admin = true;
            player.Roles.CurrentDirectory = "/home";

            BsonDocument doc = player.ToBsonDocument();

            Assert.True(doc.Contains("roles"));
            BsonDocument roles = doc["roles"].AsBsonDocument;
            Assert.True(roles["creator"].AsBoolean);
            Assert.True(roles["admin"].AsBoolean);
            Assert.Equal("/home", roles["currentDirectory"].AsString);
            Assert.False(doc.Contains("ExtendedProperties") && doc["ExtendedProperties"].AsBsonArray.Count > 0);
        }

        [Fact]
        public void RolesSurviveALoadAndSave()
        {
            var roles = new Roles { Creator = true, Admin = false, CurrentDirectory = "/area" };

            Roles loaded = BsonSerializer.Deserialize<Roles>(roles.ToBson());

            Assert.True(loaded.Creator);
            Assert.False(loaded.Admin);
            Assert.Equal("/area", loaded.CurrentDirectory);
        }

        [Fact]
        public void StoredDocumentWithoutRoles_LoadsAsAPlainLiving()
        {
            var world = TestWorld.Create();
            var living = new RecordingLiving(world);

            Assert.NotNull(living.Roles);
            Assert.False(living.Roles.Creator);
            Assert.False(living.Roles.Admin);
            Assert.Null(living.Roles.CurrentDirectory);
        }

        [Fact]
        public void UnknownFieldsInTheRolesDocumentAreIgnored()
        {
            var stored = new BsonDocument { { "creator", true }, { "admin", false }, { "futureField", 7 } };

            Roles loaded = BsonSerializer.Deserialize<Roles>(stored);

            Assert.True(loaded.Creator);
        }

        [Fact]
        public void RolesIsARegisteredCoreComponent()
        {
            var registry = new ComponentRegistry();
            registry.RegisterCore<Roles>(Roles.FieldName);

            Assert.Equal("roles", Roles.FieldName);
            Assert.Equal(ComponentKind.Core, registry.Classify("roles"));
            Assert.True(registry.TryGetCoreType("roles", out var type));
            Assert.Equal(typeof(Roles), type);
        }
    }
}
