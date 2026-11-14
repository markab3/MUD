using System;
using MongoDB.Bson;
using MUD.Data;

namespace MUD.Core.Modules
{
    /// <summary>
    /// One stored module-state document (owner decisions D12, D73): the whole document a module keeps under one key, replaced as a
    /// whole on every write, last write wins. Stored in the module_state collection through the repository path; the _id is
    /// "&lt;module&gt;/&lt;key&gt;". Only core reads and writes it; a module reaches it through SetState and GetState.
    /// </summary>
    public class ModuleStateDocument : Entity
    {
        public const string CollectionName = "module_state";

        public string Module { get; set; }

        public string Key { get; set; }

        public BsonDocument Document { get; set; }

        public DateTime UpdatedUtc { get; set; }

        public static string IdFor(string module, string key)
        {
            return module + "/" + key;
        }
    }
}
