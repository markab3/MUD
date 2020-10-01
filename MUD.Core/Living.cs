using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MUD.Core.Interfaces;

namespace MUD.Core
{
    public abstract class Living : IUpdatable, IExaminable
    {
        public string ShortDescription { get; set; }

        public string LongDescription { get; set; }

        public string Gender { get; set; }

        public string Race { get; set; }

        [BsonRepresentation(BsonType.ObjectId)]
        public string CurrentLocation_id { get; set; }

        [BsonIgnore]
        public Room CurrentLocation
        {
            get
            {
                if (_currentLocation == null && !string.IsNullOrWhiteSpace(CurrentLocation_id))
                {
                    _currentLocation = World.Instance.Rooms.FirstOrDefault(r => r._id == CurrentLocation_id);
                }
                return _currentLocation;
            }
            set { _currentLocation = value; } // Maybe make this private so we have to load via ID?
        }

        private Room _currentLocation;

        public string Examine()
        {
            throw new System.NotImplementedException();
        }

        public void Update()
        {
            throw new System.NotImplementedException();
        }

        public void MoveToRoom(Room roomToEnter)
        {
            if (CurrentLocation != null)
            {
                CurrentLocation.ExitRoom(this);
            }

            CurrentLocation = roomToEnter;
            CurrentLocation_id = roomToEnter._id;

            roomToEnter.EnterRoom(this);
            ReceiveMessage(roomToEnter.Examine());
        }

        public abstract void ReceiveMessage(string message);
    }
}