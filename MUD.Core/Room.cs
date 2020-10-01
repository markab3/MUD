using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;
using MUD.Core.Commands;
using MUD.Core.Formatting;
using MUD.Core.Interfaces;

namespace MUD.Core
{
    public class Room : IExaminable
    {
        [BsonId(IdGenerator = typeof(StringObjectIdGenerator))]
        [BsonRepresentation(BsonType.ObjectId)]
        public string _id { get; set; }

        public string ShortDescription { get; set; }

        public string LongDescription { get; set; }

        public Feature[] Features { get; set; }

        public Exit[] Exits { get; set; }

        [BsonIgnore]
        public CommandSource ExitCommandSource
        {
            get
            {
                if (_exitCommandSource == null && Exits != null && Exits.Length > 0)
                {
                    _exitCommandSource = new CommandSource();
                    foreach (Exit currentExit in Exits)
                    {
                        _exitCommandSource.AddCommand(new AnonymousCommand() {
                            CommandKeyword=currentExit.Name, 
                            ParseCommandHandler = ((string input) => 
                                { 
                                    return new object[] { currentExit };
                                }),
                            DoCommandHandler = ((Player commandIssuer, object[] commandArgs) => {
                                if (commandArgs[0] != null && commandArgs[0] is Room)
                                {
                                    commandIssuer.MoveToRoom((Room)commandArgs[0]);
                                }
                            })
                        });
                    }
                }
                return _exitCommandSource;

            }
        }

        private CommandSource _exitCommandSource = null;

        private List<Living> _occupants;

        public Room()
        {
            _occupants = new List<Living>();
        }

        public void TellRoom(string message)
        {
            foreach (var currentOccupant in _occupants)
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
            if (string.IsNullOrEmpty(exitMessage))
            {
                TellRoom(occupant.ShortDescription + " leaves.");
            }
            else
            {
                TellRoom(exitMessage);
            }
        }

        public string Examine()
        {
            string exitLine;
            if (Exits == null || Exits.Length == 0)
            {
                exitLine = "There are no obvious exits.";
            }
            else if (Exits.Length == 1)
            {
                exitLine = "There is one obvious exit: " + Exits[0].Name;
            }
            else
            {
                exitLine = string.Format("There are {0} obvious exits: {1}", Exits.Length.GetNumberText(), Exits.Select(e => e.Name).ToArray().GetListText());
            }
            exitLine = string.Format("%^{0}%^{1}%^{2}%^", EFormatOptions.Green, exitLine, EFormatOptions.Reset);
            return ShortDescription + " (" + _id + ")\r\n" + LongDescription + "\r\n" + exitLine + "\r\n";
        }
    }

}