using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MUD.Core.Commands;
using MUD.Core.Formatting;
using MUD.CoreApi;
using MUD.Core.Repositories.Interfaces;

namespace MUD.Core.GameObjects
{
    public class Room : GameObject, MUD.Core.Components.IBehaviorHolder
    {
        // Behavior entries (owner decisions D5, D53): stored as the embedded array "behaviors". Not dispatched yet.
        [BsonElement("behaviors")]
        public List<MUD.Core.Components.BehaviorRef> Behaviors
        {
            // Never null, including for a stored document that has no behaviors element.
            get { return _behaviors ?? (_behaviors = new List<MUD.Core.Components.BehaviorRef>()); }
            set { _behaviors = value; }
        }

        private List<MUD.Core.Components.BehaviorRef> _behaviors;

        // The search configuration and cooldown (core component "search", owner decisions D2, D13). Never null,
        // including for a stored document that has no search element. The cooldown is stored with the room.
        [BsonElement("search")]
        public MUD.Core.Components.Search Search
        {
            get { return _search ?? (_search = new MUD.Core.Components.Search()); }
            set { _search = value; }
        }

        private MUD.Core.Components.Search _search;

        // Area prefix this room belongs to (e.g. "castle"). Definition field.
        public string Area { get; set; }

        // Per-area local grid coordinates. Null means "unplaced" (distinct from the origin).
        public int? X { get; set; }

        public int? Y { get; set; }

        public int? Z { get; set; }

        // Revision of the stored definition (absent = 0). Written only by the website's write path, which increments it on
        // every web edit; the game adopts it when it applies a web change and never writes it back
        // (specs/WEB_EDITING.md sections 6 and 8).
        public int Rev { get; set; }

        public List<InventoryItem> Items { get; set; } = new List<InventoryItem>();

        public List<Feature> Features { get; set; } = new List<Feature>();

        public List<Exit> Exits { get; set; } = new List<Exit>();

        [BsonIgnore]
        public CommandSource RoomCommandSource
        {
            get
            {
                if (_roomCommandSource == null)
                {
                    RebuildExitCommands();
                }
                return _roomCommandSource;
            }
        }

        [BsonIgnore]
        public List<Living> Occupants { get { return _occupants; } }

        private CommandSource _roomCommandSource = null;

        private List<Living> _occupants = new List<Living>();

        private List<string[]> _exitAliases = new List<string[]>() {
            {new string[] {"northwest", "nw"}},
            {new string[] {"north", "n"}},
            {new string[] {"northeast", "ne"}},
            {new string[] {"west", "w"}},
            {new string[] {"east", "e"}},
            {new string[] {"southwest", "sw"}},
            {new string[] {"south", "s"}},
            {new string[] {"southeast", "se"}},
            {new string[] {"down", "d"}},
            {new string[] {"up", "u"}}
        };

        private IRoomRepository _roomRepository;

        public Room(IRoomRepository roomRepository)
        {
            _roomRepository = roomRepository;
        }

        public void TellRoom(string message, Living[] exclusionList = null, OutputChannel channel = OutputChannel.Room)
        {
            var recipients = _occupants.ToList();
            if (exclusionList != null)
            {
                recipients = recipients.Where(p => !exclusionList.Contains(p)).ToList();
            }
            foreach (var currentOccupant in recipients)
            {
                currentOccupant.ReceiveMessage(message, channel);
            }
        }

        public void EnterRoom(Living newOccupant, string entranceMessage = null)
        {
            if (string.IsNullOrEmpty(entranceMessage))
            {
                TellRoom(newOccupant.ShortDescription + " arrives.");
            }
            else
            {
                TellRoom(entranceMessage);
            }
            _occupants.Add(newOccupant);
        }

        public void ExitRoom(Living occupant, string exitMessage = null)
        {
            _occupants.Remove(occupant);
            if (!string.IsNullOrEmpty(exitMessage))
            {
                TellRoom(exitMessage);
            }
        }

        public void RebuildExitCommands()
        {
            _roomCommandSource = new CommandSource();
            string[] aliases;
            foreach (Exit currentExit in Exits)
            {
                var keywords = new string[] { currentExit.Name };
                aliases = _exitAliases.FirstOrDefault(s => s.Contains(currentExit.Name.ToLower()));
                if (aliases != null)
                {
                    keywords = aliases;
                }

                _roomCommandSource.AddCommand(new AnonymousCommand()
                {
                    CommandKeywords = keywords,
                    ParseCommandHandler = ((Living commandIssuer, string input) =>
                        {
                            return new object[] { currentExit };
                        }),
                    DoCommandHandler = ((Living commandIssuer, object[] commandArgs) =>
                    {
                        if (commandArgs[0] != null && commandArgs[0] is Exit)
                        {
                            commandIssuer.MoveToRoom(((Exit)commandArgs[0]).DestinationId);
                        }
                    })
                });
            }
        }

        // Copies authored definition fields (specs/ROOM_FILES.md section 5) from another copy of this room.
        // State (Items, occupants) is untouched.
        public void ApplyDefinition(Room source)
        {
            ShortDescription = source.ShortDescription;
            LongDescription = source.LongDescription;
            Handles = source.Handles;
            Adjectives = source.Adjectives;
            Plurals = source.Plurals;
            Components = source.Components;
            Search.ApplyDefinition(source.Search);
            Features = source.Features;
            Exits = source.Exits;
            Area = source.Area;
            X = source.X;
            Y = source.Y;
            Z = source.Z;
            RebuildExitCommands();
        }

        // Applies a website edit from the raw stored document: ONLY the web allow-list (Short, Long, Area, X, Y, Z, Exits)
        // plus Rev. Items, occupants, features, handles and every other field are left alone, so players and floor
        // items in the room are undisturbed (specs/WEB_EDITING.md 6.2). Game thread only. Does not announce anything.
        public void ApplyWebEdit(MongoDB.Bson.BsonDocument doc)
        {
            ShortDescription = doc.GetValue("ShortDescription", MongoDB.Bson.BsonNull.Value).IsString ? doc["ShortDescription"].AsString : ShortDescription;
            LongDescription = doc.GetValue("LongDescription", MongoDB.Bson.BsonNull.Value).IsString ? doc["LongDescription"].AsString : LongDescription;
            Area = doc.GetValue("Area", MongoDB.Bson.BsonNull.Value).IsString ? doc["Area"].AsString : null;
            X = OptionalInt(doc, "X");
            Y = OptionalInt(doc, "Y");
            Z = OptionalInt(doc, "Z");

            var exits = new List<Exit>();
            MongoDB.Bson.BsonValue stored;
            if (doc.TryGetValue("Exits", out stored) && stored.IsBsonArray)
            {
                foreach (var element in stored.AsBsonArray)
                {
                    if (!element.IsBsonDocument) { continue; }
                    var exit = element.AsBsonDocument;
                    var name = exit.GetValue("Name", MongoDB.Bson.BsonNull.Value);
                    var to = exit.GetValue("DestinationId", MongoDB.Bson.BsonNull.Value);
                    if (name.IsString && to.IsString) { exits.Add(new Exit { Name = name.AsString, DestinationId = to.AsString }); }
                }
            }
            Exits = exits;
            Rev = StoredRev(doc);
            RebuildExitCommands();
        }

        public static int StoredRev(MongoDB.Bson.BsonDocument doc)
        {
            var rev = doc.GetValue("Rev", MongoDB.Bson.BsonNull.Value);
            return rev.IsNumeric ? rev.ToInt32() : 0;
        }

        private static int? OptionalInt(MongoDB.Bson.BsonDocument doc, string field)
        {
            var value = doc.GetValue(field, MongoDB.Bson.BsonNull.Value);
            return value.IsNumeric ? value.ToInt32() : (int?)null;
        }

        // The persistence sink for rooms, or null when saves are written synchronously (startup, plain fakes in tests).
        private MUD.Data.IPersistSink Sink
        {
            get
            {
                var snapshots = _roomRepository as MUD.Data.ISnapshotRepository;
                return snapshots == null ? null : snapshots.Sink;
            }
        }

        // Snapshots this room and hands it to the persistence worker (specs/GAME_LOOP.md 8.2a). Game thread only.
        private void Post(MUD.Data.PersistKind kind, MongoDB.Bson.BsonDocument document, MongoDB.Bson.BsonDocument fallback)
        {
            Sink.Post(new MUD.Data.PersistRequest
            {
                Collection = ((MUD.Data.ISnapshotRepository)_roomRepository).CollectionName,
                Id = Id,
                Kind = kind,
                Doc = document,
                Fallback = fallback,
                Reason = kind == MUD.Data.PersistKind.Replace ? "room save" : "room state"
            });
        }

        // The stored document without the fields only the website's write path may change (Rev, and WebUpdatedAt which the
        // game does not model), so an in-game save can never roll the revision back.
        private MongoDB.Bson.BsonDocument WithoutWebBookkeeping(MongoDB.Bson.BsonDocument whole)
        {
            var set = new MongoDB.Bson.BsonDocument();
            foreach (var element in whole)
            {
                if (element.Name == "_id" || element.Name == "Rev" || element.Name == "WebUpdatedAt") { continue; }
                set[element.Name] = element.Value;
            }
            return set;
        }

        // With a persistence sink this only queues the write and returns true; otherwise it writes now.
        // An existing stored document gets a field-level $set that leaves Rev alone; the whole document is only
        // written when nothing is stored yet.
        public bool Save()
        {
            try
            {
                if (Sink != null && !string.IsNullOrWhiteSpace(Id))
                {
                    var whole = MongoDB.Bson.BsonExtensionMethods.ToBsonDocument(this);
                    Post(MUD.Data.PersistKind.SetFields, WithoutWebBookkeeping(whole), whole);
                    return true;
                }
                if (string.IsNullOrWhiteSpace(Id))
                {
                    _roomRepository.Insert(this);
                    return true;
                }
                else
                {
                    var whole = MongoDB.Bson.BsonExtensionMethods.ToBsonDocument(this);
                    if (_roomRepository.UpdateFields(Id, WithoutWebBookkeeping(whole)) || _roomRepository.Update(this))
                    {
                        return true;
                    }
                }
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine("Exception encountered while saving room: " + ex.Message);
            }
            return false;
        }

        // Persists the search component (its cooldown is runtime state, ROOM_FILES 7.4). Call after a reward is found.
        public bool SaveSearchState()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Id)) { return Save(); }

                var whole = MongoDB.Bson.BsonExtensionMethods.ToBsonDocument(this);
                var state = new MongoDB.Bson.BsonDocument("search", whole["search"]);
                if (Sink != null)
                {
                    Post(MUD.Data.PersistKind.SetFields, state, whole);
                    return true;
                }
                if (_roomRepository.UpdateFields(Id, state)) { return true; }
                return Save();
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine("Exception encountered while saving room search state: " + ex.Message);
            }
            return false;
        }

        // Write-through of runtime state (Items). Persists only the state fields of the stored document, so
        // authored definition fields are never rewritten from this in-memory copy. Falls back to a whole-document
        // Save() when the room has never been stored. Call after every mutation of Items.
        public bool SaveState()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Id)) { return Save(); }

                var whole = MongoDB.Bson.BsonExtensionMethods.ToBsonDocument(this);
                var state = new MongoDB.Bson.BsonDocument("Items", whole["Items"]);
                if (Sink != null)
                {
                    // The worker falls back to replacing the whole document if no stored document matches.
                    Post(MUD.Data.PersistKind.SetFields, state, whole);
                    return true;
                }
                if (_roomRepository.UpdateFields(Id, state)) { return true; }
                return Save();
            }
            catch (System.Exception ex)
            {
                System.Console.WriteLine("Exception encountered while saving room state: " + ex.Message);
            }
            return false;
        }

        // The long description followed by each feature's non-empty GetRoomLine(examiner), in Features order.
        // Pieces are separated by a single space so they read as one paragraph.
        private string GetLongDescriptionWithFeatures(Living examiner)
        {
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(LongDescription)) { parts.Add(LongDescription); }
            if (Features != null)
            {
                parts.AddRange(Features.Where(f => f != null).Select(f => f.GetRoomLine(examiner)).Where(l => !string.IsNullOrEmpty(l)));
            }
            return string.Join(" ", parts);
        }

        public override string Examine(Living examiner)
        {
            string exitLine;
            if (Exits == null || Exits.Count == 0)
            {
                exitLine = "There are no obvious exits.";
            }
            else if (Exits.Count == 1)
            {
                exitLine = "There is one obvious exit: " + Exits[0].Name;
            }
            else
            {
                exitLine = string.Format("There are {0} obvious exits: {1}", Exits.Count.GetExactNumberText(), Exits.Select(e => e.Name).GetListText());
            }
            exitLine = string.Format("%^{0}%^{1}%^{2}%^", EFormatOptions.Green, exitLine, EFormatOptions.Reset) + "\r\n";

            string occupantsLine = null;
            var otherOccupants = _occupants.ToList().Where(p => p != examiner).ToList();
            if (otherOccupants.Count == 1)
            {
                occupantsLine = otherOccupants.Select(o => o.ShortDescription).GetListText() + " is here.\r\n";
            }
            else if (otherOccupants.Count > 1)
            {
                occupantsLine = otherOccupants.Select(o => o.ShortDescription).GetListText() + " are here.\r\n";
            }
            if (!string.IsNullOrEmpty(occupantsLine))
            {
                occupantsLine = occupantsLine + "\r\n";
            }

            string itemsLine = null;
            if (Items.Count > 0)
            {
                itemsLine = Items.Select(i => i.ShortDescription).GetListText() + "\r\n";
            }
            if (!string.IsNullOrEmpty(itemsLine))
            {
                itemsLine = itemsLine + "\r\n";
            }

            return ShortDescription + " (" + Id + ")\r\n" + GetLongDescriptionWithFeatures(examiner) + "\r\n" + exitLine + occupantsLine + itemsLine;
        }
    }

}