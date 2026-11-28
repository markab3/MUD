using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.NpcTemplates;
using MUD.Data;

namespace MUD.Core.Administration
{
    // NPC template calls of the Admin service (specs/ADMIN_API.md "NPC templates", specs/NPC_TEMPLATES.md). A data library only.
    // Nothing references an NPC template yet, so a delete is never refused for use.
    public sealed partial class AdminApi
    {
        public const string NpcTemplateUnavailableMessage = "The NPC template library is not available.";
        public const string NpcTemplateAlreadyExistsMessage = "An NPC template with that id already exists.";
        public const string NpcTemplateNotFoundMessage = "No NPC template with that id exists.";
        public const string NpcTemplateWriteFailedMessage = "The NPC template could not be written. Nothing was changed.";
        public const string NpcTemplateNothingChangedMessage = "Nothing was changed.";
        public const string NpcTemplateEntity = "npc_template";

        private readonly INpcTemplateStore _npcTemplates;

        // ------------------------------------------------------------------ reads (creator or admin)

        public ListNpcTemplatesReply ListNpcTemplates(ListNpcTemplatesRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            var reply = new ListNpcTemplatesReply();
            if (_npcTemplates == null) { return reply; }
            string area = (request.Area ?? string.Empty).Trim();
            IReadOnlyList<NpcTemplateEntry> all = ReadNpcTemplates(() => _npcTemplates.List(area.Length == 0 ? null : area));
            reply.Templates.AddRange(all.Select(ToRecord));
            return reply;
        }

        public GetNpcTemplateReply GetNpcTemplate(NpcTemplateIdRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            NpcTemplateEntry found = _npcTemplates == null ? null : ReadNpcTemplates(() => _npcTemplates.Get((request.Id ?? string.Empty).Trim()));
            return found == null ? new GetNpcTemplateReply { Found = false } : new GetNpcTemplateReply { Found = true, Template = ToRecord(found) };
        }

        // The store throws when the database cannot be reached; the caller sees "core could not answer" (gRPC Unavailable), not a result code.
        private T ReadNpcTemplates<T>(Func<T> read)
        {
            try { return read(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log("NPC template read failed (" + ex.GetType().Name + ").");
                throw new AdminCallException(AdminFailure.Unavailable, "The NPC templates could not be read.");
            }
        }

        // ------------------------------------------------------------------ writes (creator or admin, audited first)

        public NpcTemplateEditReply CreateNpcTemplate(SaveNpcTemplateRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor => SaveNpcTemplate(actor, request.Template, create: true));
        }

        public NpcTemplateEditReply UpdateNpcTemplate(SaveNpcTemplateRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor => SaveNpcTemplate(actor, request.Template, create: false));
        }

        public NpcTemplateEditReply DeleteNpcTemplate(NpcTemplateIdRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor => RemoveNpcTemplate(actor, request.Id));
        }

        private NpcTemplateEditReply SaveNpcTemplate(Actor actor, NpcTemplateRecord record, bool create)
        {
            if (_npcTemplates == null) { return NpcTemplateReply(AdminResult.Refused, NpcTemplateUnavailableMessage); }
            if (record == null) { return NpcTemplateReply(AdminResult.Invalid, null, new[] { AdminPlayerValidator.Error("record", "The NPC template is missing.") }); }

            var errors = new List<FieldError>();
            NpcTemplateEntry entry = ValidateNpcTemplate(record, errors);
            if (errors.Count == 0)
            {
                // Backstop: the area must have a record (the website offers only existing areas).
                FieldError noArea = NpcTemplateMissingAreaError(entry.Area);
                if (noArea != null) { errors.Add(noArea); }
            }
            if (errors.Count > 0) { return NpcTemplateReply(AdminResult.Invalid, null, errors); }

            NpcTemplateEntry stored;
            try { stored = _npcTemplates.Get(entry.Id); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return NpcTemplateFailed("read", ex); }
            if (create && stored != null) { return NpcTemplateReply(AdminResult.AlreadyExists, NpcTemplateAlreadyExistsMessage); }
            if (!create && stored == null) { return NpcTemplateReply(AdminResult.NotFound, NpcTemplateNotFoundMessage); }

            var rows = new List<AuditChange>();
            if (create)
            {
                entry.Version = 1;
                rows.Add(new AuditChange("*create*", BsonNull.Value, NpcTemplateDocuments.ToDocument(entry)));
            }
            else
            {
                BsonDocument before = NpcTemplateDocuments.ToDocument(stored);
                BsonDocument after = NpcTemplateDocuments.ToDocument(entry);
                foreach (string field in new[] { "short", "long", "handles", "adjectives", "plurals", "maxHealth", "maxStamina", "maxAlacrity", "experience",
                    "strength", "dexterity", "intelligence", "wisdom", "constitution", "charisma" })
                {
                    BsonValue old = before.TryGetValue(field, out BsonValue o) ? o : BsonNull.Value;
                    BsonValue now = after.TryGetValue(field, out BsonValue n) ? n : BsonNull.Value;
                    if (!old.Equals(now)) { rows.Add(new AuditChange(field, old, now)); }
                }
                if (rows.Count == 0) { return NpcTemplateReply(AdminResult.NoChange, NpcTemplateNothingChangedMessage, null, stored); }
                entry.Version = stored.Version + 1;
                rows.Add(new AuditChange("version", stored.Version, entry.Version));
            }

            List<AuditRecord> entries = BuildEntries(actor, NpcTemplateEntity, entry.Id, entry.Id, rows);
            if (!TryAudit(entries)) { return NpcTemplateReply(AdminResult.AuditFailed, null); }

            bool written;
            try { written = create ? _npcTemplates.Insert(entry, actor.Player.PlayerName) : _npcTemplates.Replace(entry, actor.Player.PlayerName); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Finish(entries, false);
                return NpcTemplateFailed("write", ex);
            }
            Finish(entries, written);
            // Someone else created or removed the id between our read and our write.
            if (!written) { return create ? NpcTemplateReply(AdminResult.AlreadyExists, NpcTemplateAlreadyExistsMessage) : NpcTemplateReply(AdminResult.NotFound, NpcTemplateNotFoundMessage); }
            return NpcTemplateReply(AdminResult.Ok, null, null, entry);
        }

        // Normalizes the record into an entry and lists every broken rule (fixed sentences, nothing echoed). Null lists count as empty.
        private static NpcTemplateEntry ValidateNpcTemplate(NpcTemplateRecord record, List<FieldError> errors)
        {
            string id = (record.Id ?? string.Empty).Trim();
            string area = (record.Area ?? string.Empty).Trim();
            string shortText = RoomRules.NormalizeText(record.Short ?? string.Empty);
            string longText = RoomRules.NormalizeText(record.Long ?? string.Empty);
            var entry = new NpcTemplateEntry
            {
                Id = id, Area = area, Short = shortText, Long = longText,
                MaxHealth = record.HasMaxHealth ? record.MaxHealth : (int?)null,
                MaxStamina = record.HasMaxStamina ? record.MaxStamina : (int?)null,
                MaxAlacrity = record.HasMaxAlacrity ? record.MaxAlacrity : (int?)null,
                Experience = record.HasExperience ? record.Experience : (long?)null,
                Strength = record.HasStrength ? record.Strength : (int?)null,
                Dexterity = record.HasDexterity ? record.Dexterity : (int?)null,
                Intelligence = record.HasIntelligence ? record.Intelligence : (int?)null,
                Wisdom = record.HasWisdom ? record.Wisdom : (int?)null,
                Constitution = record.HasConstitution ? record.Constitution : (int?)null,
                Charisma = record.HasCharisma ? record.Charisma : (int?)null,
            };

            Add(errors, "Id", NpcTemplateRules.CheckId(id, area));
            Add(errors, "Short", RoomRules.CheckShort(shortText));
            Add(errors, "Long", RoomRules.CheckLong(longText));
            entry.Handles = Words(errors, "Handles", record.Handles);
            entry.Adjectives = Words(errors, "Adjectives", record.Adjectives);
            entry.Plurals = Words(errors, "Plurals", record.Plurals);

            Add(errors, "MaxHealth", NpcTemplateRules.CheckVital("Max health", entry.MaxHealth));
            Add(errors, "MaxStamina", NpcTemplateRules.CheckVital("Max stamina", entry.MaxStamina));
            Add(errors, "MaxAlacrity", NpcTemplateRules.CheckVital("Max alacrity", entry.MaxAlacrity));
            Add(errors, "Experience", NpcTemplateRules.CheckExperience(entry.Experience));
            Add(errors, "Strength", NpcTemplateRules.CheckAbility("Strength", entry.Strength));
            Add(errors, "Dexterity", NpcTemplateRules.CheckAbility("Dexterity", entry.Dexterity));
            Add(errors, "Intelligence", NpcTemplateRules.CheckAbility("Intelligence", entry.Intelligence));
            Add(errors, "Wisdom", NpcTemplateRules.CheckAbility("Wisdom", entry.Wisdom));
            Add(errors, "Constitution", NpcTemplateRules.CheckAbility("Constitution", entry.Constitution));
            Add(errors, "Charisma", NpcTemplateRules.CheckAbility("Charisma", entry.Charisma));
            return entry;
        }

        private NpcTemplateEditReply RemoveNpcTemplate(Actor actor, string rawId)
        {
            if (_npcTemplates == null) { return NpcTemplateReply(AdminResult.Refused, NpcTemplateUnavailableMessage); }
            string id = (rawId ?? string.Empty).Trim();
            NpcTemplateEntry stored;
            try { stored = id.Length == 0 ? null : _npcTemplates.Get(id); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return NpcTemplateFailed("read", ex); }
            if (stored == null) { return NpcTemplateReply(AdminResult.NotFound, NpcTemplateNotFoundMessage); }
            // Delete protection by references: nothing references an NPC template yet (no room, spawn or NPC class uses one), so there is none.

            List<AuditRecord> entries = BuildEntries(actor, NpcTemplateEntity, id, id, new List<AuditChange> { new AuditChange("*delete*", NpcTemplateDocuments.ToDocument(stored), BsonNull.Value) });
            if (!TryAudit(entries)) { return NpcTemplateReply(AdminResult.AuditFailed, null); }

            bool deleted;
            try { deleted = _npcTemplates.Delete(id); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Finish(entries, false);
                return NpcTemplateFailed("delete", ex);
            }
            Finish(entries, deleted);
            return deleted ? NpcTemplateReply(AdminResult.Ok, null) : NpcTemplateReply(AdminResult.NotFound, NpcTemplateNotFoundMessage);
        }

        // With no area store (not configured) nothing can be checked, as for rooms.
        private FieldError NpcTemplateMissingAreaError(string area)
        {
            if (_areas == null) { return null; }
            try { if (_areas.Get(area) != null) { return null; } }
            catch (Exception ex) when (ex is not OutOfMemoryException) { throw new AdminCallException(AdminFailure.Unavailable, "The areas could not be read."); }
            return AdminPlayerValidator.Error("Area", "There is no area with that prefix. Create the area first.");
        }

        // The number of NPC templates of an area, for the area delete refusal. No store = none.
        private int CountNpcTemplatesInArea(string area)
        {
            return _npcTemplates == null ? 0 : _npcTemplates.CountInArea(area);
        }

        private NpcTemplateEditReply NpcTemplateFailed(string what, Exception ex)
        {
            // Generic result: no exception text, no values.
            _log("NPC template " + what + " failed (" + ex.GetType().Name + ").");
            return NpcTemplateReply(AdminResult.Failed, NpcTemplateWriteFailedMessage);
        }

        private static NpcTemplateEditReply NpcTemplateReply(AdminResult result, string message, IEnumerable<FieldError> errors = null, NpcTemplateEntry entry = null)
        {
            var reply = new NpcTemplateEditReply { Result = result, Message = message ?? string.Empty };
            if (errors != null) { reply.Errors.AddRange(errors); }
            if (entry != null) { reply.Template = ToRecord(entry); }
            return reply;
        }

        public static NpcTemplateRecord ToRecord(NpcTemplateEntry e)
        {
            var record = new NpcTemplateRecord
            {
                Id = e.Id ?? string.Empty, Area = e.Area ?? string.Empty, Short = e.Short ?? string.Empty, Long = e.Long ?? string.Empty, Version = e.Version,
            };
            record.Handles.AddRange(e.Handles ?? new List<string>());
            record.Adjectives.AddRange(e.Adjectives ?? new List<string>());
            record.Plurals.AddRange(e.Plurals ?? new List<string>());
            if (e.MaxHealth.HasValue) { record.MaxHealth = e.MaxHealth.Value; }
            if (e.MaxStamina.HasValue) { record.MaxStamina = e.MaxStamina.Value; }
            if (e.MaxAlacrity.HasValue) { record.MaxAlacrity = e.MaxAlacrity.Value; }
            if (e.Experience.HasValue) { record.Experience = e.Experience.Value; }
            if (e.Strength.HasValue) { record.Strength = e.Strength.Value; }
            if (e.Dexterity.HasValue) { record.Dexterity = e.Dexterity.Value; }
            if (e.Intelligence.HasValue) { record.Intelligence = e.Intelligence.Value; }
            if (e.Wisdom.HasValue) { record.Wisdom = e.Wisdom.Value; }
            if (e.Constitution.HasValue) { record.Constitution = e.Constitution.Value; }
            if (e.Charisma.HasValue) { record.Charisma = e.Charisma.Value; }
            return record;
        }
    }
}
