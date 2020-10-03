using System.Collections.Generic;
using MUD.Data;

namespace MUD.Core.Entities
{
    public class RoomEntity : Entity
    {
        public string ShortDescription { get; set; }

        public string LongDescription { get; set; }

        public List<Feature> Features { get; set; } = new List<Feature>();

        public List<Exit> Exits { get; set; } = new List<Exit>();
    }
}