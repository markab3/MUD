using System;
using System.Collections.Concurrent;

namespace MUD.Core.Components
{
    // Knows which component names are core components (with their C# type) and treats every other
    // "<module>.<name>" as opaque BSON that core preserves on load and save, so data of a module that is not
    // loaded is never lost (owner decisions D10, D13). Core components are registered by the card that
    // introduces them; this card registers none. Core does not validate module components against a schema in
    // version 1 (D50).
    public class ComponentRegistry
    {
        private readonly ConcurrentDictionary<string, Type> _coreTypes = new ConcurrentDictionary<string, Type>(StringComparer.Ordinal);

        public void RegisterCore(string name, Type componentType)
        {
            ComponentName.RequireValidPart(name, nameof(name));
            if (componentType == null) { throw new ArgumentNullException(nameof(componentType)); }
            if (!_coreTypes.TryAdd(name, componentType))
            {
                throw new InvalidOperationException("Core component '" + name + "' is already registered.");
            }
        }

        public void RegisterCore<T>(string name)
        {
            RegisterCore(name, typeof(T));
        }

        public bool TryGetCoreType(string name, out Type componentType)
        {
            if (name == null) { componentType = null; return false; }
            return _coreTypes.TryGetValue(name, out componentType);
        }

        public ComponentKind Classify(string fullName)
        {
            if (string.IsNullOrEmpty(fullName)) { return ComponentKind.Unknown; }
            if (ComponentName.TryParseModuleName(fullName, out _, out _)) { return ComponentKind.Module; }
            return _coreTypes.ContainsKey(fullName) ? ComponentKind.Core : ComponentKind.Unknown;
        }
    }
}
