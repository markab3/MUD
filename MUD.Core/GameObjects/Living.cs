using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MUD.Core.Commands;
using MUD.Core.Formatting;
using MUD.Core.Properties;

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

        public List<InventoryItem> Inventory { get; set; }

        public List<InventoryItem> WornItems { get; set; }

        public List<InventoryItem> HeldItems { get; set; }

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

        public GameObject MatchItems(string input)
        {

            return null;
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
    }
}