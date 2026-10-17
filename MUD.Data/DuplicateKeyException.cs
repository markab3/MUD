using System;
using MongoDB.Driver;

namespace MUD.Data
{
    // Raised by Repository when a write violates a unique index. Callers treat it as "that key is already taken".
    public class DuplicateKeyException : Exception
    {
        public DuplicateKeyException() : base("A document with the same unique key already exists.") { }

        public DuplicateKeyException(Exception inner) : base("A document with the same unique key already exists.", inner) { }

        public static bool IsDuplicateKey(Exception ex)
        {
            if (ex is DuplicateKeyException) { return true; }
            var writeEx = ex as MongoWriteException;
            if (writeEx != null) { return writeEx.WriteError != null && writeEx.WriteError.Category == ServerErrorCategory.DuplicateKey; }
            var commandEx = ex as MongoCommandException;
            return commandEx != null && commandEx.Code == 11000;
        }
    }
}
