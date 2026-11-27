using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.ItemTemplates;
using MUD.Data;

namespace MUD.Core.Administration
{
    // Item template calls of the Admin service (specs/ADMIN_API.md "Item templates", specs/ITEM_TEMPLATES.md).
    public sealed partial class AdminApi
    {
        public const string ItemTemplateUnavailableMessage = "The item template library is not available.";
        public const string ItemTemplateAlreadyExistsMessage = "An item template with that id already exists.";
        public const string ItemTemplateNotFoundMessage = "No item template with that id exists.";
        public const string ItemTemplateWriteFailedMessage = "The item template could not be written. Nothing was changed.";
        public const string ItemTemplateNothingChangedMessage = "Nothing was changed.";
        public const string ItemTemplateEntity = "item_template";

        private readonly IItemTemplateStore _itemTemplates;

        // ------------------------------------------------------------------ reads (creator or admin)

        public ListItemTemplatesReply ListItemTemplates(ListItemTemplatesRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            var reply = new ListItemTemplatesReply();
            if (_itemTemplates == null) { return reply; }
            string area = (request.Area ?? string.Empty).Trim();
            IReadOnlyList<ItemTemplateEntry> all = ReadItemTemplates(() => _itemTemplates.List(area.Length == 0 ? null : area));
            reply.Templates.AddRange(all.Select(ToRecord));
            return reply;
        }

        public GetItemTemplateReply GetItemTemplate(ItemTemplateIdRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            ItemTemplateEntry found = _itemTemplates == null ? null : ReadItemTemplates(() => _itemTemplates.Get((request.Id ?? string.Empty).Trim()));
            return found == null ? new GetItemTemplateReply { Found = false } : new GetItemTemplateReply { Found = true, Template = ToRecord(found) };
        }

        // The store throws when the database cannot be reached; the caller sees "core could not answer" (gRPC Unavailable), not a result code.
        private T ReadItemTemplates<T>(Func<T> read)
        {
            try { return read(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log("Item template read failed (" + ex.GetType().Name + ").");
                throw new AdminCallException(AdminFailure.Unavailable, "The item templates could not be read.");
            }
        }

        // ------------------------------------------------------------------ writes (creator or admin, audited first)

        public ItemTemplateEditReply CreateItemTemplate(SaveItemTemplateRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor => SaveItemTemplate(actor, request.Template, create: true));
        }

        public ItemTemplateEditReply UpdateItemTemplate(SaveItemTemplateRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor => SaveItemTemplate(actor, request.Template, create: false));
        }

        public ItemTemplateEditReply DeleteItemTemplate(ItemTemplateIdRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor => RemoveItemTemplate(actor, request.Id));
        }

        private ItemTemplateEditReply SaveItemTemplate(Actor actor, ItemTemplateRecord record, bool create)
        {
            if (_itemTemplates == null) { return ItemTemplateReply(AdminResult.Refused, ItemTemplateUnavailableMessage); }
            if (record == null) { return ItemTemplateReply(AdminResult.Invalid, null, new[] { AdminPlayerValidator.Error("record", "The item template is missing.") }); }

            var errors = new List<FieldError>();
            ItemTemplateEntry entry = ValidateItemTemplate(record, errors);
            if (errors.Count == 0)
            {
                // Backstop: the area must have a record (the website offers only existing areas).
                FieldError noArea = ItemTemplateMissingAreaError(entry.Area);
                if (noArea != null) { errors.Add(noArea); }
            }
            if (errors.Count > 0) { return ItemTemplateReply(AdminResult.Invalid, null, errors); }

            ItemTemplateEntry stored;
            try { stored = _itemTemplates.Get(entry.Id); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return ItemTemplateFailed("read", ex); }
            if (create && stored != null) { return ItemTemplateReply(AdminResult.AlreadyExists, ItemTemplateAlreadyExistsMessage); }
            if (!create && stored == null) { return ItemTemplateReply(AdminResult.NotFound, ItemTemplateNotFoundMessage); }

            var rows = new List<AuditChange>();
            if (create)
            {
                entry.Version = 1;
                rows.Add(new AuditChange("*create*", BsonNull.Value, ItemTemplateDocuments.ToDocument(entry)));
            }
            else
            {
                BsonDocument before = ItemTemplateDocuments.ToDocument(stored);
                BsonDocument after = ItemTemplateDocuments.ToDocument(entry);
                foreach (string field in new[] { "short", "long", "handles", "adjectives", "plurals", "components", "behaviors" })
                {
                    BsonValue old = before.TryGetValue(field, out BsonValue o) ? o : BsonNull.Value;
                    BsonValue now = after.TryGetValue(field, out BsonValue n) ? n : BsonNull.Value;
                    if (!old.Equals(now)) { rows.Add(new AuditChange(field, old, now)); }
                }
                if (rows.Count == 0) { return ItemTemplateReply(AdminResult.NoChange, ItemTemplateNothingChangedMessage, null, stored); }
                entry.Version = stored.Version + 1;
                rows.Add(new AuditChange("version", stored.Version, entry.Version));
            }

            List<AuditRecord> entries = BuildEntries(actor, ItemTemplateEntity, entry.Id, entry.Id, rows);
            if (!TryAudit(entries)) { return ItemTemplateReply(AdminResult.AuditFailed, null); }

            bool written;
            try { written = create ? _itemTemplates.Insert(entry, actor.Player.PlayerName) : _itemTemplates.Replace(entry, actor.Player.PlayerName); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Finish(entries, false);
                return ItemTemplateFailed("write", ex);
            }
            Finish(entries, written);
            // Someone else created or removed the id between our read and our write.
            if (!written) { return create ? ItemTemplateReply(AdminResult.AlreadyExists, ItemTemplateAlreadyExistsMessage) : ItemTemplateReply(AdminResult.NotFound, ItemTemplateNotFoundMessage); }
            return ItemTemplateReply(AdminResult.Ok, null, null, entry);
        }

        // Normalizes the record into an entry and lists every broken rule (fixed sentences, nothing echoed). Null lists count as empty.
        private static ItemTemplateEntry ValidateItemTemplate(ItemTemplateRecord record, List<FieldError> errors)
        {
            string id = (record.Id ?? string.Empty).Trim();
            string area = (record.Area ?? string.Empty).Trim();
            string shortText = RoomRules.NormalizeText(record.Short ?? string.Empty);
            string longText = RoomRules.NormalizeText(record.Long ?? string.Empty);
            var entry = new ItemTemplateEntry { Id = id, Area = area, Short = shortText, Long = longText };

            Add(errors, "Id", ItemTemplateRules.CheckId(id, area));
            Add(errors, "Short", RoomRules.CheckShort(shortText));
            Add(errors, "Long", RoomRules.CheckLong(longText));
            entry.Handles = Words(errors, "Handles", record.Handles);
            entry.Adjectives = Words(errors, "Adjectives", record.Adjectives);
            entry.Plurals = Words(errors, "Plurals", record.Plurals);

            Add(errors, "ComponentsJson", ItemTemplateRules.ParseComponents(record.ComponentsJson, out BsonDocument components));
            entry.Components = components;

            if (record.Behaviors != null && record.Behaviors.Count > ItemTemplateRules.MaxBehaviors)
            {
                errors.Add(AdminPlayerValidator.Error("Behaviors", "An item template may have at most " + ItemTemplateRules.MaxBehaviors + " behaviors."));
            }
            else
            {
                int number = 0;
                foreach (RoomBehavior b in record.Behaviors ?? new Google.Protobuf.Collections.RepeatedField<RoomBehavior>())
                {
                    number++;
                    string problem = ItemTemplateRules.ParseBehavior(b.Hook, b.Module, b.Type, b.Priority, b.ConfigJson, out MUD.Core.Components.BehaviorRef parsed);
                    if (problem != null) { errors.Add(AdminPlayerValidator.Error("Behaviors", "Behavior " + number + ": " + problem)); }
                    else { entry.Behaviors.Add(parsed); }
                }
            }
            return entry;
        }

        private static void Add(List<FieldError> errors, string field, string problem)
        {
            if (problem != null) { errors.Add(AdminPlayerValidator.Error(field, AdminPlayerValidator.Capitalize(problem))); }
        }

        // Trims entries; a list that breaks a rule adds one error and yields what was sent.
        private static List<string> Words(List<FieldError> errors, string field, IEnumerable<string> sent)
        {
            List<string> words = (sent ?? new string[0]).Select(w => (w ?? string.Empty).Trim()).ToList();
            Add(errors, field, ItemTemplateRules.CheckWords(field.ToLowerInvariant(), words));
            return words;
        }

        private ItemTemplateEditReply RemoveItemTemplate(Actor actor, string rawId)
        {
            if (_itemTemplates == null) { return ItemTemplateReply(AdminResult.Refused, ItemTemplateUnavailableMessage); }
            string id = (rawId ?? string.Empty).Trim();
            ItemTemplateEntry stored;
            try { stored = id.Length == 0 ? null : _itemTemplates.Get(id); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return ItemTemplateFailed("read", ex); }
            if (stored == null) { return ItemTemplateReply(AdminResult.NotFound, ItemTemplateNotFoundMessage); }
            // Nothing references an item template yet. When the reference card lands, a referenced template is refused here (Refused,
            // message naming the count), as DeleteArea does for rooms.

            List<AuditRecord> entries = BuildEntries(actor, ItemTemplateEntity, id, id, new List<AuditChange> { new AuditChange("*delete*", ItemTemplateDocuments.ToDocument(stored), BsonNull.Value) });
            if (!TryAudit(entries)) { return ItemTemplateReply(AdminResult.AuditFailed, null); }

            bool deleted;
            try { deleted = _itemTemplates.Delete(id); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Finish(entries, false);
                return ItemTemplateFailed("delete", ex);
            }
            Finish(entries, deleted);
            return deleted ? ItemTemplateReply(AdminResult.Ok, null) : ItemTemplateReply(AdminResult.NotFound, ItemTemplateNotFoundMessage);
        }

        // With no area store (not configured) nothing can be checked, as for rooms.
        private FieldError ItemTemplateMissingAreaError(string area)
        {
            if (_areas == null) { return null; }
            try { if (_areas.Get(area) != null) { return null; } }
            catch (Exception ex) when (ex is not OutOfMemoryException) { throw new AdminCallException(AdminFailure.Unavailable, "The areas could not be read."); }
            return AdminPlayerValidator.Error("Area", "There is no area with that prefix. Create the area first.");
        }

        private ItemTemplateEditReply ItemTemplateFailed(string what, Exception ex)
        {
            // Generic result: no exception text, no values.
            _log("Item template " + what + " failed (" + ex.GetType().Name + ").");
            return ItemTemplateReply(AdminResult.Failed, ItemTemplateWriteFailedMessage);
        }

        private static ItemTemplateEditReply ItemTemplateReply(AdminResult result, string message, IEnumerable<FieldError> errors = null, ItemTemplateEntry entry = null)
        {
            var reply = new ItemTemplateEditReply { Result = result, Message = message ?? string.Empty };
            if (errors != null) { reply.Errors.AddRange(errors); }
            if (entry != null) { reply.Template = ToRecord(entry); }
            return reply;
        }

        public static ItemTemplateRecord ToRecord(ItemTemplateEntry e)
        {
            var record = new ItemTemplateRecord
            {
                Id = e.Id ?? string.Empty, Area = e.Area ?? string.Empty, Short = e.Short ?? string.Empty, Long = e.Long ?? string.Empty,
                ComponentsJson = e.Components == null ? string.Empty : CompactJson(e.Components), Version = e.Version,
            };
            record.Handles.AddRange(e.Handles ?? new List<string>());
            record.Adjectives.AddRange(e.Adjectives ?? new List<string>());
            record.Plurals.AddRange(e.Plurals ?? new List<string>());
            foreach (MUD.Core.Components.BehaviorRef b in e.Behaviors ?? new List<MUD.Core.Components.BehaviorRef>())
            {
                if (b != null) { record.Behaviors.Add(BehaviorToDetail(b.Hook, b.Module, b.Type, b.Priority, b.Config)); }
            }
            return record;
        }
    }
}
