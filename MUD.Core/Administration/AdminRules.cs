using System;
using System.Collections.Generic;
using System.Linq;
using MUD.Contracts;
using MUD.Data;

namespace MUD.Core.Administration
{
    /// <summary>
    /// What an editor may do to a target (owner decisions in specs/WEB_EDITING.md section 15): an editor may edit only players of strictly
    /// LOWER role than their own (a creator edits players; an admin edits creators and players; nobody edits an admin), may not grant a role
    /// above their own, and retiring or un-retiring another player is an admin power. Pure functions; the same rules the website applied
    /// before core took over the edits.
    /// </summary>
    public static class AdminPlayerRules
    {
        public const int MaxFreeText = 32;

        public static bool CanEdit(AdminRole editor, AdminRole target)
        {
            return editor >= AdminRole.Creator && editor > target;
        }

        public static bool CanSetRole(AdminRole editor, AdminRole target, AdminRole newRole)
        {
            return editor == AdminRole.Admin && CanEdit(editor, target) && newRole <= editor;
        }

        public static bool CanSetRetired(AdminRole editor, AdminRole target)
        {
            return editor == AdminRole.Admin && CanEdit(editor, target);
        }

        // Defensive: lowering the role of an admin when no other admin exists would leave nobody able to grant roles.
        public static bool WouldRemoveLastAdmin(AdminRole target, AdminRole newRole, int adminCount)
        {
            return target == AdminRole.Admin && newRole < AdminRole.Admin && adminCount <= 1;
        }

        // Free text (race, gender, class): length-limited, no control characters, no fixed vocabulary.
        public static string CheckFreeText(string label, string value)
        {
            if (value.Length > MaxFreeText) { return label + " must be at most " + MaxFreeText + " characters."; }
            if (value.Any(char.IsControl)) { return label + " must not contain control characters."; }
            return null;
        }
    }

    /// <summary>Validation of the editable text fields of a player. Every verdict for descriptions comes from MUD.Data.RoomRules.</summary>
    public static class AdminPlayerValidator
    {
        public sealed class Result
        {
            public string Short;
            public string Long;
            public string Race;
            public string Gender;
            public string Class;
            public List<FieldError> Errors = new List<FieldError>();
        }

        public static Result Validate(string shortText, string longText, string race, string gender, string playerClass)
        {
            var result = new Result();
            string normalizedShort = RoomRules.NormalizeText(shortText ?? string.Empty);
            string normalizedLong = RoomRules.NormalizeText(longText ?? string.Empty);

            string problem = RoomRules.CheckShort(normalizedShort);
            if (problem != null) { result.Errors.Add(Error("Short", Capitalize(problem))); }
            // A player's long description may be empty (new characters have none); when present it follows the room rules.
            if (normalizedLong.Length > 0)
            {
                problem = RoomRules.CheckLong(normalizedLong);
                if (problem != null) { result.Errors.Add(Error("Long", Capitalize(problem))); }
            }

            result.Short = normalizedShort;
            result.Long = normalizedLong.Length == 0 ? null : normalizedLong;
            result.Race = Free(result.Errors, "Race", race);
            result.Gender = Free(result.Errors, "Gender", gender);
            result.Class = Free(result.Errors, "Class", playerClass);
            return result;
        }

        private static string Free(List<FieldError> errors, string label, string value)
        {
            string trimmed = (value ?? string.Empty).Trim();
            string problem = AdminPlayerRules.CheckFreeText(label, trimmed);
            if (problem != null) { errors.Add(Error(label, problem)); }
            return trimmed.Length == 0 ? null : trimmed;
        }

        internal static FieldError Error(string field, string message)
        {
            return new FieldError { Field = field, Message = message };
        }

        internal static string Capitalize(string message)
        {
            return char.ToUpperInvariant(message[0]) + message.Substring(1);
        }
    }

    /// <summary>
    /// Validation of a room's replacement Feature list. Verdicts come from MUD.Data.RoomRules (the importer's rules: short and long
    /// required, no duplicate short in a room compared case-insensitively, room_line and detail optional with empty stored as null,
    /// text normalised per ROOM_FILES.md section 11). The importer has no handle requirement, so none is made here.
    /// </summary>
    public static class AdminFeatureValidator
    {
        /// <summary>Guard on request size; the importer has no such limit.</summary>
        public const int MaxFeatures = 64;
        public const int MaxWordsPerList = 32;

        public sealed class Result
        {
            public List<RoomFeature> Features = new List<RoomFeature>();
            public List<FieldError> Errors = new List<FieldError>();
        }

        public static Result Validate(IEnumerable<RoomFeature> features)
        {
            var result = new Result();
            List<RoomFeature> all = (features ?? new RoomFeature[0]).ToList();
            if (all.Count > MaxFeatures) { result.Errors.Add(AdminPlayerValidator.Error("Features", "At most " + MaxFeatures + " features are supported.")); return result; }
            for (int i = 0; i < all.Count; i++)
            {
                RoomFeature raw = all[i] ?? new RoomFeature();
                string prefix = "Features[" + i + "].";
                var feature = new RoomFeature
                {
                    Short = RoomRules.NormalizeText(raw.Short ?? string.Empty),
                    Long = RoomRules.NormalizeText(raw.Long ?? string.Empty),
                    RoomLine = RoomRules.NormalizeText(raw.RoomLine ?? string.Empty),
                    Detail = RoomRules.NormalizeText(raw.Detail ?? string.Empty)
                };
                Report(result, prefix + "Short", RoomRules.CheckFeatureShort(feature.Short));
                Report(result, prefix + "Long", RoomRules.CheckFeatureLong(feature.Long));
                Report(result, prefix + "RoomLine", RoomRules.CheckFeatureOptionalText("Room line", feature.RoomLine));
                Report(result, prefix + "Detail", RoomRules.CheckFeatureOptionalText("Detail", feature.Detail));
                Words(result, prefix + "Handles", "Handle", raw.Handles, feature.Handles);
                Words(result, prefix + "Adjectives", "Adjective", raw.Adjectives, feature.Adjectives);
                Words(result, prefix + "Plurals", "Plural", raw.Plurals, feature.Plurals);
                result.Features.Add(feature);
            }
            if (RoomRules.DuplicateFeatureShorts(result.Features.Select(f => f.Short)).Count > 0)
            {
                result.Errors.Add(AdminPlayerValidator.Error("Features", "Two features use the same short description."));
            }
            return result;
        }

        private static void Words(Result result, string field, string label, IEnumerable<string> raw, Google.Protobuf.Collections.RepeatedField<string> into)
        {
            List<string> all = raw.ToList();
            if (all.Count > MaxWordsPerList) { result.Errors.Add(AdminPlayerValidator.Error(field, "At most " + MaxWordsPerList + " entries are supported.")); return; }
            foreach (string word in all)
            {
                string trimmed = (word ?? string.Empty).Trim();
                Report(result, field, RoomRules.CheckFeatureWord(label, trimmed));
                into.Add(trimmed);
            }
        }

        private static void Report(Result result, string field, string problem)
        {
            if (problem != null) { result.Errors.Add(AdminPlayerValidator.Error(field, AdminPlayerValidator.Capitalize(problem))); }
        }
    }

    /// <summary>The one place core validates a room edit. Every verdict comes from MUD.Data.RoomRules, the same rules the importer applies.</summary>
    public static class AdminRoomValidator
    {
        /// <summary>Guard on request size; the importer has no such limit.</summary>
        public const int MaxExits = 32;

        public sealed class Result
        {
            public string Short;
            public string Long;
            public string Area;
            public List<RoomExit> Exits = new List<RoomExit>();
            public List<FieldError> Errors = new List<FieldError>();
            public List<string> Dangling = new List<string>();
        }

        /// <summary>exists answers "is there a room with this key". Targets equal to the room's own key always count as existing.</summary>
        public static Result Validate(string key, string shortText, string longText, string area, IEnumerable<RoomExit> exits, Func<string, bool> exists)
        {
            var result = new Result();
            string normalizedShort = RoomRules.NormalizeText(shortText ?? string.Empty);
            string normalizedLong = RoomRules.NormalizeText(longText ?? string.Empty);
            string normalizedArea = (area ?? string.Empty).Trim();
            result.Short = normalizedShort;
            result.Long = normalizedLong;
            result.Area = normalizedArea;

            string problem = RoomRules.CheckShort(normalizedShort);
            if (problem != null) { result.Errors.Add(AdminPlayerValidator.Error("Short", AdminPlayerValidator.Capitalize(problem))); }
            problem = RoomRules.CheckLong(normalizedLong);
            if (problem != null) { result.Errors.Add(AdminPlayerValidator.Error("Long", AdminPlayerValidator.Capitalize(problem))); }

            if (RoomRules.LooksLikeObjectId(key) && key.IndexOf('.') >= 0)
            {
                result.Errors.Add(AdminPlayerValidator.Error("Key", "Authored keys must not look like generated ids."));
            }
            if (normalizedArea.Length > 0 || key.IndexOf('.') >= 0)
            {
                problem = RoomRules.CheckAreaPrefix(normalizedArea);
                if (problem != null) { result.Errors.Add(AdminPlayerValidator.Error("Area", AdminPlayerValidator.Capitalize(problem))); }
                else
                {
                    problem = RoomRules.CheckKeyMatchesArea(key, normalizedArea);
                    if (problem != null) { result.Errors.Add(AdminPlayerValidator.Error("Area", AdminPlayerValidator.Capitalize(problem))); }
                }
            }

            List<RoomExit> all = (exits ?? new RoomExit[0]).ToList();
            if (all.Count > MaxExits) { result.Errors.Add(AdminPlayerValidator.Error("Exits", "At most " + MaxExits + " exits are supported.")); }
            for (int i = 0; i < all.Count && i < MaxExits; i++)
            {
                string direction = (all[i] == null ? string.Empty : all[i].Name ?? string.Empty).Trim();
                string target = (all[i] == null ? string.Empty : all[i].To ?? string.Empty).Trim();
                if (direction.Length == 0 && target.Length == 0) { continue; }
                problem = RoomRules.CheckDirection(direction);
                if (problem != null) { result.Errors.Add(AdminPlayerValidator.Error("Exits[" + i + "].Dir", AdminPlayerValidator.Capitalize(problem))); }
                problem = RoomRules.CheckRoomKeyReference(target);
                if (problem != null) { result.Errors.Add(AdminPlayerValidator.Error("Exits[" + i + "].To", AdminPlayerValidator.Capitalize(problem))); }
                result.Exits.Add(new RoomExit { Name = direction, To = target });
            }
            if (RoomRules.DuplicateDirections(result.Exits.Select(e => e.Name)).Count > 0)
            {
                result.Errors.Add(AdminPlayerValidator.Error("Exits", "Two exits use the same direction."));
            }

            if (result.Errors.Count == 0)
            {
                foreach (RoomExit exit in result.Exits)
                {
                    if (exit.To != key && !exists(exit.To) && !result.Dangling.Contains(exit.To)) { result.Dangling.Add(exit.To); }
                }
            }
            return result;
        }
    }
}
