using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Driver;
using MUD.Core.Administration;
using MUD.Core.Components;
using MUD.Core.GameObjects;
using MUD.Core.Generation;
using MUD.Core.Generation.Terrains;
using MUD.Core.Import;
using MUD.Core.Repositories.Interfaces;

namespace MUD.Core.Commands
{
    // Shared by 'regen' and 'update' (specs/TERRAIN_GENERATORS.md section 9). Runs on the game thread (commands do), so the
    // repository write that refreshes live rooms is on the right thread. Audit first: rows are written before the rooms.
    public abstract class GenerationCommandBase : ICommand
    {
        private readonly IRoomRepository _rooms;
        private readonly IAuditStore _audit;
        private readonly WorldSettings _settings;
        private readonly Func<ILibrary> _library;

        public abstract string[] CommandKeywords { get; }
        public CommandCategories CommandCategory { get => CommandCategories.Creator; }
        public abstract string HelpText { get; }
        protected abstract bool IsUpdate { get; }
        protected abstract string Verb { get; }

        protected GenerationCommandBase(IRoomRepository rooms, WorldSettings settings, IMongoDatabase database)
            : this(rooms, settings, new MongoAuditStore(database), MongoLibrary(database))
        {
        }

        // Test seam: the audit store and library source are supplied instead of built from a database.
        // Internal, so World's DI scan (which only considers public constructors) never sees it.
        internal GenerationCommandBase(IRoomRepository rooms, WorldSettings settings, IAuditStore audit, Func<ILibrary> library)
        {
            _rooms = rooms;
            _settings = settings;
            _audit = audit;
            _library = library;
        }

        private static Func<ILibrary> MongoLibrary(IMongoDatabase database)
        {
            StoreLibrary cache = null;
            return () =>
            {
                if (cache == null)
                {
                    var store = new MongoLibraryStore(database);
                    if (!store.GetVersion().HasValue) { return null; }
                    cache = new StoreLibrary(store);
                }
                cache.Refresh();
                return cache;
            };
        }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) };
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            if (!commandIssuer.Roles.Creator)
            {
                commandIssuer.ReceiveMessage("This is a creator only command. How did you get this?!");
                return;
            }

            var args = (commandArgs != null && commandArgs.Length > 0 ? commandArgs[0] as string : null) ?? "";
            var parts = args.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            bool dry = parts.Remove("dry");
            string seed = null;
            int seedAt = parts.IndexOf("seed");
            if (seedAt >= 0)
            {
                if (IsUpdate || seedAt + 1 >= parts.Count) { Syntax(commandIssuer); return; }
                seed = parts[seedAt + 1];
                parts.RemoveRange(seedAt, 2);
            }
            if (parts.Count == 0) { Syntax(commandIssuer); return; }

            string kind = "room", target;
            if (parts[0] == "map" || parts[0] == "area" || parts[0] == "room")
            {
                if (parts.Count != 2) { Syntax(commandIssuer); return; }
                kind = parts[0];
                target = parts[1];
            }
            else if (parts.Count == 1) { target = parts[0]; }
            else { Syntax(commandIssuer); return; }

            if (kind == "room" && target == "here")
            {
                var here = commandIssuer.CurrentLocation;
                if (here == null) { commandIssuer.ReceiveMessage("You are not in a room."); return; }
                target = here.Id;
            }

            var store = new RoomRepositoryImportStore(_rooms);
            var service = new GenerationService(store, _library, _settings.ContentDirectory, entries => BeginAudit(commandIssuer, entries));
            GenerationReport report;
            try
            {
                if (kind == "map" && !IsUpdate) { report = service.RegenerateMap(target, dry, seed); }
                else
                {
                    var keys = kind == "room" ? new List<string> { target }
                        : kind == "map" ? service.GeneratedKeysOfMap(target)
                        : service.GeneratedKeysOfArea(target);
                    if (keys.Count == 0 && kind != "room") { commandIssuer.ReceiveMessage("No generated rooms found for " + kind + " '" + target + "'."); return; }
                    report = IsUpdate ? service.UpdateRooms(keys, dry) : service.RegenerateRooms(keys, dry, seed);
                }
            }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                commandIssuer.ReceiveMessage(Verb + " failed (" + ex.GetType().Name + "). See the server log.");
                return;
            }

            foreach (var line in report.Describe(IsUpdate)) { commandIssuer.ReceiveMessage(line); }
        }

        private void Syntax(Living issuer)
        {
            issuer.ReceiveMessage(IsUpdate
                ? "Syntax: update <room key|here> | update map <map> | update area <prefix>   [dry]"
                : "Syntax: regen <room key|here> | regen map <map> | regen area <prefix>   [dry] [seed <value>]");
        }

        // Audit rows, one per room, written before the rooms. Null when they cannot be written.
        private Action<bool> BeginAudit(Living issuer, IReadOnlyList<GenAuditEntry> entries)
        {
            var now = DateTime.UtcNow;
            var role = issuer.Roles.Admin ? "Admin" : "Creator";
            var name = (issuer as Player) != null ? ((Player)issuer).PlayerName : issuer.ShortDescription;
            var rows = entries.Select(e => new AuditRecord
            {
                Id = ObjectId.GenerateNewId().ToString(), At = now, ActorId = issuer.Id, ActorName = name, ActorRole = role,
                Entity = "room", EntityId = e.Key, EntityName = e.Key, Field = e.Field, Old = BsonNull.Value, New = e.Summary, Source = "creator",
            }).ToList();
            try { _audit.Insert(rows); }
            catch (Exception ex) when (!(ex is OutOfMemoryException)) { return null; }
            return applied =>
            {
                try { _audit.SetOutcome(rows.Select(r => r.Id).ToList(), applied); }
                catch (Exception ex) when (!(ex is OutOfMemoryException)) { /* the row stays unapplied, which marks it for review */ }
            };
        }
    }

    public class RegenCommand : GenerationCommandBase
    {
        public RegenCommand(IRoomRepository rooms, WorldSettings settings, IMongoDatabase database) : base(rooms, settings, database) { }

        internal RegenCommand(IRoomRepository rooms, WorldSettings settings, IAuditStore audit, Func<ILibrary> library) : base(rooms, settings, audit, library) { }

        public override string[] CommandKeywords { get => new string[] { "regen" }; }

        protected override bool IsUpdate { get => false; }

        protected override string Verb { get => "regen"; }

        public override string HelpText { get => "Regenerate generated rooms from their map and the terrain library (MongoDB). A room is wiped and refilled: its description, ALL features (hand-added ones too) and properties are replaced by the generator's output, so hand edits are lost. Exits follow the map; items and occupants stay. The stored seed is used unless you give 'seed <value>'. Every change is audited. 'dry' reports without changing anything. 'regen map <map>' regenerates every room of the map (like 'genmap' then 'import').\r\n\r\nSyntax: regen <room key|here> | regen map <map> | regen area <prefix>   [dry] [seed <value>]"; }
    }

    public class UpdateCommand : GenerationCommandBase
    {
        public UpdateCommand(IRoomRepository rooms, WorldSettings settings, IMongoDatabase database) : base(rooms, settings, database) { }

        internal UpdateCommand(IRoomRepository rooms, WorldSettings settings, IAuditStore audit, Func<ILibrary> library) : base(rooms, settings, audit, library) { }

        public override string[] CommandKeywords { get => new string[] { "update" }; }

        protected override bool IsUpdate { get => true; }

        protected override string Verb { get => "update"; }

        public override string HelpText { get => "Refresh the library features of generated rooms. Only features that carry a library template reference are touched: when the template has a newer version, its text and words are rewritten; if the template was deleted it is reported and left as is. Features you added by hand, the room description, exits and properties are never changed. Reports how many features were updated, unchanged and had their template deleted. Every change is audited. 'dry' reports without changing anything.\r\n\r\nSyntax: update <room key|here> | update map <map> | update area <prefix>   [dry]"; }
    }
}
