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
            var recipients = _occupants;
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

        public string Examine(Living examiner)
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
            exitLine = string.Format("%^{0}%^{1}%^{2}%^", EFormatOptions.Green, exitLine, EFormatOptions.Reset) + "/r/n";

            string occupantsLine = null;
            var otherOccupants = _occupants.Where(p => p != examiner).ToList();
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

            return ShortDescription + " (" + Id + ")\r\n" + LongDescription + "\r\n" + exitLine + occupantsLine + itemsLine;
        }
    }

}