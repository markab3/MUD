// using System.Linq;
// using MongoDB.Bson;
// using MongoDB.Bson.Serialization.Attributes;
// using MUD.Core.Interfaces;
// using MUD.Data;

namespace MUD.Core.Interfaces
{
    public interface ILiving
     {
        void ReceiveMessage(string message);
     }
 }