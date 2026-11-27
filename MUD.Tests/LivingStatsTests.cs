using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using Xunit;

namespace MUD.Tests
{
    public class LivingStatsTests
    {
        private static readonly string[] NewFields = { "MaxHealth", "Health", "MaxStamina", "Stamina", "MaxAlacrity", "Alacrity", "Experience",
            "Strength", "Dexterity", "Intelligence", "Wisdom", "Constitution", "Charisma" };

        private static void AssertDefaults(Living l)
        {
            Assert.Equal(1000, l.MaxHealth); Assert.Equal(1000, l.Health);
            Assert.Equal(1000, l.MaxStamina); Assert.Equal(1000, l.Stamina);
            Assert.Equal(1000, l.MaxAlacrity); Assert.Equal(1000, l.Alacrity);
            Assert.Equal(0L, l.Experience);
            Assert.Equal(10, l.Strength); Assert.Equal(10, l.Dexterity); Assert.Equal(10, l.Intelligence);
            Assert.Equal(10, l.Wisdom); Assert.Equal(10, l.Constitution); Assert.Equal(10, l.Charisma);
        }

        private static BsonDocument WithoutNewFields(BsonDocument doc)
        {
            foreach (string name in NewFields)
            {
                Assert.True(doc.Contains(name), name);
                doc.Remove(name);
            }
            doc.Add("someFutureField", 1); // extra elements are still ignored
            return doc;
        }

        [Fact]
        public void NewPlayerAndNpcHaveDefaults()
        {
            AssertDefaults(new Player(null, null, null));
            AssertDefaults(new NPC(null));
        }

        [Fact]
        public void StoredPlayerAndNpcWithoutTheFieldsLoadWithDefaultsNotZero()
        {
            var player = BsonSerializer.Deserialize<Player>(WithoutNewFields(new Player(null, null, null) { Id = "p1", PlayerName = "Ann" }.ToBsonDocument()));
            var npc = BsonSerializer.Deserialize<NPC>(WithoutNewFields(new NPC(null) { Id = "n1" }.ToBsonDocument()));

            Assert.Equal("Ann", player.PlayerName);
            AssertDefaults(player);
            AssertDefaults(npc);
        }

        [Fact]
        public void ValuesRoundTripAsInt32WithoutACustomSerializer()
        {
            var npc = new NPC(null) { Id = "n1", MaxHealth = 800, Health = 450, MaxStamina = 600, Stamina = 5, MaxAlacrity = 900, Alacrity = 0,
                Experience = 5_000_000_000L, Strength = 255, Dexterity = 0, Intelligence = 7, Wisdom = 8, Constitution = 9, Charisma = 11 };

            BsonDocument doc = npc.ToBsonDocument();
            Assert.Equal(BsonType.Int32, doc["Health"].BsonType);
            Assert.Equal(BsonType.Int32, doc["Strength"].BsonType);
            Assert.Equal(BsonType.Int64, doc["Experience"].BsonType);

            var back = BsonSerializer.Deserialize<NPC>(doc);
            Assert.Equal(450, back.Health); Assert.Equal(800, back.MaxHealth);
            Assert.Equal(5, back.Stamina); Assert.Equal(600, back.MaxStamina);
            Assert.Equal(0, back.Alacrity); Assert.Equal(900, back.MaxAlacrity);
            Assert.Equal(5_000_000_000L, back.Experience);
            Assert.Equal(255, back.Strength); Assert.Equal(0, back.Dexterity); Assert.Equal(7, back.Intelligence);
            Assert.Equal(8, back.Wisdom); Assert.Equal(9, back.Constitution); Assert.Equal(11, back.Charisma);
        }

        [Fact]
        public void CurrentValuesAreClampedToZeroAndMax()
        {
            var l = new NPC(null) { MaxHealth = 500 };
            l.Health = 900; Assert.Equal(500, l.Health);
            l.Health = -5; Assert.Equal(0, l.Health);
            l.Stamina = short.MaxValue; Assert.Equal(1000, l.Stamina);
            l.Alacrity = short.MinValue; Assert.Equal(0, l.Alacrity);
        }

        [Fact]
        public void MaxIsKeptWithinOneAndOneThousandAndReclampsTheCurrentValue()
        {
            var l = new NPC(null);
            l.MaxHealth = 400; Assert.Equal(400, l.Health);
            l.MaxStamina = 0; Assert.Equal(1, l.MaxStamina); Assert.Equal(1, l.Stamina);
            l.MaxAlacrity = short.MaxValue; Assert.Equal(1000, l.MaxAlacrity);
            l.MaxAlacrity = -3; Assert.Equal(1, l.MaxAlacrity); Assert.Equal(1, l.Alacrity);
            l.Health = 100; l.MaxHealth = 900; Assert.Equal(100, l.Health); // raising a max does not heal
        }

        [Fact]
        public void ExperienceIsNeverNegative()
        {
            var l = new NPC(null) { Experience = -1 };
            Assert.Equal(0L, l.Experience);
        }

        [Fact]
        public void StatusShowsTheNewLines()
        {
            var world = TestWorld.Create();
            var living = new RecordingLiving(world) { MaxHealth = 800, Health = 650, Stamina = 10, Experience = 5_000_000_000L, Strength = 14 };

            new StatusCommand().DoCommand(living, null);

            string text = string.Join("\n", living.Messages);
            Assert.Contains("Health: 650/800", text);
            Assert.Contains("Stamina: 10/1000", text);
            Assert.Contains("Alacrity: 1000/1000", text);
            Assert.Contains("Experience: 5000000000", text);
            Assert.Contains("Strength: 14", text);
            Assert.Contains("Charisma: 10", text);
        }
    }
}
