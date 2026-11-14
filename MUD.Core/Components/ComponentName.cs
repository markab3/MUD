using System;

namespace MUD.Core.Components
{
    // A component is addressed either as a core name ("roles", one segment, registered by core) or as a module
    // name ("desert.storm": the module that owns it, a dot, the component name). Module components are stored in
    // MongoDB as components.<module>.<name> (owner decisions D50, D13), so neither part may be empty or contain
    // a dot or a dollar sign, which MongoDB does not allow in field names.
    public static class ComponentName
    {
        public static bool IsValidPart(string part)
        {
            return !string.IsNullOrWhiteSpace(part) && part.IndexOfAny(new[] { '.', '$', '\0' }) < 0 && part == part.Trim();
        }

        // Splits "module.name" into its two parts. Returns false when the text is not a module component name.
        public static bool TryParseModuleName(string fullName, out string moduleName, out string componentName)
        {
            moduleName = null;
            componentName = null;
            if (string.IsNullOrEmpty(fullName)) { return false; }
            int dot = fullName.IndexOf('.');
            if (dot < 0) { return false; }
            string module = fullName.Substring(0, dot);
            string name = fullName.Substring(dot + 1);
            if (!IsValidPart(module) || !IsValidPart(name)) { return false; }
            moduleName = module;
            componentName = name;
            return true;
        }

        public static void RequireValidPart(string part, string parameterName)
        {
            if (!IsValidPart(part))
            {
                throw new ArgumentException("A component name part must be non-empty and must not contain '.', '$' or leading/trailing spaces: '" + part + "'", parameterName);
            }
        }
    }
}
