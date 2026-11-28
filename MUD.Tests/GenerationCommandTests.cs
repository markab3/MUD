using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;
using MUD.Core;
using MUD.Core.Administration;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using MUD.Core.Generation;
using MUD.Core.Generation.Terrains;
using MUD.Core.Import;
using MUD.Core.Repositories.Interfaces;
using Scrutor;
using Xunit;

namespace MUD.Tests
{
    // Card t_1c0109d3: the creator commands 'regen' and 'update' (specs/TERRAIN_GENERATORS.md section 9) and their construction by
    // World's DI scan. The audit store, library and room repository are in memory: nothing here ran against MongoDB.
    public class GenerationCommandTests : IDisposable
    {
        private const string Map = "sample_land";
        private readonly string _dir;

        public GenerationCommandTests()
        {
            _dir = Path.Combine(Path.GetTempPath(), "gencmd-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(_dir, "maps"));
            File.WriteAllText(Path.Combine(_dir, "maps", Map + ".map.yaml"), File.ReadAllText(SampleMapPath()));
        }

        public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

        private static string SampleMapPath()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir != null)
            {
                var candidate = Path.Combine(dir.FullName, "MUD.Main", "Content", "maps", "sample_land.map.yaml");
                if (File.Exists(candidate)) { return candidate; }
                dir = dir.Parent;
            }
            throw new FileNotFoundException("sample_land.map.yaml not found above " + AppContext.BaseDirectory);
        }

        private static InMemoryLibrary Library()
        {
            var errors = new List<string>();
            var data = LibrarySeed.Parse(LibrarySeed.ReadEmbedded(), errors, new List<string>());
            Assert.Empty(errors);
            var lib = new InMemoryLibrary { Version = data.LibraryVersion };
            lib.FragmentList.AddRange(data.Fragments);
            lib.PhraseList.AddRange(data.Phrases);
            lib.FeatureList.Add(new FeatureTemplate
            {
                Id = "rock", Terrain = "sample_land.grass", Short = "a grey rock", Long = "A grey rock.",
                Handles = new List<string> { "rock" }, Weight = 1, Chance = 1.0, Version = 1,
            });
            return lib;
        }

        private class RecordingPlayer : Player
        {
            public List<string> Messages = new List<string>();

            public RecordingPlayer(World world) : base(world, null, new FakePlayerRepo())
            {
                PlayerName = "Tester";
                NameKey = "tester";
                ShortDescription = "Tester";
            }

            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        private class FakeAudit : IAuditStore
        {
            public List<AuditRecord> Rows = new List<AuditRecord>();
            public void Insert(IReadOnlyList<AuditRecord> rows) { Rows.AddRange(rows); }
            public void SetOutcome(IReadOnlyList<string> ids, bool applied) { }
            public AuditPage Query(string entity, string entityId, int page, int pageSize) { throw new NotSupportedException(); }
        }

        private class Fixture
        {
            public SyncRoomRepo Rooms = new SyncRoomRepo();
            public FakeAudit Audit = new FakeAudit();
            public Func<ILibrary> LibrarySource;
            public WorldSettings Settings;

            public Fixture(string contentDir, Func<ILibrary> library)
            {
                Settings = new WorldSettings { ContentDirectory = contentDir };
                LibrarySource = library;
            }

            public RegenCommand Regen() { return new RegenCommand(Rooms, Settings, Audit, LibrarySource); }
            public UpdateCommand Update() { return new UpdateCommand(Rooms, Settings, Audit, LibrarySource); }

            // A first import of the sample map into the repository (generated rooms with provenance).
            public void ImportMap(ILibrary library, string dir)
            {
                var generated = MapGenerator.Generate(File.ReadAllText(Path.Combine(dir, "maps", Map + ".map.yaml")), Map, library);
                Assert.True(generated.Success, string.Join("; ", generated.Errors));
                Assert.True(new AreaImporter(new RoomRepositoryImportStore(Rooms)).Import(generated.Yaml, new ImportOptions { ReplaceGenerated = true }).Success);
            }

            public string Snapshot()
            {
                return string.Join("|", Rooms.Stored.OrderBy(k => k.Key, StringComparer.Ordinal).Select(k => k.Key + "=" + k.Value.ToJson()));
            }
        }

        private static RecordingPlayer Player(bool creator, Room here = null)
        {
            // World's constructor registers global Mongo class maps and races with other tests, so it is skipped.
            var world = (World)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(World));
            world.Players = new List<Player>();
            var p = new RecordingPlayer(world);
            p.Roles.Creator = creator;
            if (here != null) { p.CurrentLocation = here; p.CurrentLocationId = here.Id; }
            return p;
        }

        private static void Run(ICommand command, Living issuer, string input)
        {
            command.DoCommand(issuer, command.ParseCommand(issuer, input));
        }

        private const string RegenSyntax = "Syntax: regen <room key|here> | regen map <map> | regen area <prefix>   [dry] [seed <value>]";
        private const string UpdateSyntax = "Syntax: update <room key|here> | update map <map> | update area <prefix>   [dry]";

        // ---------------------------------------------------------------- role

        [Theory]
        [InlineData("regen", "regen map sample_land")]
        [InlineData("update", "update map sample_land")]
        public void ANonCreatorIsRefusedAndNothingIsRead(string verb, string input)
        {
            var lib = Library();
            var f = new Fixture(_dir, () => { throw new InvalidOperationException("library must not be opened"); });
            f.ImportMap(lib, _dir);
            var before = f.Snapshot();
            var player = Player(creator: false);

            Run(verb == "regen" ? (ICommand)f.Regen() : f.Update(), player, input);

            Assert.Equal(new[] { "This is a creator only command. How did you get this?!" }, player.Messages);
            Assert.Equal(before, f.Snapshot());
            Assert.Empty(f.Audit.Rows);
        }

        // ---------------------------------------------------------------- syntax

        [Theory]
        [InlineData("")]                      // no arguments
        [InlineData("dry")]                   // only a flag
        [InlineData("planet sample_land")]    // bad kind: two words, first is not map/area/room
        [InlineData("map")]                   // kind without a target
        [InlineData("map a b")]               // too many
        [InlineData("seed")]                  // seed without a value
        [InlineData("map sample_land seed")]  // missing seed value
        public void RegenSyntaxErrorsShowTheSyntaxAndChangeNothing(string args)
        {
            var f = new Fixture(_dir, () => { throw new InvalidOperationException("library must not be opened"); });
            f.ImportMap(Library(), _dir);
            var before = f.Snapshot();
            var player = Player(creator: true);

            Run(f.Regen(), player, ("regen " + args).TrimEnd());

            Assert.Equal(new[] { RegenSyntax }, player.Messages);
            Assert.Equal(before, f.Snapshot());
            Assert.Empty(f.Audit.Rows);
        }

        [Theory]
        [InlineData("")]
        [InlineData("planet sample_land")]
        [InlineData("map")]
        [InlineData("seed abc")]                      // seed is a regen-only option
        [InlineData("map sample_land seed abc")]
        [InlineData("seed")]
        public void UpdateSyntaxErrorsShowTheSyntaxAndChangeNothing(string args)
        {
            var f = new Fixture(_dir, () => { throw new InvalidOperationException("library must not be opened"); });
            f.ImportMap(Library(), _dir);
            var before = f.Snapshot();
            var player = Player(creator: true);

            Run(f.Update(), player, ("update " + args).TrimEnd());

            Assert.Equal(new[] { UpdateSyntax }, player.Messages);
            Assert.Equal(before, f.Snapshot());
            Assert.Empty(f.Audit.Rows);
        }

        // ---------------------------------------------------------------- here

        [Theory]
        [InlineData("regen")]
        [InlineData("update")]
        public void HereWithNoLocationSaysSo(string verb)
        {
            var f = new Fixture(_dir, () => { throw new InvalidOperationException("library must not be opened"); });
            var player = Player(creator: true, here: null);

            Run(verb == "regen" ? (ICommand)f.Regen() : f.Update(), player, verb + " here");

            Assert.Equal(new[] { "You are not in a room." }, player.Messages);
            Assert.Empty(f.Audit.Rows);
        }

        [Fact]
        public void HereRegeneratesTheRoomThePlayerIsIn()
        {
            var lib = Library();
            var f = new Fixture(_dir, () => lib);
            f.ImportMap(lib, _dir);
            var key = f.Rooms.Stored.Values.First(d => d["gen"]["terrain"].AsString == "grass")["_id"].AsString;
            f.Rooms.Stored[key]["LongDescription"] = "Hand written.";
            var player = Player(creator: true, here: new Room(new FakeRoomRepo()) { Id = key });

            Run(f.Regen(), player, "regen here");

            Assert.NotEqual("Hand written.", f.Rooms.Stored[key]["LongDescription"].AsString);
            Assert.Contains(player.Messages, m => m.StartsWith(key + ": "));
            Assert.Equal(1, f.Audit.Rows.Count(r => r.EntityId == key));
        }

        // ---------------------------------------------------------------- dry

        [Theory]
        [InlineData("regen", "regen map sample_land dry")]
        [InlineData("regen", "regen dry area sample_land")]
        [InlineData("update", "update map sample_land dry")]
        public void DryReportsAndChangesNothing(string verb, string input)
        {
            var lib = Library();
            var f = new Fixture(_dir, () => lib);
            f.ImportMap(lib, _dir);
            foreach (var doc in f.Rooms.Stored.Values) { doc["LongDescription"] = "Hand written."; }
            var before = f.Snapshot();
            var upserts = f.Rooms.Upserts;
            var player = Player(creator: true);

            Run(verb == "regen" ? (ICommand)f.Regen() : f.Update(), player, input);

            Assert.Equal(before, f.Snapshot());
            Assert.Equal(upserts, f.Rooms.Upserts);
            Assert.Empty(f.Audit.Rows);
            Assert.NotEmpty(player.Messages);
            Assert.Contains(player.Messages, m => m.StartsWith("Dry run: "));
        }

        // ---------------------------------------------------------------- unknown targets

        [Theory]
        [InlineData("regen", "regen map nosuchmap", "map", "nosuchmap")]
        [InlineData("regen", "regen area nosuchprefix", "area", "nosuchprefix")]
        [InlineData("update", "update map nosuchmap", "map", "nosuchmap")]
        [InlineData("update", "update area nosuchprefix", "area", "nosuchprefix")]
        public void AnUnknownMapOrAreaReportsNoGeneratedRooms(string verb, string input, string kind, string target)
        {
            var lib = Library();
            var f = new Fixture(_dir, () => lib);
            f.ImportMap(lib, _dir);
            var before = f.Snapshot();
            var player = Player(creator: true);

            Run(verb == "regen" ? (ICommand)f.Regen() : f.Update(), player, input);

            // 'regen map' goes straight to the service, which reports a missing map file instead; 'update map', 'area' use the count check.
            if (verb == "regen" && kind == "map")
            {
                Assert.Contains(player.Messages, m => m.Contains("No such map: " + target));
            }
            else
            {
                Assert.Equal(new[] { "No generated rooms found for " + kind + " '" + target + "'." }, player.Messages);
            }
            Assert.Equal(before, f.Snapshot());
            Assert.Empty(f.Audit.Rows);
        }

        // ---------------------------------------------------------------- failure

        // The command's own catch: an exception that escapes the service (here the room store failing) is reported by type only.
        private class ThrowingRoomRepo : SyncRoomRepo, IRoomRepository
        {
            BsonDocument MUD.Data.IRepository<Room>.GetRawDocument(string id) { throw new InvalidOperationException("secret connection detail"); }
        }

        [Theory]
        [InlineData("regen", "regen map sample_land", "regen failed (InvalidOperationException). See the server log.")]
        [InlineData("regen", "regen sample_land.grass", "regen failed (InvalidOperationException). See the server log.")]
        [InlineData("update", "update sample_land.grass", "update failed (InvalidOperationException). See the server log.")]
        public void AnExceptionFromTheStoreReportsAFailureWithoutTheDetail(string verb, string input, string expected)
        {
            var lib = Library();
            var f = new Fixture(_dir, () => lib);
            f.Rooms = new ThrowingRoomRepo();
            var player = Player(creator: true);

            Run(verb == "regen" ? (ICommand)f.Regen() : f.Update(), player, input);

            Assert.Equal(new[] { expected }, player.Messages);
            Assert.DoesNotContain(player.Messages, m => m.Contains("secret"));
            Assert.Empty(f.Audit.Rows);
        }

        // The service handles a library that cannot be read itself: an error line, nothing changed, no detail leaked.
        [Theory]
        [InlineData("regen", "regen map sample_land")]
        [InlineData("update", "update area sample_land")]
        public void AnExceptionFromTheLibraryIsReportedAsAnErrorAndNothingChanges(string verb, string input)
        {
            var lib = Library();
            var good = new Fixture(_dir, () => lib);
            good.ImportMap(lib, _dir);
            var f = new Fixture(_dir, () => { throw new InvalidOperationException("secret connection detail"); });
            f.Rooms = good.Rooms;
            var before = f.Snapshot();
            var player = Player(creator: true);

            Run(verb == "regen" ? (ICommand)f.Regen() : f.Update(), player, input);

            Assert.Contains(player.Messages, m => m.StartsWith("error: The terrain library could not be read (InvalidOperationException)"));
            Assert.DoesNotContain(player.Messages, m => m.Contains("secret"));
            Assert.Equal(before, f.Snapshot());
            Assert.Empty(f.Audit.Rows);
        }

        // ---------------------------------------------------------------- metadata

        [Fact]
        public void TheCommandsAreCreatorCommandsWithTheirKeywords()
        {
            var f = new Fixture(_dir, () => null);
            Assert.Equal(CommandCategories.Creator, f.Regen().CommandCategory);
            Assert.Equal(CommandCategories.Creator, f.Update().CommandCategory);
            Assert.Equal(new[] { "regen" }, f.Regen().CommandKeywords);
            Assert.Equal(new[] { "update" }, f.Update().CommandKeywords);
        }

        // ---------------------------------------------------------------- DI construction

        // Mirrors World.LoadTypes: ICommand implementations are scanned from the library assemblies as singletons, and the host
        // registers IRoomRepository, WorldSettings and IMongoDatabase. World itself is not started (its constructor registers
        // global Mongo class maps); MongoClient does not connect until used, so no database is needed to build the database object.
        [Fact]
        public void WorldsCommandScanCanConstructRegenAndUpdateWithTheHostRegistrations()
        {
            IMongoDatabase database = new MongoClient("mongodb://localhost:1/?connectTimeoutMS=1").GetDatabase("unused");
            var services = new ServiceCollection();
            services.Scan(scan => scan
                .FromAssemblies(typeof(RegenCommand).Assembly)
                .AddClasses(classes => classes.AssignableTo<ICommand>().Where(t => t.Name != "AnonymousCommand"))
                .AsImplementedInterfaces()
                .WithSingletonLifetime());
            services.AddSingleton<WorldSettings>(new WorldSettings { ContentDirectory = _dir });
            services.AddSingleton<IMongoDatabase>(database);
            services.AddSingleton<IRoomRepository>(new SyncRoomRepo());
            var provider = services.BuildServiceProvider();

            foreach (var type in new[] { typeof(RegenCommand), typeof(UpdateCommand) })
            {
                var descriptor = Assert.Single(services, d => d.ServiceType == typeof(ICommand) && d.ImplementationType == type);
                Assert.Equal(ServiceLifetime.Singleton, descriptor.Lifetime);
                // The container picks the public constructor and fills it from the registrations above.
                var instance = ActivatorUtilities.CreateInstance(provider, type);
                Assert.IsType(type, instance);
                Assert.Equal(CommandCategories.Creator, ((ICommand)instance).CommandCategory);
            }
        }
    }
}
