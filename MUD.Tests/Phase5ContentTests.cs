using System;
using System.IO;
using System.Linq;
using MongoDB.Bson;
using MUD.Core.Import;
using Xunit;

namespace MUD.Tests
{
    /// <summary>
    /// The two area files that the phase 5 acceptance walkthrough imports (card P5-3): MUD.Main/Content/desert.yaml and temple.yaml.
    /// They lead into each other, and an import refuses an exit whose destination does not exist yet, so the cross-area exits are made
    /// with 'terraform link' after both are imported (specs/ACCEPTANCE_PHASE5.md). These tests keep the files importable and keep the
    /// room keys that the behavior commands of the walkthrough name. They use the in-memory store from AreaImporterTests: no database.
    /// </summary>
    public class Phase5ContentTests
    {
        private static string ContentPath(string fileName)
        {
            string directory = AppContext.BaseDirectory;
            while (directory != null)
            {
                string candidate = Path.Combine(directory, "MUD.Main", "Content", fileName);
                if (File.Exists(candidate)) { return candidate; }
                directory = Path.GetDirectoryName(directory);
            }
            throw new FileNotFoundException("Content file not found above " + AppContext.BaseDirectory + ": " + fileName);
        }

        private static ImportResult Import(FakeStore store, string fileName)
        {
            return new AreaImporter(store).Import(File.ReadAllText(ContentPath(fileName)));
        }

        [Fact]
        public void TheDesertAreaImportsWithFourDunesInARow()
        {
            var store = new FakeStore();
            var result = Import(store, "desert.yaml");

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(new[] { "desert.dune_1", "desert.dune_2", "desert.dune_3", "desert.dune_4" }, store.Docs.Keys.OrderBy(key => key).ToArray());
            var east = store.Docs["desert.dune_1"]["Exits"].AsBsonArray.Single(exit => exit["Name"] == "east");
            Assert.Equal("desert.dune_2", east["DestinationId"].AsString);
        }

        [Fact]
        public void TheTempleAreaImportsWithTheRoomsTheBehaviorCommandsName()
        {
            var store = new FakeStore();
            var result = Import(store, "temple.yaml");

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(
                new[] { "temple.antechamber", "temple.corridor_2", "temple.corridor_3", "temple.pit", "temple.sanctum" },
                store.Docs.Keys.OrderBy(key => key).ToArray());
        }

        [Fact]
        public void ThePitHasNoTrapAndClimbsBackToTheAntechamber()
        {
            var store = new FakeStore();
            Import(store, "temple.yaml");

            var exits = store.Docs["temple.pit"]["Exits"].AsBsonArray;
            Assert.Single(exits);
            Assert.Equal("up", exits[0]["Name"].AsString);
            Assert.Equal("temple.antechamber", exits[0]["DestinationId"].AsString);
        }

        [Fact]
        public void TheFilesCarryNoBehaviorEntriesBecauseTheyAreAttachedWithTheCreatorCommand()
        {
            var store = new FakeStore();
            Import(store, "desert.yaml");
            Import(store, "temple.yaml");

            foreach (var document in store.Docs.Values)
            {
                BsonValue behaviors;
                Assert.True(!document.TryGetValue("behaviors", out behaviors) || behaviors.AsBsonArray.Count == 0, document["_id"] + " carries behavior entries");
            }
        }

        [Fact]
        public void TheAntechamberDescriptionMentionsTheLeverBecauseFeaturesAreInert()
        {
            var store = new FakeStore();
            Import(store, "temple.yaml");

            Assert.Contains("lever", store.Docs["temple.antechamber"]["LongDescription"].AsString, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ReimportingBothAreasWritesNothingNew()
        {
            var store = new FakeStore();
            Import(store, "desert.yaml");
            Import(store, "temple.yaml");
            int writes = store.UpsertCount;

            Assert.True(Import(store, "desert.yaml").Success);
            Assert.True(Import(store, "temple.yaml").Success);

            Assert.Equal(writes, store.UpsertCount);
        }
    }
}
