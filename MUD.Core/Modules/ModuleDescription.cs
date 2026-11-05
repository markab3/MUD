using System;
using System.Collections.Generic;
using System.Text.Json;

namespace MUD.Core.Modules
{
    /// <summary>
    /// What a module executable prints for `--describe` (owner decision D67): one JSON document with the module name, module
    /// version, contract version, the executable's own arguments, the behavior types it provides and whether it is required.
    /// The property names below were chosen by card P4-3 and are flagged in its pull request; the C# module library (P4-5) must print them.
    ///
    ///   { "name": "desert", "version": "1.0.0", "contract_version": 1, "arguments": ["--quiet"],
    ///     "behavior_types": ["DesertStorm"], "required": false }
    ///
    /// `arguments` and `behavior_types` may be left out. `required` defaults to false (D67 working default 1). The description grants
    /// nothing (D67); capabilities are requested in the manifest at connect time and decided by the grants file.
    /// </summary>
    public sealed class ModuleDescription
    {
        public string Name { get; private set; }
        public string Version { get; private set; }
        public uint ContractVersion { get; private set; }
        public IReadOnlyList<string> Arguments { get; private set; }
        public IReadOnlyList<string> BehaviorTypes { get; private set; }
        public bool Required { get; private set; }

        /// <summary>Parses the output of `--describe`. Returns null and says why when the document is not usable.</summary>
        public static ModuleDescription Parse(string json, out string problem)
        {
            problem = null;
            JsonDocument document;
            try
            {
                document = JsonDocument.Parse(json ?? string.Empty);
            }
            catch (JsonException ex)
            {
                problem = "the --describe output is not valid JSON: " + ex.Message;
                return null;
            }

            using (document)
            {
                JsonElement root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    problem = "the --describe output must be one JSON object.";
                    return null;
                }

                var description = new ModuleDescription();
                JsonElement value;

                if (!root.TryGetProperty("name", out value) || value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                {
                    problem = "the --describe output has no \"name\" text.";
                    return null;
                }
                description.Name = value.GetString();

                description.Version = root.TryGetProperty("version", out value) && value.ValueKind == JsonValueKind.String ? value.GetString() : "";

                if (!root.TryGetProperty("contract_version", out value) || value.ValueKind != JsonValueKind.Number || !value.TryGetUInt32(out uint contractVersion))
                {
                    problem = "the --describe output has no \"contract_version\" whole number.";
                    return null;
                }
                description.ContractVersion = contractVersion;

                if (!ReadTextList(root, "arguments", out IReadOnlyList<string> arguments, out problem)) { return null; }
                description.Arguments = arguments;
                if (!ReadTextList(root, "behavior_types", out IReadOnlyList<string> behaviorTypes, out problem)) { return null; }
                description.BehaviorTypes = behaviorTypes;

                if (root.TryGetProperty("required", out value))
                {
                    if (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)
                    {
                        problem = "\"required\" in the --describe output must be true or false.";
                        return null;
                    }
                    description.Required = value.GetBoolean();
                }
                return description;
            }
        }

        private static bool ReadTextList(JsonElement root, string name, out IReadOnlyList<string> list, out string problem)
        {
            problem = null;
            var items = new List<string>();
            list = items;
            JsonElement value;
            if (!root.TryGetProperty(name, out value) || value.ValueKind == JsonValueKind.Null) { return true; }
            if (value.ValueKind != JsonValueKind.Array)
            {
                problem = "\"" + name + "\" in the --describe output must be a list of text.";
                return false;
            }
            foreach (JsonElement item in value.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                {
                    problem = "\"" + name + "\" in the --describe output must be a list of text.";
                    return false;
                }
                items.Add(item.GetString());
            }
            return true;
        }
    }
}
