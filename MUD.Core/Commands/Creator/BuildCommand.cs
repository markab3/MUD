using System.Collections.Generic;
using System.Linq;
using MUD.Core.GameObjects;
using MUD.Core.Properties.PlayerProperties;
using MUD.Core.Properties.RoomProperties;
using MUD.Core.Repositories.Interfaces;

namespace MUD.Core.Commands
{
    public class BuildCommand : ICommand
    {
        private IRoomRepository _roomRepository;

        public string[] CommandKeywords { get => new string[] { "build" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Creator; }

        public string HelpText { get => "Build a room."; }

        public BuildCommand(IRoomRepository roomRepository)
        {
            _roomRepository = roomRepository;
        }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) }; ;
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            CreatorProperty creatorStatusObj = (CreatorProperty)commandIssuer.ExtendedProperties.FirstOrDefault(s => s.GetType() == typeof(CreatorProperty));
            if (creatorStatusObj == null)
            {
                commandIssuer.ReceiveMessage("This is a creator only command. How did you get this?!");
                return;
            }

            Room newRoom = new Room(_roomRepository);
            newRoom.ShortDescription = "A small, abandoned shop";
            newRoom.LongDescription = "This is a small shop off of the main square. It has been abandoned for quite some time and debris litters the floor.";
            newRoom.Features = new List<Feature>() {
                new Feature() {
                    Handles = new List<string>() { "debris" },
                    ShortDescription = "debris",
                    LongDescription = "Debris covers the floor. It is presumably what remains of the shop's inventory, though it appears everything that is left is broken or worthless."
                 }
            };
            newRoom.ExtendedProperties = new List<Properties.ExtendedProperty>() {
                new BasicSearchProperty() {
                    SearchTarget = "debris",
                    Reward = new InventoryItem() { ShortDescription = "emerald", LongDescription = "A small, green emerald." },
                    RefreshInterval = 10000
                }
            };
            newRoom.Exits = new List<Exit>() {
                new Exit() { Name = "out", DestinationId= "5f6e27f20c1fdd24b4b18b1c" }
            };
            newRoom.Save();
            commandIssuer.ReceiveMessage("Room built! Id: " + newRoom.Id);
        }
    }
}