using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using MUD.Core.Import;
using MUD.Core.ItemTemplates;
using Xunit;

namespace MUD.Tests
{
    // Cards t_ae0cd319 / t_0c2a2723: a search reward that points at an item template ('template: <area>.<local>'), the 'clone' command.
    // In-memory stores only: nothing here ran against MongoDB.
    public class RewardTemplateImportTests
    {
        private const string Header = @"
area:
  schema_version: 1
  prefix: t
  name: Test
rooms:
  - key: hall
    short: a hall
    long: A plain hall.
    properties:
      - behavior: search
        id: s1
        params:
          target: table
          refresh_ms: 1000
";

        private static string WithReward(string rewardYaml) { return Header + "          reward:\n" + rewardYaml; }

        private static FakeItemTemplateStore Library(int version = 3)
        {
            var store = new FakeItemTemplateStore();
            store.Items["lib.sword"] = new ItemTemplateEntry
            {
                Id = "lib.sword", Area = "lib", Short = "a steel sword", Long = "A fine steel sword.", Handles = { "sword", "blade" }, Adjectives = { "steel" }, Plurals = { "swords" },
                Components = new BsonDocument("desert", new BsonDocument("edge", new BsonDocument("sharp", true))),
                Behaviors = { new MUD.Core.Components.BehaviorRef("item.take", "desert", "Blade", 1, new BsonDocument("k", "v")) },
                Version = version,
            };
            return store;
        }

        private static BsonDocument Reward(FakeStore rooms) { return rooms.Docs["t.hall"]["search"]["entries"].AsBsonArray[0]["reward"].AsBsonDocument; }

        [Fact]
        public void ATemplateReward_IsCopiedWithItsFieldsComponentsAndBehaviors_AndRecordsIdAndVersion()
        {
            var rooms = new FakeStore();

            var result = new AreaImporter(rooms, null, Library()).Import(WithReward("            template: lib.sword\n"));

            Assert.True(result.Success, string.Join("; ", result.Errors));
            var reward = Reward(rooms);
            Assert.Equal("a steel sword", reward["ShortDescription"].AsString);
            Assert.Equal("A fine steel sword.", reward["LongDescription"].AsString);
            Assert.Equal(new[] { "sword", "blade" }, reward["Handles"].AsBsonArray.Select(v => v.AsString));
            Assert.Equal(new[] { "steel" }, reward["Adjectives"].AsBsonArray.Select(v => v.AsString));
            Assert.Equal(new[] { "swords" }, reward["Plurals"].AsBsonArray.Select(v => v.AsString));
            Assert.True(reward["components"]["desert"]["edge"]["sharp"].AsBoolean);
            Assert.Equal("item.take", reward["behaviors"].AsBsonArray[0]["hook"].AsString);
            Assert.Equal("lib.sword", reward["templateId"].AsString);
            Assert.Equal(3, reward["templateVersion"].AsInt32);
        }

        [Fact]
        public void TheStoredCopyIsOrdinaryData_ItLoadsAsAnItemAndNeverReadsTheLibraryAgain()
        {
            var rooms = new FakeStore();
            var library = Library();
            new AreaImporter(rooms, null, library).Import(WithReward("            template: lib.sword\n"));

            library.Items.Clear();   // gameplay does not depend on a live lookup
            var item = BsonSerializer.Deserialize<InventoryItem>(Reward(rooms));

            Assert.Equal("a steel sword", item.ShortDescription);
            Assert.Equal(("lib.sword", 3), (item.TemplateId, item.TemplateVersion));
            Assert.True(item.Components["desert"]["edge"]["sharp"].AsBoolean);
            Assert.Equal("Blade", Assert.Single(item.Behaviors).Type);
        }

        [Fact]
        public void ShortAndLongOverrideTheTemplate_TheRestStaysCopied()
        {
            var rooms = new FakeStore();

            var result = new AreaImporter(rooms, null, Library()).Import(WithReward("            template: lib.sword\n            short: a cursed sword\n            long: It hums.\n"));

            Assert.True(result.Success, string.Join("; ", result.Errors));
            var reward = Reward(rooms);
            Assert.Equal(("a cursed sword", "It hums."), (reward["ShortDescription"].AsString, reward["LongDescription"].AsString));
            Assert.Equal(new[] { "sword", "blade" }, reward["Handles"].AsBsonArray.Select(v => v.AsString));
            Assert.Equal("lib.sword", reward["templateId"].AsString);
        }

        [Fact]
        public void OnlyShortOverridden_LongComesFromTheTemplate()
        {
            var rooms = new FakeStore();
            new AreaImporter(rooms, null, Library()).Import(WithReward("            template: lib.sword\n            short: a dull sword\n"));
            Assert.Equal(("a dull sword", "A fine steel sword."), (Reward(rooms)["ShortDescription"].AsString, Reward(rooms)["LongDescription"].AsString));
        }

        [Fact]
        public void AnUnknownTemplate_IsAValidationErrorNamingTheId_AndNothingIsWritten()
        {
            var rooms = new FakeStore();

            var result = new AreaImporter(rooms, null, Library()).Import(WithReward("            template: lib.nothing\n"));

            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("lib.nothing") && e.Contains("does not exist"));
            Assert.Equal(0, rooms.UpsertCount);
            Assert.Empty(rooms.Docs);
        }

        [Fact]
        public void EveryUnknownTemplateIsReported_NotJustTheFirst()
        {
            var yaml = WithReward("            template: lib.one\n") + @"  - key: study
    short: a study
    long: A study.
    properties:
      - behavior: search
        id: s2
        params:
          target: desk
          refresh_ms: 1000
          reward:
            template: lib.two
";
            var result = new AreaImporter(new FakeStore(), null, Library()).Import(yaml);
            Assert.Contains(result.Errors, e => e.Contains("lib.one"));
            Assert.Contains(result.Errors, e => e.Contains("lib.two"));
        }

        [Fact]
        public void WithoutALibrary_ATemplateRewardIsAnErrorNotSilentlyDropped()
        {
            var rooms = new FakeStore();
            var result = new AreaImporter(rooms).Import(WithReward("            template: lib.sword\n"));
            Assert.Contains(result.Errors, e => e.Contains("lib.sword") && e.Contains("not available"));
            Assert.Empty(rooms.Docs);
        }

        [Fact]
        public void ALibraryThatCannotBeRead_IsAnErrorNamingTheId()
        {
            var library = Library();
            library.FailReads = true;
            var result = new AreaImporter(new FakeStore(), null, library).Import(WithReward("            template: lib.sword\n"));
            Assert.Contains(result.Errors, e => e.Contains("lib.sword") && e.Contains("could not be read"));
            Assert.DoesNotContain(result.Errors, e => e.Contains("db down"));
        }

        [Theory]
        [InlineData("            template: nodot\n")]
        [InlineData("            template: .x\n")]
        [InlineData("            template: Lib.Sword\n")]
        [InlineData("            template: lib.\n")]
        public void ATemplateValueThatIsNotAnId_IsRejectedByTheParser_BeforeAnyLookup(string reward)
        {
            var library = Library();
            var result = new AreaImporter(new FakeStore(), null, library).Import(WithReward(reward));
            Assert.Contains(result.Errors, e => e.Contains("'template' is not a template id") || e.Contains("'template'"));
        }

        [Theory]
        [InlineData("handles")]
        [InlineData("adjectives")]
        [InlineData("plurals")]
        public void InlineWordListsCannotBeGivenWithATemplate(string key)
        {
            var result = new AreaImporter(new FakeStore(), null, Library()).Import(WithReward("            template: lib.sword\n            " + key + ": [x]\n"));
            Assert.Contains(result.Errors, e => e.Contains("'" + key + "' cannot be given with 'template'"));
        }

        [Fact]
        public void ATemplateMappedToNothing_IsRequiredToHaveAValue()
        {
            var result = new AreaImporter(new FakeStore(), null, Library()).Import(WithReward("            template: ''\n"));
            Assert.False(result.Success);
        }

        [Fact]
        public void AnInlineRewardStillNeedsShortAndLong_AndCarriesNoTemplateFields()
        {
            var rooms = new FakeStore();
            var bad = new AreaImporter(rooms, null, Library()).Import(WithReward("            handles: [coin]\n"));
            Assert.Contains(bad.Errors, e => e.Contains("'short' is required"));
            Assert.Contains(bad.Errors, e => e.Contains("'long' is required"));

            var ok = new AreaImporter(rooms, null, Library()).Import(WithReward("            short: a coin\n            long: A coin.\n"));
            Assert.True(ok.Success, string.Join("; ", ok.Errors));
            Assert.False(Reward(rooms).Contains("templateId"));
            Assert.False(Reward(rooms).Contains("templateVersion"));
        }

        [Fact]
        public void ReImportingAfterTheTemplateChanged_TakesTheCurrentFieldsAndVersion()
        {
            var rooms = new FakeStore();
            var library = Library(version: 1);
            var yaml = WithReward("            template: lib.sword\n");
            new AreaImporter(rooms, null, library).Import(yaml);
            Assert.Equal(1, Reward(rooms)["templateVersion"].AsInt32);

            library.Items["lib.sword"].Short = "a gleaming sword";
            library.Items["lib.sword"].Version = 2;
            var again = new AreaImporter(rooms, null, library).Import(yaml);

            Assert.True(again.Success, string.Join("; ", again.Errors));
            Assert.Equal(("a gleaming sword", 2), (Reward(rooms)["ShortDescription"].AsString, Reward(rooms)["templateVersion"].AsInt32));
        }

        [Fact]
        public void ADryRunResolvesAndValidatesButWritesNothing()
        {
            var rooms = new FakeStore();
            var ok = new AreaImporter(rooms, null, Library()).Import(WithReward("            template: lib.sword\n"), dryRun: true);
            Assert.True(ok.Success);
            Assert.Empty(rooms.Docs);
            var bad = new AreaImporter(rooms, null, Library()).Import(WithReward("            template: lib.zzz\n"), dryRun: true);
            Assert.False(bad.Success);
        }

        [Fact]
        public void Phase1StillPassesForGeneratorOutput_AnImporterWithoutALibraryTakesAFileWithNoTemplateRewards()
        {
            // The generators write no rewards, so an importer without a library (as GenerationService builds) still validates their files.
            var yaml = Header.Replace("          refresh_ms: 1000\n", "          refresh_ms: 1000\n          discovery: {id: found_it}\n");
            var result = new AreaImporter(new FakeStore()).Import(yaml);
            Assert.True(result.Success, string.Join("; ", result.Errors));
        }

        [Fact]
        public void ExportKeepsTheProvenance_AndTheExportedFileImportsBackWithoutALibrary()
        {
            var rooms = new FakeStore();
            new AreaImporter(rooms, null, Library()).Import(WithReward("            template: lib.sword\n"));

            var export = new AreaExporter(rooms).ExportArea("t");

            Assert.True(export.Success, string.Join("; ", export.Errors));
            Assert.Contains("x-gen:", export.Yaml);
            Assert.Contains("templateId: lib.sword", export.Yaml);
            Assert.Contains("templateVersion: 3", export.Yaml);
            var back = new FakeStore();
            var result = new AreaImporter(back).Import(export.Yaml);
            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Equal(("lib.sword", 3), (back.Docs["t.hall"]["search"]["entries"][0]["reward"]["templateId"].AsString, back.Docs["t.hall"]["search"]["entries"][0]["reward"]["templateVersion"].AsInt32));
        }

        [Theory]
        [InlineData("            x-gen: oops\n", "x-gen must be a mapping")]
        [InlineData("            x-gen: {templateVersion: 1}\n", "'templateId' is required")]
        [InlineData("            x-gen: {templateId: lib.sword}\n", "templateVersion is required")]
        public void ARewardXGenMustBeComplete(string gen, string expected)
        {
            var result = new AreaImporter(new FakeStore()).Import(WithReward("            short: a coin\n            long: A coin.\n" + gen));
            Assert.Contains(result.Errors, e => e.Contains(expected));
        }
    }

    public class CloneCommandTests
    {
        private class Recording : Player
        {
            public List<string> Messages = new List<string>();
            public Recording(World world) : base(world, null, new FakePlayerRepo()) { PlayerName = "Tester"; NameKey = "tester"; ShortDescription = "Tester"; }
            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private static Recording Person(bool creator)
        {
            // World's constructor registers global Mongo class maps and races with other tests, so it is skipped.
            var world = (World)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(World));
            world.Players = new List<Player>();
            var p = new Recording(world);
            p.Roles.Creator = creator;
            return p;
        }

        private static ItemTemplateEntry Lamp()
        {
            return new ItemTemplateEntry
            {
                Id = "t.lamp", Area = "t", Short = "a brass lamp", Long = "A brass lamp.", Handles = { "lamp" }, Version = 4,
                Components = new BsonDocument("desert", new BsonDocument("fuel", new BsonDocument("amount", 5))),
                Behaviors = { new MUD.Core.Components.BehaviorRef("item.take", "desert", "Lamp", 0, new BsonDocument("k", "v")) },
            };
        }

        private static void Run(ICommand command, Living issuer, string input) { command.DoCommand(issuer, command.ParseCommand(issuer, input)); }

        [Fact]
        public void ACreatorGetsACopyOfTheTemplateInTheInventory_WithComponentsAndBehaviors()
        {
            var p = Person(creator: true);
            var command = new CloneCommand(id => id == "t.lamp" ? Lamp() : null);

            Run(command, p, "clone t.lamp");

            var item = Assert.Single(p.Inventory);
            Assert.Equal(("a brass lamp", "A brass lamp."), (item.ShortDescription, item.LongDescription));
            Assert.Equal(new[] { "lamp" }, item.Handles);
            Assert.Equal(5, item.Components["desert"]["fuel"]["amount"].AsInt32);
            Assert.Equal("Lamp", Assert.Single(item.Behaviors).Type);
            Assert.Contains(p.Messages, m => m.Contains("a brass lamp") && m.Contains("t.lamp"));
        }

        [Fact]
        public void EachCloneIsANewItem_SoCloningTwiceGivesTwo()
        {
            var p = Person(creator: true);
            var command = new CloneCommand(id => Lamp());
            Run(command, p, "clone t.lamp");
            Run(command, p, "clone t.lamp");
            Assert.Equal(2, p.Inventory.Count);
            Assert.NotEqual(p.Inventory[0].Id, p.Inventory[1].Id);
        }

        [Fact]
        public void AnUnknownId_SaysSo_AndGivesNothing()
        {
            var p = Person(creator: true);
            Run(new CloneCommand(id => null), p, "clone t.nothing");
            Assert.Equal(new[] { "No such item template." }, p.Messages);
            Assert.Empty(p.Inventory);
        }

        [Fact]
        public void ANonCreatorIsRefused_AndTheLibraryIsNeverRead()
        {
            var p = Person(creator: false);
            int reads = 0;
            Run(new CloneCommand(id => { reads++; return Lamp(); }), p, "clone t.lamp");
            Assert.Equal(new[] { "This is a creator only command. How did you get this?!" }, p.Messages);
            Assert.Empty(p.Inventory);
            Assert.Equal(0, reads);
        }

        [Theory]
        [InlineData("clone")]
        [InlineData("clone    ")]
        public void NoIdGivesTheSyntax(string input)
        {
            var p = Person(creator: true);
            int reads = 0;
            Run(new CloneCommand(id => { reads++; return Lamp(); }), p, input);
            Assert.Equal(new[] { "Syntax: clone <area>.<local key>" }, p.Messages);
            Assert.Equal(0, reads);
        }

        [Fact]
        public void ALibraryThatCannotBeRead_ReportsWithoutDetails_AndGivesNothing()
        {
            var p = Person(creator: true);
            Run(new CloneCommand(id => throw new InvalidOperationException("secret connection text")), p, "clone t.lamp");
            var message = Assert.Single(p.Messages);
            Assert.Contains("could not be read", message);
            Assert.DoesNotContain("secret", message);
            Assert.Empty(p.Inventory);
        }

        [Fact]
        public void TheIdIsTrimmed()
        {
            var p = Person(creator: true);
            string asked = null;
            Run(new CloneCommand(id => { asked = id; return Lamp(); }), p, "clone   t.lamp  ");
            Assert.Equal("t.lamp", asked);
        }

        [Fact]
        public void TheKeywordIsCloneInTheCreatorCategory_AndHelpTextSaysHowToUseIt()
        {
            var command = new CloneCommand(id => null);
            Assert.Equal(new[] { "clone" }, command.CommandKeywords);
            Assert.Equal(CommandCategories.Creator, command.CommandCategory);
            Assert.Contains("clone <area>.<local key>", command.HelpText);
            Assert.Contains("item template", command.HelpText);
        }

        [Fact]
        public void TheWorldsDiScanCanStillConstructTheCommand_WithTheHostsDatabase()
        {
            var database = new MongoDB.Driver.MongoClient("mongodb://localhost:1/?connectTimeoutMS=1").GetDatabase("unused");
            var services = new ServiceCollection();
            services.AddSingleton<MongoDB.Driver.IMongoDatabase>(database);
            var provider = services.BuildServiceProvider();
            var instance = ActivatorUtilities.CreateInstance(provider, typeof(CloneCommand));
            Assert.IsType<CloneCommand>(instance);
        }
    }
}
