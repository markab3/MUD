using System.Collections.Generic;
using System.Linq;
using MUD.Core.GameObjects;
using MUD.Core.Components;
using MUD.Core.Repositories.Interfaces;

namespace MUD.Core.Commands
{
    public class BuildCommand : ICommand
    {
        private IRoomRepository _roomRepository;

        public string[] CommandKeywords { get => new string[] { "build" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Creator; }

        public string HelpText { get => "Build a room. 'build feature list|add|set|remove' edits the features of the room you are in."; }

        private WorldSettings _settings;

        public BuildCommand(IRoomRepository roomRepository, WorldSettings settings)
        {
            _roomRepository = roomRepository;
            _settings = settings;
        }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) }; ;
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            Roles creatorStatusObj = commandIssuer.Roles.Creator ? commandIssuer.Roles : null;
            if (creatorStatusObj == null)
            {
                commandIssuer.ReceiveMessage("This is a creator only command. How did you get this?!");
                return;
            }

            string subArgs = commandArgs != null && commandArgs.Length > 0 ? (commandArgs[0] as string ?? "").Trim() : "";
            if (subArgs == "feature" || subArgs.StartsWith("feature "))
            {
                BuildFeatureEditor.Execute(commandIssuer, subArgs.Substring("feature".Length));
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
            newRoom.Search.Entries.Add(new MUD.Core.Components.SearchEntry() {
                EntryId = "debris_search",
                Target = "debris",
                Reward = new InventoryItem() { ShortDescription = "emerald", LongDescription = "A small, green emerald." },
                RefreshMilliseconds = 10000
            });
            newRoom.Exits = new List<Exit>() {
                new Exit() { Name = "out", DestinationId= _settings.StartRoomKey }
            };
            newRoom.Save();
            commandIssuer.ReceiveMessage("Room built! Id: " + newRoom.Id);
        }
    }
}