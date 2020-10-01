using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Bson.Serialization.IdGenerators;
using MongoDB.Driver;
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

        public List<Exit> Exits { get; set; } = new List<Exit>();

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

        private List<Living> _occupants;

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
                            commandIssuer.MoveToRoom(((Exit)commandArgs[0]).Destination);
                        }
                    })
                });
            }
        }

        public bool Save()
        {
            MongoClient dbClient = new MongoClient("mongodb+srv://testuser:qVvizXD1jrUaRdz4@cluster0.9titb.gcp.mongodb.net/test");
            var database = dbClient.GetDatabase("testmud");
            var collection = database.GetCollection<Room>("room");
            //var filter = Builders<Player>.Filter.Eq(p => p._id == _id);
            var result = collection.ReplaceOne<Room>((p => p._id == _id), this);
            if (result != null && result.IsModifiedCountAvailable && result.ModifiedCount == 1)
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