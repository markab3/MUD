using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MUD.Core.Commands;
using MUD.Core.Formatting;
using MUD.Core.Properties;
using MUD.Core.Properties.Interfaces;
using MUD.Core.Properties.PlayerProperties;

namespace MUD.Core.GameObjects
{
    public class Living : GameObject
    {
        protected Room _currentLocation;

        protected World _world;

        public string Gender { get; set; }

        public string Race { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string CurrentLocationId { get; set; }

        public string Class { get; set; }

        public string[] KnownCommands { get; set; }

        public List<InventoryItem> Inventory { get; set; } = new List<InventoryItem>();

        public List<InventoryItem> WornItems { get; set; } = new List<InventoryItem>();

        public List<InventoryItem> HeldItems { get; set; } = new List<InventoryItem>();

        [BsonIgnore]
        public bool IsBusy { get; set; } = false;

        [BsonIgnore]
        public Room CurrentLocation
        {
            get
            {
                if (_currentLocation == null && !string.IsNullOrWhiteSpace(CurrentLocationId))
                {
                    _currentLocation = _world.GetRoom(CurrentLocationId);
                }
                return _currentLocation;
            }
            set { _currentLocation = value; } // Maybe make this private so we have to load via ID?
        }

        public Living(World world)
        {
            _world = world;
        }

        public virtual void ReceiveMessage(string message) { }

        public virtual void DoHeartbeat()
        {
            foreach (ExtendedProperty currentProperty in ExtendedProperties.Where(p => p.GetType().IsSubclassOf(typeof(IHeartbeat))))
            {
                ((IHeartbeat)currentProperty).DoHeartbeat();
            }
        }

        public override string Examine()
        {
            return string.Format("Here stands {0}, a prime example of {1} {2}.", ShortDescription, Race.GetArticle(), Race);
        }

        public GameObject[] MatchObjects(string input, bool includeInventory = true, bool includeRoomItems = true, bool includeRoomOccupants = true, bool includeRoomFeatures = true)
        {
            // Trim whitespace.
            input = input.Trim().ToLower();

            // support adding a number at the start - so "2 swords" would get the first 2 swords in the room.
            int? quantity = null;
            if (input.Contains(" "))
            {
                var quantityMatches = Regex.Match(input, "^(\\d+) (.*)$");
                if (quantityMatches.Success)
                {
                    quantity = int.Parse(quantityMatches.Groups[1].Value);
                    input = Regex.Replace(input, "^(\\d+) (.*)$", "$2");
                }
            }

            // Support adding a number at the end - so "sword 2" would get the second sword in the room.
            int? ordinalValue = null;
            if (input.Contains(" "))
            {
                var ordinalMatches = Regex.Match(input, "^(.*) (\\d+)$");
                if (ordinalMatches.Success)
                {
                    ordinalValue = int.Parse(ordinalMatches.Groups[2].Value);
                    input = Regex.Replace(input, "^(.*) (\\d+)$", "$1");
                }
            }

            List<GameObject> searchList = new List<GameObject>();
            if (includeInventory) { searchList.AddRange(this.Inventory ?? new List<InventoryItem>()); }// Check inventory of items in the living's inventory
            if (includeRoomItems) { searchList.AddRange(CurrentLocation.Items ?? new List<InventoryItem>()); }// Check inventory of items in the living's inventory
            if (includeRoomOccupants) { searchList.AddRange(CurrentLocation.Occupants ?? new List<Living>()); }// Check the living occupants of the current room
            if (includeRoomFeatures) { searchList.AddRange(CurrentLocation.Features ?? new List<Feature>()); }// Check the features of the current room

            // Return all (or quantity of) the things that match on plurals.
            GameObject[] matches = searchList.Where(g => g.IsPluralMatchForString(input)).ToArray();
            if (matches != null && matches.Length >= 1)
            {
                if (quantity != null && quantity.Value <= matches.Count())
                {
                    return matches.Take(quantity.Value).ToArray(); // Return first quantity matches
                }
                else
                {
                    return matches; // Return all plural matches
                }
            }

            // If nothing matches on plurals, return the first (or ordinal) item that matches otherwise.
            matches = searchList.Where(g => g.IsMatchForString(input)).ToArray();

            if (ordinalValue != null && ordinalValue < matches.Count())
            {
                return new GameObject[] { matches[ordinalValue.Value] }; // Return the ordinalValue-th match
            }

            return matches; // Return all singular matches
        }

        public void MoveToRoom(string roomIdToEnter)
        {
            var roomToEnter = _world.GetRoom(roomIdToEnter);
            if (CurrentLocation != null)
            {
                CurrentLocation.ExitRoom(this, string.Format("{0} leaves.", this.ShortDescription));
            }

            CurrentLocation = roomToEnter;
            CurrentLocationId = roomToEnter.Id;

            roomToEnter.EnterRoom(this);
            ReceiveMessage(roomToEnter.Examine(this));
        }

        /// <summary>
        /// Checks the player, their environment, their equipment, and the global default commands to find a command that matches the provided input.
        /// </summary>
        public ICommand ResolveCommand(string input)
        {
            ICommand matchedCommand = null;

            if (CurrentLocation != null)
            {
                matchedCommand = CurrentLocation.RoomCommandSource.GetCommandFromInput(input);
            }
            if (matchedCommand != null) { return matchedCommand; }

            matchedCommand = GetKnownCommandSource().GetCommandFromInput(input);
            if (matchedCommand != null) { return matchedCommand; }

            if (ExtendedProperties.Any(s => s.GetType() == typeof(AdminProperty)))
            {
                matchedCommand = _world.CreatorCommands.GetCommandFromInput(input);
                if (matchedCommand != null) { return matchedCommand; }
            }

            if (ExtendedProperties.Any(s => s.GetType() == typeof(CreatorProperty)))
            {
                matchedCommand = _world.CreatorCommands.GetCommandFromInput(input);
                if (matchedCommand != null) { return matchedCommand; }
            }

            matchedCommand = _world.DefaultCommands.GetCommandFromInput(input);
            if (matchedCommand != null) { return matchedCommand; }

            return null;
        }

        public CommandSource GetKnownCommandSource()
        {
            if (KnownCommands != null)
            {
                return _world.AllCommands.GetSubset(KnownCommands);
            }
            return new CommandSource();
        }

        public override bool IsMatchForString(string input)
        {
            return input == Gender?.ToLower()
                    || input == Race?.ToLower()
                    || input == Class?.ToLower()
                    || base.IsMatchForString(input);
        }
    }
}