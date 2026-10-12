using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MUD.Core.Commands;
using MUD.Core.Formatting;
using MUD.Core.Repositories.Interfaces;

namespace MUD.Core.GameObjects
{
    public class Room : GameObject
    {
        // Area prefix this room belongs to (e.g. "castle"). Definition field.
        public string Area { get; set; }

        // Per-area local grid coordinates. Null means "unplaced" (distinct from the origin).
        public int? X { get; set; }

        public int? Y { get; set; }

        public int? Z { get; set; }

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

        public void TellRoom(string message, Living[] exclusionList = null)
        {
            var recipients = _occupants.ToList();
            if (exclusionList != null)
            {
                recipients = recipients.Where(p => !exclusionList.Contains(p)).ToList();
            }
            foreach (var currentOccupant in recipients)
            {
                currentOccupant.ReceiveMessage(message);
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
            ExtendedProperties = source.ExtendedProperties;
            Features = source.Features;
            Exits = source.Exits;
            Area = source.Area;
            X = source.X;
            Y = source.Y;
            Z = source.Z;
            RebuildExitCommands();
        }

        public bool Save()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Id))
                {
                    _roomRepository.Insert(this);
                    return true;
                }
                else
                {
                    if (_roomRepository.Update(this))
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

        // Write-through of runtime state (Items). Persists only the state fields of the stored document, so
        // authored definition fields are never rewritten from this in-memory copy. Falls back to a whole-document
        // Save() when the room has never been stored. Call after every mutation of Items.
        public bool SaveState()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(Id)) { return Save(); }

                var state = new MongoDB.Bson.BsonDocument("Items", MongoDB.Bson.BsonExtensionMethods.ToBsonDocument(this)["Items"]);
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