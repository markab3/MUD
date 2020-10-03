using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson.Serialization.Attributes;
using MUD.Core.Commands;
using MUD.Core.Entities;
using MUD.Core.Formatting;
using MUD.Core.Repositories.Interfaces;

namespace MUD.Core
{
    public class Room: RoomEntity
    {

        [BsonIgnore]
        public CommandSource ExitCommandSource
        {
            get
            {
                if (_exitCommandSource == null)
                {
                    RebuildExitCommands();
                }
                return _exitCommandSource;
            }
        }

        private CommandSource _exitCommandSource = null;

        private List<Player> _occupants = new List<Player>();

        private Dictionary<string, string[]> _exitAliases = new Dictionary<string, string[]>() {
            {"northwest", new string[] {"nw"}},
            {"north", new string[] {"n"}},
            {"northeast", new string[] {"ne"}},
            {"west", new string[] {"w"}},
            {"east", new string[] {"e"}},
            {"southwest", new string[] {"sw"}},
            {"south", new string[] {"s"}},
            {"southeast", new string[] {"se"}},
            {"down", new string[] {"d"}},
            {"up", new string[] {"u"}}
        };

        private IRoomRepository _roomRepository;

        public Room(IRoomRepository roomRepository, RoomEntity entity) {
            _roomRepository = roomRepository;
            entity.Map(this);
        }

        public void TellRoom(string message)
        {
            foreach (var currentOccupant in _occupants)
            {
                currentOccupant.ReceiveMessage(message);
            }
        }

        public void EnterRoom(Player newOccupant, string entranceMessage = null)
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

        public void ExitRoom(Player occupant, string exitMessage = null)
        {
            _occupants.Remove(occupant);
            if (string.IsNullOrEmpty(exitMessage))
            {
                TellRoom(occupant.ShortDescription + " leaves.");
            }
            else
            {
                TellRoom(exitMessage);
            }
        }

        public void RebuildExitCommands()
        {
            _exitCommandSource = new CommandSource();
            string[] aliases;
            foreach (Exit currentExit in Exits)
            {
                aliases = null;
                if (_exitAliases[currentExit.Name.ToLower()] != null) { aliases = _exitAliases[currentExit.Name.ToLower()]; }
                _exitCommandSource.AddCommand(new AnonymousCommand()
                {
                    CommandKeyword = currentExit.Name,
                    CommandAliases = aliases,
                    ParseCommandHandler = ((string input) =>
                        {
                            return new object[] { currentExit };
                        }),
                    DoCommandHandler = ((Player commandIssuer, object[] commandArgs) =>
                    {
                        if (commandArgs[0] != null && commandArgs[0] is Exit)
                        {
                            commandIssuer.MoveToRoom(((Exit)commandArgs[0]).Destination_id);
                        }
                    })
                });
            }
        }

        public bool Save()
        {
            if (_roomRepository.Update(this))
            {
                return true;
            }
            return false;
        }

        public string Examine()
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
                exitLine = string.Format("There are {0} obvious exits: {1}", Exits.Count.GetExactNumberText(), Exits.Select(e => e.Name).ToArray().GetListText());
            }
            exitLine = string.Format("%^{0}%^{1}%^{2}%^", EFormatOptions.Green, exitLine, EFormatOptions.Reset);
            return ShortDescription + " (" + _id + ")\r\n" + LongDescription + "\r\n" + exitLine + "\r\n";
        }
    }

}