using System;
using System.Collections.Generic;

namespace MUD.Core.Events
{
    // What a Before chain does with a behavior entry that cannot answer: its module is down or unresolved, it threw, or it
    // missed the chain deadline (owner decisions D10, D15, D60).
    //
    // All six hooks fail OPEN by default: the miss counts as Allow with no events (for command.entered, as "not handled").
    // The core policy file may mark one (Module, Type) as fail closed: its miss counts as a Deny with a core message.
    // Nothing is closed by default. Reading the policy file is not built yet; the set is filled in code (or by a test).
    public class FailPolicy
    {
        public const string ClosedMessage = "Something prevents you from doing that right now.";

        private readonly HashSet<string> _closed = new HashSet<string>(StringComparer.Ordinal);

        public void MarkClosed(string module, string type)
        {
            _closed.Add(Key(module, type));
        }

        public void MarkOpen(string module, string type)
        {
            _closed.Remove(Key(module, type));
        }

        public bool IsClosed(string module, string type)
        {
            return _closed.Count > 0 && _closed.Contains(Key(module, type));
        }

        private static string Key(string module, string type)
        {
            return module + "\n" + type;
        }
    }
}
