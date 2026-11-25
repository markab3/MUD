using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Generation.Terrains;

namespace MUD.Core.Administration
{
    // Terrain library calls of the Admin service (specs/TERRAIN_GENERATORS.md section 6, specs/ADMIN_API.md "Terrain library").
    public sealed partial class AdminApi
    {
        public const string LibraryUnavailableMessage = "The terrain library is not available.";
        public const string LibraryAlreadyExistsMessage = "An entity with that id already exists.";
        public const string LibraryNotFoundMessage = "No entity with that id exists.";
        public const string LibraryWriteFailedMessage = "The library could not be written. Nothing was changed.";
        public const string LibraryNothingChangedMessage = "Nothing was changed.";

        public const string FragmentEntity = "terrain_fragment";
        public const string FeatureEntity = "terrain_feature";
        public const string PhraseEntity = "terrain_neighbor_phrase";

        private readonly ILibraryStore _library;
        private readonly StoreLibrary _libraryCache;

        // What differs between the three entity kinds; the flow of every write is the same.
        private sealed class LibraryKind<T> where T : class
        {
            public string Entity;
            public Func<ILibraryStore, ILibraryCollection<T>> Collection;
            public Func<T, string> Id;
            public Func<T, BsonDocument> ToDocument;
            public Func<T, List<LibraryError>> Validate;
        }

        private static readonly LibraryKind<DescriptionFragment> Fragments = new LibraryKind<DescriptionFragment>
        {
            Entity = FragmentEntity, Collection = s => s.Fragments, Id = e => e.Id, ToDocument = LibraryDocuments.ToDocument, Validate = LibraryValidation.Validate,
        };

        private static readonly LibraryKind<FeatureTemplate> Features = new LibraryKind<FeatureTemplate>
        {
            Entity = FeatureEntity, Collection = s => s.Features, Id = e => e.Id, ToDocument = LibraryDocuments.ToDocument, Validate = LibraryValidation.Validate,
        };

        private static readonly LibraryKind<NeighborPhrase> Phrases = new LibraryKind<NeighborPhrase>
        {
            Entity = PhraseEntity, Collection = s => s.Phrases, Id = e => e.Id, ToDocument = LibraryDocuments.ToDocument, Validate = LibraryValidation.Validate,
        };

        // ------------------------------------------------------------------ reads (creator or admin)

        public ListFragmentsReply ListFragments(TokenRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            var reply = new ListFragmentsReply();
            if (_library == null) { return reply; }
            reply.LibraryVersion = ReadLibrary(() => _library.GetVersion() ?? 0);
            reply.Fragments.AddRange(ReadLibrary(() => _library.Fragments.List()).Select(ToRecord));
            return reply;
        }

        public GetFragmentReply GetFragment(LibraryIdRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            DescriptionFragment found = _library == null ? null : ReadLibrary(() => _library.Fragments.Get(request.Id ?? string.Empty));
            return found == null ? new GetFragmentReply { Found = false } : new GetFragmentReply { Found = true, Fragment = ToRecord(found) };
        }

        public ListFeatureTemplatesReply ListFeatureTemplates(TokenRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            var reply = new ListFeatureTemplatesReply();
            if (_library == null) { return reply; }
            reply.LibraryVersion = ReadLibrary(() => _library.GetVersion() ?? 0);
            reply.Templates.AddRange(ReadLibrary(() => _library.Features.List()).Select(ToRecord));
            return reply;
        }

        public GetFeatureTemplateReply GetFeatureTemplate(LibraryIdRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            FeatureTemplate found = _library == null ? null : ReadLibrary(() => _library.Features.Get(request.Id ?? string.Empty));
            return found == null ? new GetFeatureTemplateReply { Found = false } : new GetFeatureTemplateReply { Found = true, Template = ToRecord(found) };
        }

        public ListNeighborPhrasesReply ListNeighborPhrases(TokenRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            var reply = new ListNeighborPhrasesReply();
            if (_library == null) { return reply; }
            reply.LibraryVersion = ReadLibrary(() => _library.GetVersion() ?? 0);
            reply.Phrases.AddRange(ReadLibrary(() => _library.Phrases.List()).Select(ToRecord));
            return reply;
        }

        public GetNeighborPhraseReply GetNeighborPhrase(LibraryIdRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            NeighborPhrase found = _library == null ? null : ReadLibrary(() => _library.Phrases.Get(request.Id ?? string.Empty));
            return found == null ? new GetNeighborPhraseReply { Found = false } : new GetNeighborPhraseReply { Found = true, Phrase = ToRecord(found) };
        }

        // The store throws when the database cannot be reached; the caller sees "core could not answer" (gRPC Unavailable), not a result code.
        private T ReadLibrary<T>(Func<T> read)
        {
            try { return read(); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log("Library read failed (" + ex.GetType().Name + ").");
                throw new AdminCallException(AdminFailure.Unavailable, "The library could not be read.");
            }
        }

        // ------------------------------------------------------------------ writes (creator or admin, audited first)

        public LibraryEditReply CreateFragment(SaveFragmentRequest request)
        {
            DescriptionFragment entity = request.Fragment == null ? null : FromRecord(request.Fragment);
            return Mutate(request.Token, AdminRole.Creator, actor => SaveEntity(actor, Fragments, entity, create: true, prepare: null, reply: (r, e) => r.Fragment = ToRecord(e)));
        }

        public LibraryEditReply UpdateFragment(SaveFragmentRequest request)
        {
            DescriptionFragment entity = request.Fragment == null ? null : FromRecord(request.Fragment);
            return Mutate(request.Token, AdminRole.Creator, actor => SaveEntity(actor, Fragments, entity, create: false, prepare: null, reply: (r, e) => r.Fragment = ToRecord(e)));
        }

        public LibraryEditReply DeleteFragment(LibraryIdRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor => DeleteEntity(actor, Fragments, request.Id));
        }

        public LibraryEditReply CreateFeatureTemplate(SaveFeatureTemplateRequest request)
        {
            FeatureTemplate entity = request.Template == null ? null : FromRecord(request.Template);
            return Mutate(request.Token, AdminRole.Creator, actor => SaveEntity(actor, Features, entity, create: true, prepare: BumpFeatureVersion, reply: (r, e) => r.Template = ToRecord(e)));
        }

        public LibraryEditReply UpdateFeatureTemplate(SaveFeatureTemplateRequest request)
        {
            FeatureTemplate entity = request.Template == null ? null : FromRecord(request.Template);
            return Mutate(request.Token, AdminRole.Creator, actor => SaveEntity(actor, Features, entity, create: false, prepare: BumpFeatureVersion, reply: (r, e) => r.Template = ToRecord(e)));
        }

        public LibraryEditReply DeleteFeatureTemplate(LibraryIdRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor => DeleteEntity(actor, Features, request.Id));
        }

        public LibraryEditReply CreateNeighborPhrase(SaveNeighborPhraseRequest request)
        {
            NeighborPhrase entity = request.Phrase == null ? null : FromRecord(request.Phrase);
            return Mutate(request.Token, AdminRole.Creator, actor => SaveEntity(actor, Phrases, entity, create: true, prepare: null, reply: (r, e) => r.Phrase = ToRecord(e)));
        }

        public LibraryEditReply UpdateNeighborPhrase(SaveNeighborPhraseRequest request)
        {
            NeighborPhrase entity = request.Phrase == null ? null : FromRecord(request.Phrase);
            return Mutate(request.Token, AdminRole.Creator, actor => SaveEntity(actor, Phrases, entity, create: false, prepare: null, reply: (r, e) => r.Phrase = ToRecord(e)));
        }

        public LibraryEditReply DeleteNeighborPhrase(LibraryIdRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor => DeleteEntity(actor, Phrases, request.Id));
        }

        // Template edits bump the version (section 6.1): new = 1; update = stored + 1 when the text/handles part changed, else the stored value.
        // The client-sent version is never used.
        private static void BumpFeatureVersion(FeatureTemplate entity, FeatureTemplate stored)
        {
            if (stored == null) { entity.Version = 1; return; }
            bool changed = entity.Short != stored.Short || entity.Long != stored.Long || entity.RoomLine != stored.RoomLine || entity.Detail != stored.Detail
                || !entity.Handles.SequenceEqual(stored.Handles) || !entity.Adjectives.SequenceEqual(stored.Adjectives);
            entity.Version = changed ? stored.Version + 1 : stored.Version;
        }

        private LibraryEditReply SaveEntity<T>(Actor actor, LibraryKind<T> kind, T entity, bool create, Action<T, T> prepare, Action<LibraryEditReply, T> reply) where T : class
        {
            if (_library == null) { return LibraryReply(AdminResult.Refused, LibraryUnavailableMessage); }
            if (entity == null) { return LibraryReply(AdminResult.Invalid, null, new[] { new LibraryError("record", "The entity is missing.") }); }
            List<LibraryError> errors = kind.Validate(entity);
            if (errors.Count > 0) { return LibraryReply(AdminResult.Invalid, null, errors.ToArray()); }

            string id = kind.Id(entity);
            ILibraryCollection<T> collection = kind.Collection(_library);
            T stored;
            try { stored = collection.Get(id); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return LibraryFailed("read", kind, id, ex); }
            if (create && stored != null) { return LibraryReply(AdminResult.AlreadyExists, LibraryAlreadyExistsMessage); }
            if (!create && stored == null) { return LibraryReply(AdminResult.NotFound, LibraryNotFoundMessage); }
            if (prepare != null) { prepare(entity, stored); }

            BsonDocument after = kind.ToDocument(entity);
            var rows = new List<AuditChange>();
            if (create) { rows.Add(new AuditChange("*create*", BsonNull.Value, after)); }
            else
            {
                BsonDocument before = kind.ToDocument(stored);
                foreach (BsonElement element in after.Elements.Where(e => e.Name != "_id"))
                {
                    if (!before[element.Name].Equals(element.Value)) { rows.Add(new AuditChange(element.Name, before[element.Name], element.Value)); }
                }
                if (rows.Count == 0) { return LibraryReply(AdminResult.NoChange, LibraryNothingChangedMessage, null, CurrentVersion(), r => reply(r, stored)); }
            }

            List<AuditRecord> entries = BuildEntries(actor, kind.Entity, id, id, rows);
            if (!TryAudit(entries)) { return LibraryReply(AdminResult.AuditFailed, null); }

            bool written;
            int version;
            try
            {
                written = create ? collection.Insert(entity, actor.Player.PlayerName) : collection.Replace(entity, actor.Player.PlayerName);
                version = written ? _library.BumpVersion() : CurrentVersion();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Finish(entries, false);
                return LibraryFailed("write", kind, id, ex);
            }
            Finish(entries, written);
            // Someone else created or removed the id between our read and our write.
            if (!written) { return create ? LibraryReply(AdminResult.AlreadyExists, LibraryAlreadyExistsMessage) : LibraryReply(AdminResult.NotFound, LibraryNotFoundMessage); }
            RefreshLibraryCache();
            return LibraryReply(AdminResult.Ok, null, null, version, r => reply(r, entity));
        }

        private LibraryEditReply DeleteEntity<T>(Actor actor, LibraryKind<T> kind, string id) where T : class
        {
            if (_library == null) { return LibraryReply(AdminResult.Refused, LibraryUnavailableMessage); }
            id = id ?? string.Empty;
            ILibraryCollection<T> collection = kind.Collection(_library);
            T stored;
            try { stored = string.IsNullOrWhiteSpace(id) ? null : collection.Get(id); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return LibraryFailed("read", kind, id, ex); }
            if (stored == null) { return LibraryReply(AdminResult.NotFound, LibraryNotFoundMessage); }

            List<AuditRecord> entries = BuildEntries(actor, kind.Entity, id, id, new List<AuditChange> { new AuditChange("*delete*", kind.ToDocument(stored), BsonNull.Value) });
            if (!TryAudit(entries)) { return LibraryReply(AdminResult.AuditFailed, null); }

            bool deleted;
            int version;
            try
            {
                deleted = collection.Delete(id);
                version = deleted ? _library.BumpVersion() : CurrentVersion();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                Finish(entries, false);
                return LibraryFailed("delete", kind, id, ex);
            }
            Finish(entries, deleted);
            if (!deleted) { return LibraryReply(AdminResult.NotFound, LibraryNotFoundMessage); }
            RefreshLibraryCache();
            return LibraryReply(AdminResult.Ok, null, null, version);
        }

        private int CurrentVersion()
        {
            try { return _library.GetVersion() ?? 0; }
            catch (Exception ex) when (ex is not OutOfMemoryException) { return 0; }
        }

        // The generators read a cached snapshot; drop it after a write so the next run sees the edit.
        private void RefreshLibraryCache()
        {
            if (_libraryCache == null) { return; }
            try { _libraryCache.Refresh(); }
            catch (Exception ex) when (ex is not OutOfMemoryException) { _log("Library snapshot refresh failed (" + ex.GetType().Name + ")."); }
        }

        private LibraryEditReply LibraryFailed<T>(string what, LibraryKind<T> kind, string id, Exception ex) where T : class
        {
            // Generic result: no exception text, no values.
            _log("Library " + what + " of " + kind.Entity + " failed (" + ex.GetType().Name + ").");
            return LibraryReply(AdminResult.Failed, LibraryWriteFailedMessage);
        }

        private static LibraryEditReply LibraryReply(AdminResult result, string message, LibraryError[] errors = null, int version = 0, Action<LibraryEditReply> fill = null)
        {
            var reply = new LibraryEditReply { Result = result, Message = message ?? string.Empty, LibraryVersion = version };
            if (errors != null) { reply.Errors.AddRange(errors.Select(e => new FieldError { Field = e.Field, Message = e.Message })); }
            if (fill != null) { fill(reply); }
            return reply;
        }

        // ------------------------------------------------------------------ record <-> entity

        private static DescriptionFragment FromRecord(FragmentRecord r)
        {
            return new DescriptionFragment { Id = Trim(r.Id), Terrains = r.Terrains.ToList(), Slot = Trim(r.Slot), Text = r.Text, Weight = r.Weight, Tags = r.Tags.ToList() };
        }

        private static FeatureTemplate FromRecord(FeatureTemplateRecord r)
        {
            return new FeatureTemplate
            {
                Id = Trim(r.Id), Terrains = r.Terrains.ToList(), Short = r.Short, Long = r.Long, Handles = r.Handles.ToList(), Adjectives = r.Adjectives.ToList(),
                RoomLine = string.IsNullOrEmpty(r.RoomLine) ? null : r.RoomLine, Detail = string.IsNullOrEmpty(r.Detail) ? null : r.Detail,
                Weight = r.Weight, Chance = r.Chance, Version = 1, Tags = r.Tags.ToList(),
            };
        }

        private static NeighborPhrase FromRecord(NeighborPhraseRecord r)
        {
            return new NeighborPhrase { Id = Trim(r.Id), Terrains = r.Terrains.ToList(), NeighborTerrain = Trim(r.NeighborTerrain), Text = r.Text, Weight = r.Weight, Merge = r.Merge };
        }

        private static string Trim(string value) { return value == null ? null : value.Trim(); }

        public static FragmentRecord ToRecord(DescriptionFragment f)
        {
            var r = new FragmentRecord { Id = f.Id ?? string.Empty, Slot = f.Slot ?? string.Empty, Text = f.Text ?? string.Empty, Weight = f.Weight };
            r.Terrains.AddRange(f.Terrains ?? new List<string>());
            r.Tags.AddRange(f.Tags ?? new List<string>());
            return r;
        }

        public static FeatureTemplateRecord ToRecord(FeatureTemplate f)
        {
            var r = new FeatureTemplateRecord
            {
                Id = f.Id ?? string.Empty, Short = f.Short ?? string.Empty, Long = f.Long ?? string.Empty, RoomLine = f.RoomLine ?? string.Empty,
                Detail = f.Detail ?? string.Empty, Weight = f.Weight, Chance = f.Chance, Version = f.Version,
            };
            r.Terrains.AddRange(f.Terrains ?? new List<string>());
            r.Handles.AddRange(f.Handles ?? new List<string>());
            r.Adjectives.AddRange(f.Adjectives ?? new List<string>());
            r.Tags.AddRange(f.Tags ?? new List<string>());
            return r;
        }

        public static NeighborPhraseRecord ToRecord(NeighborPhrase p)
        {
            var r = new NeighborPhraseRecord { Id = p.Id ?? string.Empty, NeighborTerrain = p.NeighborTerrain ?? string.Empty, Text = p.Text ?? string.Empty, Weight = p.Weight, Merge = p.Merge };
            r.Terrains.AddRange(p.Terrains ?? new List<string>());
            return r;
        }
    }
}
