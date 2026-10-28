using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MUD.Core.Commands;
using MUD.Core.Components;
using MUD.Core.GameObjects;
using MUD.Core.Import;
using MUD.Core.Properties.PlayerProperties;
using Xunit;

namespace MUD.Tests
{
    // The BehaviorRef list on rooms, items, players and NPCs (P2-2). Mongo is not available here: persistence is
    // checked through BSON serialization, not a database.
    public class BehaviorRefTests
    {
        private class RecordingPlayer : Player
        {
            public List<string> Messages = new List<string>();

            public RecordingPlayer(string name) : base(null, null, new FakePlayerRepo())
            {
                PlayerName = name;
                NameKey = name.ToLowerInvariant();
                ShortDescription = name;
                Race = "human";
                Plurals = new List<string>();
            }

            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private static BehaviorRef Trap()
        {
            return new BehaviorRef(BehaviorHooks.LivingEnter, "temple", "DesertTempleTrap", 5, new BsonDocument { { "damage", 3 }, { "label", "spikes" } }, 2);
        }

        // ---- model and persistence ----

        [Fact]
        public void NewEntitiesStartWithAnEmptyList()
        {
            Assert.Empty(new Room(null).Behaviors);
            Assert.Empty(new InventoryItem().Behaviors);
            Assert.Empty(new Player(null, null, null).Behaviors);
            Assert.Empty(new NPC(null).Behaviors);
        }

        [Fact]
        public void FeaturesDoNotCarryBehaviors()
        {
            Assert.False(typeof(IBehaviorHolder).IsAssignableFrom(typeof(Feature)));
            Assert.Null(typeof(Feature).GetProperty("Behaviors"));
        }

        [Fact]
        public void EntryIsStoredAsAnEmbeddedArrayWithTheDecidedFieldNames()
        {
            var room = new Room(null) { Id = "t.hall" };
            room.AddBehavior(Trap());

            var stored = room.ToBsonDocument()["behaviors"].AsBsonArray.Single().AsBsonDocument;

            Assert.Equal(new[] { "hook", "module", "type", "priority", "config", "schemaVersion" }, stored.Names.ToArray());
            Assert.Equal("living.enter", stored["hook"].AsString);
            Assert.Equal("temple", stored["module"].AsString);
            Assert.Equal("DesertTempleTrap", stored["type"].AsString);
            Assert.Equal(5, stored["priority"].AsInt32);
            Assert.Equal(2, stored["schemaVersion"].AsInt32);
            Assert.True(stored["config"].IsBsonDocument);
            Assert.Equal(3, stored["config"]["damage"].AsInt32);
        }

        [Fact]
        public void EntriesSurviveSerializeAndDeserializeOnRoomItemPlayerAndNpc()
        {
            var room = new Room(null) { Id = "t.hall" };
            room.AddBehavior(Trap());
            var item = new InventoryItem { Id = "i1" };
            item.AddBehavior(new BehaviorRef(BehaviorHooks.ItemTake, "curses", "Sticky"));
            var player = new Player(null, null, null) { Id = "p1" };
            player.AddBehavior(new BehaviorRef(BehaviorHooks.Heartbeat, "status", "Regeneration"));
            var npc = new NPC(null) { Id = "n1" };
            npc.AddBehavior(new BehaviorRef(BehaviorHooks.CommandEntered, "guards", "Challenge"));

            var reloadedRoom = BsonSerializer.Deserialize<Room>(room.ToBsonDocument());
            var reloadedItem = BsonSerializer.Deserialize<InventoryItem>(item.ToBsonDocument());
            var reloadedPlayer = BsonSerializer.Deserialize<Player>(player.ToBsonDocument());
            var reloadedNpc = BsonSerializer.Deserialize<NPC>(npc.ToBsonDocument());

            var entry = reloadedRoom.Behaviors.Single();
            Assert.Equal("living.enter", entry.Hook);
            Assert.Equal("temple", entry.Module);
            Assert.Equal("DesertTempleTrap", entry.Type);
            Assert.Equal(5, entry.Priority);
            Assert.Equal(2, entry.SchemaVersion);
            Assert.Equal(new BsonDocument { { "damage", 3 }, { "label", "spikes" } }, entry.Config);
            Assert.Equal("item.take", reloadedItem.Behaviors.Single().Hook);
            Assert.Equal("Regeneration", reloadedPlayer.Behaviors.Single().Type);
            Assert.Equal("guards", reloadedNpc.Behaviors.Single().Module);
        }

        [Fact]
        public void ItemInsideARoomKeepsItsEntries()
        {
            var room = new Room(null) { Id = "t.hall" };
            var item = new InventoryItem { Id = "i1" };
            item.AddBehavior(new BehaviorRef(BehaviorHooks.ItemDrop, "curses", "Sticky"));
            room.Items.Add(item);

            var reloaded = BsonSerializer.Deserialize<Room>(room.ToBsonDocument());

            Assert.Equal("item.drop", reloaded.Items.Single().Behaviors.Single().Hook);
        }

        [Fact]
        public void StoredDocumentWithoutBehaviorsLoadsWithAnEmptyList()
        {
            var doc = new Room(null) { Id = "t.hall" }.ToBsonDocument();
            doc.Remove("behaviors");

            Assert.Empty(BsonSerializer.Deserialize<Room>(doc).Behaviors);
        }

        [Fact]
        public void UnknownExtraFieldsOnAnEntryAreIgnoredAndMissingOnesDefault()
        {
            var doc = new Room(null) { Id = "t.hall" }.ToBsonDocument();
            doc["behaviors"] = new BsonArray
            {
                new BsonDocument { { "hook", "heartbeat" }, { "module", "m" }, { "type", "T" }, { "future", 1 } }
            };

            var entry = BsonSerializer.Deserialize<Room>(doc).Behaviors.Single();

            Assert.Equal(0, entry.Priority);
            Assert.Equal(1, entry.SchemaVersion);
            Assert.Empty(entry.Config);
        }

        // ---- snapshot copy ----

        [Fact]
        public void CopyIsIndependentOfTheOriginal()
        {
            var original = Trap();
            var copy = original.Clone();
            copy.Config["damage"] = 99;
            copy.Priority = 1;

            Assert.Equal(3, original.Config["damage"].AsInt32);
            Assert.Equal(5, original.Priority);
        }

        [Fact]
        public void AddedEntryIsACopyOfTheCallersEntry()
        {
            var room = new Room(null);
            var entry = Trap();
            room.AddBehavior(entry);
            entry.Config["damage"] = 99;

            Assert.Equal(3, room.Behaviors.Single().Config["damage"].AsInt32);
        }

        [Fact]
        public void CloningAnEntityCopiesTheList()
        {
            var room = new Room(null) { Id = "t.hall" };
            room.AddBehavior(Trap());

            var clone = room.Clone<Room>();
            clone.Behaviors[0].Config["damage"] = 99;

            Assert.Equal(3, room.Behaviors[0].Config["damage"].AsInt32);
            Assert.Equal("temple", clone.Behaviors[0].Module);
        }

        // ---- editing rules ----

        [Fact]
        public void SameHookModuleAndTypeIsRefusedNotReplaced()
        {
            var room = new Room(null);
            Assert.Null(room.AddBehavior(Trap()));

            var problem = room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "temple", "DesertTempleTrap", 9));

            Assert.Contains("already exists", problem);
            Assert.Equal(5, room.Behaviors.Single().Priority);
        }

        [Fact]
        public void DifferentHookOrModuleOrTypeIsAllowed()
        {
            var room = new Room(null);
            Assert.Null(room.AddBehavior(Trap()));
            Assert.Null(room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingExit, "temple", "DesertTempleTrap")));
            Assert.Null(room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "other", "DesertTempleTrap")));
            Assert.Null(room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingEnter, "temple", "Other")));
            Assert.Equal(4, room.Behaviors.Count);
        }

        [Fact]
        public void RemoveDeletesOnlyTheMatchingEntry()
        {
            var room = new Room(null);
            room.AddBehavior(Trap());
            room.AddBehavior(new BehaviorRef(BehaviorHooks.LivingExit, "temple", "DesertTempleTrap"));

            Assert.True(room.RemoveBehavior("living.enter", "temple", "DesertTempleTrap"));
            Assert.False(room.RemoveBehavior("living.enter", "temple", "DesertTempleTrap"));
            Assert.Equal("living.exit", room.Behaviors.Single().Hook);
        }

        [Theory]
        [InlineData("nonsense", "m", "T")]
        [InlineData("heartbeat", "", "T")]
        [InlineData("heartbeat", "m", "")]
        [InlineData("heartbeat", "has space", "T")]
        [InlineData(null, "m", "T")]
        public void InvalidEntriesAreRefused(string hook, string module, string type)
        {
            Assert.NotNull(new Room(null).AddBehavior(new BehaviorRef(hook, module, type)));
        }

        [Fact]
        public void AnyModuleNameIsAcceptedWithoutAuthorisation()
        {
            Assert.Null(new Room(null).AddBehavior(new BehaviorRef(BehaviorHooks.Heartbeat, "never-heard-of-it", "Anything")));
        }

        [Fact]
        public void AllSixDecidedHookNamesAreKnownAndNoOthers()
        {
            Assert.Equal(
                new[] { "heartbeat", "command.entered", "living.exit", "living.enter", "item.drop", "item.take" },
                BehaviorHooks.All.ToArray());
            Assert.False(BehaviorHooks.IsKnown("living.move"));
        }

        [Fact]
        public void OversizedConfigIsRefused()
        {
            var config = new BsonDocument("blob", new string('x', BehaviorRef.MaxConfigBytes));
            Assert.Contains("larger than", new Room(null).AddBehavior(new BehaviorRef(BehaviorHooks.Heartbeat, "m", "T", 0, config)));
        }

        // ---- importer and exporter ----

        private const string Area = @"
area:
  schema_version: 1
  prefix: t
  name: Test
rooms:
  - key: hall
    short: a hall
    long: A plain hall.
    behaviors:
      - hook: living.enter
        module: temple
        type: DesertTempleTrap
        priority: 5
        schema_version: 2
        config:
          damage: 3
          label: spikes
          ratio: 0.5
          armed: true
          tags: [a, b]
          quoted: ""7""
      - hook: heartbeat
        module: status
        type: Pulse
";

        private static string Replace(string text, string from, string to) { return text.Replace(from, to); }

        [Fact]
        public void ImportWritesTheBehaviorList()
        {
            var store = new FakeStore();

            var result = new AreaImporter(store).Import(Area);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            var entries = store.Docs["t.hall"]["behaviors"].AsBsonArray;
            Assert.Equal(2, entries.Count);
            var trap = entries[0].AsBsonDocument;
            Assert.Equal("living.enter", trap["hook"].AsString);
            Assert.Equal(5, trap["priority"].AsInt32);
            Assert.Equal(2, trap["schemaVersion"].AsInt32);
            var config = trap["config"].AsBsonDocument;
            Assert.Equal(3, config["damage"].AsInt32);
            Assert.Equal("spikes", config["label"].AsString);
            Assert.Equal(0.5, config["ratio"].AsDouble);
            Assert.True(config["armed"].AsBoolean);
            Assert.Equal(2, config["tags"].AsBsonArray.Count);
            Assert.Equal("7", config["quoted"].AsString);
            Assert.Equal(0, entries[1]["priority"].AsInt32);
            Assert.Equal(1, entries[1]["schemaVersion"].AsInt32);
        }

        [Fact]
        public void RoomWithoutBehaviorsImportsAnEmptyList()
        {
            var store = new FakeStore();
            var yaml = Area.Substring(0, Area.IndexOf("    behaviors:"));

            Assert.True(new AreaImporter(store).Import(yaml).Success);
            Assert.Empty(store.Docs["t.hall"]["behaviors"].AsBsonArray);
        }

        [Fact]
        public void ReImportOfTheSameFileChangesNothing()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);
            var before = store.UpsertCount;

            var again = importer.Import(Area);

            Assert.Equal(1, again.Count(ImportAction.Unchanged));
            Assert.Empty(again.SkippedBehaviors);
            Assert.Equal(before, store.UpsertCount);
        }

        [Fact]
        public void ImportSkipsAndReportsAnEntryThatAlreadyExistsOnTheStoredRoom()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);
            // A builder changed the stored entry's settings after the first import.
            store.Docs["t.hall"]["behaviors"][0]["priority"] = 9;

            var result = importer.Import(Area);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            Assert.Single(result.SkippedBehaviors);
            Assert.Contains("living.enter temple DesertTempleTrap", result.SkippedBehaviors[0]);
            Assert.Equal(9, store.Docs["t.hall"]["behaviors"][0]["priority"].AsInt32);
            Assert.Equal(2, store.Docs["t.hall"]["behaviors"].AsBsonArray.Count);
        }

        [Fact]
        public void ReplaceFlagOverwritesTheStoredEntryAndReportsIt()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);
            store.Docs["t.hall"]["behaviors"][0]["priority"] = 9;

            var result = importer.Import(Area, replaceBehaviors: true);

            Assert.Empty(result.SkippedBehaviors);
            Assert.Single(result.ReplacedBehaviors);
            Assert.Equal(5, store.Docs["t.hall"]["behaviors"][0]["priority"].AsInt32);
        }

        [Fact]
        public void ImportKeepsStoredEntriesThatAreNotInTheFileAndAppendsNewOnes()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);
            store.Docs["t.hall"]["behaviors"].AsBsonArray.Add(new BsonDocument
            {
                { "hook", "item.take" }, { "module", "added" }, { "type", "ByBuilder" }, { "priority", 0 }, { "config", new BsonDocument() }, { "schemaVersion", 1 }
            });
            var withNewEntry = Area + @"      - hook: item.drop
        module: temple
        type: Fresh
";

            var result = importer.Import(withNewEntry);

            Assert.True(result.Success, string.Join("; ", result.Errors));
            var types = store.Docs["t.hall"]["behaviors"].AsBsonArray.Select(b => b["type"].AsString).ToArray();
            Assert.Equal(new[] { "DesertTempleTrap", "Pulse", "ByBuilder", "Fresh" }, types);
        }

        [Fact]
        public void DryRunReportsSkippedEntriesAndWritesNothing()
        {
            var store = new FakeStore();
            var importer = new AreaImporter(store);
            importer.Import(Area);
            var before = store.UpsertCount;

            var result = importer.Import(Replace(Area, "priority: 5", "priority: 6"), dryRun: true);

            Assert.Single(result.SkippedBehaviors);
            Assert.Equal(before, store.UpsertCount);
        }

        [Fact]
        public void ParserRejectsBadBehaviorEntries()
        {
            Assert.Contains(new AreaImporter(new FakeStore()).Import(Replace(Area, "hook: living.enter", "hook: living.move")).Errors, e => e.Contains("unknown hook 'living.move'"));
            Assert.Contains(new AreaImporter(new FakeStore()).Import(Replace(Area, "        module: temple\n        type: DesertTempleTrap\n", "        type: DesertTempleTrap\n")).Errors, e => e.Contains("'module' is required"));
            Assert.Contains(new AreaImporter(new FakeStore()).Import(Replace(Area, "priority: 5", "priority: high")).Errors, e => e.Contains("'priority' must be an integer"));
            Assert.Contains(new AreaImporter(new FakeStore()).Import(Replace(Area, "priority: 5", "priority: 5\n        colour: red")).Errors, e => e.Contains("unknown key 'colour'"));
            Assert.Contains(new AreaImporter(new FakeStore()).Import(Replace(Area, "        config:\n", "        config: [1]\n        x-ignored:\n")).Errors, e => e.Contains("'config' must be a mapping"));
        }

        [Fact]
        public void ParserRejectsADuplicateEntryInOneList()
        {
            var duplicated = Area + @"      - hook: heartbeat
        module: status
        type: Pulse
";
            Assert.Contains(new AreaImporter(new FakeStore()).Import(duplicated).Errors, e => e.Contains("duplicate behavior (heartbeat status Pulse)"));
        }

        [Fact]
        public void FeaturesCannotCarryBehaviors()
        {
            var yaml = @"
area:
  schema_version: 1
  prefix: t
  name: Test
rooms:
  - key: hall
    short: a hall
    long: A plain hall.
    features:
      - short: a table
        long: A table.
        behaviors:
          - {hook: heartbeat, module: m, type: T}
";
            Assert.Contains(new AreaImporter(new FakeStore()).Import(yaml).Errors, e => e.Contains("unknown key 'behaviors'"));
        }

        [Fact]
        public void ExportThenImportRoundTripsTheBehaviorList()
        {
            var original = new FakeStore();
            Assert.True(new AreaImporter(original).Import(Area).Success);

            var exported = new AreaExporter(original).ExportArea("t");
            Assert.True(exported.Success, string.Join("; ", exported.Errors));
            Assert.Contains("behaviors:", exported.Yaml);

            var rebuilt = new FakeStore();
            Assert.True(new AreaImporter(rebuilt).Import(exported.Yaml).Success);
            Assert.Equal(original.Docs["t.hall"]["behaviors"].ToJson(), rebuilt.Docs["t.hall"]["behaviors"].ToJson());

            var before = original.UpsertCount;
            var reimport = new AreaImporter(original).Import(exported.Yaml);
            Assert.Equal(1, reimport.Count(ImportAction.Unchanged));
            Assert.Equal(before, original.UpsertCount);
        }

        [Fact]
        public void ExportWritesAnEmptyListForARoomWithoutBehaviors()
        {
            var store = new FakeStore();
            store.Docs["t.hall"] = new BsonDocument { { "_id", "t.hall" }, { "Area", "t" }, { "ShortDescription", "a hall" }, { "LongDescription", "A hall." } };

            var exported = new AreaExporter(store).ExportRoom("t.hall");

            Assert.True(exported.Success, string.Join("; ", exported.Errors));
            Assert.Contains("behaviors: []", exported.Yaml);
        }

        [Fact]
        public void ExportRefusesAStoredEntryItCannotWriteBack()
        {
            var store = new FakeStore();
            store.Docs["t.hall"] = new BsonDocument
            {
                { "_id", "t.hall" }, { "Area", "t" }, { "ShortDescription", "a hall" }, { "LongDescription", "A hall." },
                { "behaviors", new BsonArray { new BsonDocument { { "hook", "bogus" }, { "module", "m" }, { "type", "T" } } } }
            };

            var exported = new AreaExporter(store).ExportRoom("t.hall");

            Assert.False(exported.Success);
            Assert.Contains(exported.Errors, e => e.Contains("unknown hook 'bogus'"));
        }

        // ---- the creator command ----

        private class Setup
        {
            public BehaviorCommand Command = new BehaviorCommand();
            public FakeRoomRepo RoomRepo = new FakeRoomRepo();
            public Room Room;
            public RecordingPlayer Creator;

            public Setup(bool isCreator = true)
            {
                Room = new Room(RoomRepo) { Id = "t.hall", ShortDescription = "a hall", LongDescription = "A hall." };
                RoomRepo.Insert(Room);
                Creator = new RecordingPlayer("Ana");
                if (isCreator) { Creator.ExtendedProperties.Add(new CreatorProperty()); }
                Creator.CurrentLocation = Room;
                Creator.CurrentLocationId = Room.Id;
                Room.Occupants.Add(Creator);
            }

            public string Run(string input)
            {
                Creator.Messages.Clear();
                Command.DoCommand(Creator, Command.ParseCommand(Creator, input));
                return string.Join("\n", Creator.Messages);
            }
        }

        [Fact]
        public void CommandIsACreatorCommandWithKeywordBehavior()
        {
            var type = typeof(BehaviorCommand);
            Assert.True(typeof(ICommand).IsAssignableFrom(type) && !type.IsAbstract);
            Assert.NotNull(type.GetConstructor(System.Type.EmptyTypes));
            Assert.Equal(CommandCategories.Creator, new BehaviorCommand().CommandCategory);
            Assert.Equal(new[] { "behavior" }, new BehaviorCommand().CommandKeywords);
            Assert.Contains("behavior <target> add <hook> <module> <type> [json]", new BehaviorCommand().HelpText);
        }

        [Fact]
        public void NonCreatorIsTurnedAwayAndNothingChanges()
        {
            var s = new Setup(isCreator: false);

            var output = s.Run("behavior here add heartbeat m T");

            Assert.Contains("creator only", output);
            Assert.Empty(s.Room.Behaviors);
        }

        [Fact]
        public void AddThenListThenRemoveOnTheCurrentRoom()
        {
            var s = new Setup();

            Assert.Contains("Added living.enter temple DesertTempleTrap", s.Run("behavior here add living.enter temple DesertTempleTrap {\"damage\": 3, \"label\": \"two words\"}"));
            var entry = s.Room.Behaviors.Single();
            Assert.Equal(3, entry.Config["damage"].AsInt32);
            Assert.Equal("two words", entry.Config["label"].AsString);
            Assert.Equal(s.Room.Behaviors.Count, BsonSerializer.Deserialize<Room>(s.RoomRepo.Docs["t.hall"]).Behaviors.Count);

            var listing = s.Run("behavior here list");
            Assert.Contains("1 behavior entry", listing);
            Assert.Contains("living.enter temple DesertTempleTrap", listing);

            Assert.Contains("Removed living.enter temple DesertTempleTrap", s.Run("behavior here remove living.enter temple DesertTempleTrap"));
            Assert.Empty(s.Room.Behaviors);
            Assert.Empty(BsonSerializer.Deserialize<Room>(s.RoomRepo.Docs["t.hall"]).Behaviors);
            Assert.Contains("No behavior entries", s.Run("behavior here list"));
        }

        [Fact]
        public void AddRefusesDuplicatesBadHooksAndBadJson()
        {
            var s = new Setup();
            s.Run("behavior here add heartbeat m T");

            Assert.Contains("already exists", s.Run("behavior here add heartbeat m T"));
            Assert.Contains("unknown hook", s.Run("behavior here add living.move m T"));
            Assert.Contains("not a valid JSON object", s.Run("behavior here add heartbeat m U {nope"));
            Assert.Single(s.Room.Behaviors);
        }

        [Fact]
        public void RemoveOfAMissingEntryAndShortSyntaxGiveMessages()
        {
            var s = new Setup();

            Assert.Contains("No entry", s.Run("behavior here remove heartbeat m T"));
            Assert.Contains("Syntax", s.Run("behavior"));
            Assert.Contains("Syntax", s.Run("behavior here"));
            Assert.Contains("Syntax", s.Run("behavior here add heartbeat m"));
            Assert.Contains("Syntax", s.Run("behavior here remove heartbeat m"));
        }

        [Fact]
        public void CanTargetOneselfAnItemInTheRoomAndAnItemInInventory()
        {
            var s = new Setup();
            s.Room.Items.Add(new InventoryItem { ShortDescription = "rusty sword", Handles = new List<string> { "sword" }, Plurals = new List<string>() });
            s.Creator.Inventory.Add(new InventoryItem { ShortDescription = "lamp", Handles = new List<string> { "lamp" }, Plurals = new List<string>() });

            Assert.Contains("Added", s.Run("behavior me add heartbeat m T"));
            Assert.Contains("Added", s.Run("behavior sword add item.take m T"));
            Assert.Contains("Added", s.Run("behavior lamp add item.drop m T"));

            Assert.Single(s.Creator.Behaviors);
            Assert.Equal("item.take", s.Room.Items.Single().Behaviors.Single().Hook);
            Assert.Equal("item.drop", s.Creator.Inventory.Single().Behaviors.Single().Hook);
            // The room item was written through to the room document.
            Assert.Equal("item.take", s.RoomRepo.Docs["t.hall"]["Items"][0]["behaviors"][0]["hook"].AsString);
        }

        [Fact]
        public void FeaturesAndUnknownTargetsCannotBeBound()
        {
            var s = new Setup();
            s.Room.Features.Add(new Feature { ShortDescription = "a table", Handles = new List<string> { "table" }, Plurals = new List<string>() });

            Assert.Contains("cannot find", s.Run("behavior table add heartbeat m T"));
            Assert.Contains("cannot find", s.Run("behavior ghost add heartbeat m T"));
        }
    }
}
