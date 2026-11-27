using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Components;
using MUD.Core.GameObjects;
using MUD.Core.Generation.Terrains;
using MUD.Core.Modules;
using MUD.Core.Repositories;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;

namespace MUD.Core.Administration
{
    /// <summary>Why an Admin call was refused before it did anything. The gRPC layer maps these to status codes.</summary>
    public enum AdminFailure
    {
        /// <summary>The token is empty, unknown or revoked, or its player is gone or retired.</summary>
        Unauthenticated,
        /// <summary>The token is good but the player's current role is too low for the call.</summary>
        Forbidden,
        /// <summary>The game thread did not run the call (the game is stopping, the queue is full, or it took too long).</summary>
        Unavailable
    }

    public sealed class AdminCallException : Exception
    {
        public AdminCallException(AdminFailure failure, string message) : base(message)
        {
            Failure = failure;
        }

        public AdminFailure Failure { get; private set; }
    }

    /// <summary>
    /// The Admin service logic of card P6-1 (owner decisions D27, D30, D31, D34). The web front end calls it through gRPC on frontends.sock
    /// instead of reading and writing the database itself.
    ///
    /// Sessions: WebAuthenticate checks the password, the retirement flag and the shared login throttle, and returns an opaque token. Every
    /// other call names the token. Core re-reads the player's CURRENT role on every call, so a demotion or a retirement takes effect at once;
    /// a retirement or a lowered role also revokes the player's sessions (D31). The session table is in memory and is lost on a restart.
    ///
    /// World change: everything that changes live game objects runs on the single game thread (World.TryRunOnGameThread), including the
    /// token check of that call. Listings read stored documents on the calling thread. Edits are last-write-wins (D30: no revision number)
    /// and audited per field, audit first: if the audit rows cannot be written the edit is refused (specs/WEB_EDITING.md section 15).
    /// Passwords are never logged, audited or returned.
    /// </summary>
    public sealed partial class AdminApi
    {
        public const string RoomUpdatedMessage = "Something feels different...";
        public const string RoomDeletedArrivalFormat = "A sudden draft of magical wind deposits {0} in the room!";
        public const string PlayerDescriptionChangedMessage = "Something about you feels different...";
        public const string PlayerRolesChangedMessage = "Your status has changed.";
        public const string DefaultRoomNotConfiguredMessage = "The default room is not configured, so rooms cannot be deleted.";
        public const string DefaultRoomCannotBeDeletedMessage = "The default room can never be deleted.";
        public const string NothingCouldBeMovedMessage = "This room has no usable exit and the default room does not exist, so its contents could not be moved. Nothing was deleted.";
        public const string ExitsLeftBehindMessage = "The room was deleted, but some exits that led to it could not be removed. They are harmless and can be removed by editing those rooms.";

        public const int DefaultPageSize = 25;
        public const int MaxPageSize = 200;

        private sealed class Actor
        {
            public AdminSession Session;
            public Player Player;
            public AdminRole Role;
        }

        private sealed class Outcome<T>
        {
            public AdminFailure? Failure;
            public string Detail;
            public T Value;
        }

        private readonly IAdminHost _host;
        private readonly IPlayerRepository _players;
        private readonly IRoomRepository _rooms;
        private readonly IAdminQueries _queries;
        private readonly IAuditStore _audit;
        private readonly AdminSessions _sessions;
        private readonly IClock _clock;
        private readonly Action<string> _log;
        private readonly Func<int, int> _random;
        private readonly IModuleControl _modules;
        private readonly string _contentDirectory;

        public AdminApi(IAdminHost host, IPlayerRepository players, IRoomRepository rooms, IAdminQueries queries, IAuditStore audit,
            AdminSessions sessions, IClock clock, Action<string> log, Func<int, int> random = null, IModuleControl modules = null,
            ILibraryStore library = null, StoreLibrary libraryCache = null, string contentDirectory = null, MUD.Core.Areas.IAreaStore areas = null)
        {
            _areas = areas;
            _contentDirectory = contentDirectory;
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _players = players ?? throw new ArgumentNullException(nameof(players));
            _rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
            _queries = queries ?? throw new ArgumentNullException(nameof(queries));
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _clock = clock ?? SystemClock.Instance;
            _log = log ?? (_ => { });
            _random = random ?? (max => new Random().Next(max));
            _modules = modules;
            _library = library;
            _libraryCache = libraryCache;
        }

        /// <summary>The session table, for tests and for the host that wants to know how many web users are signed in.</summary>
        public AdminSessions Sessions { get { return _sessions; } }

        // ------------------------------------------------------------------ sessions

        /// <summary>
        /// Checks the credentials and opens a session. Result is AuthOk (with a token), AuthBadCredentials, AuthRetired (right password, the
        /// character cannot be played), AuthThrottled or AuthTryAgainLater. The throttle is the one telnet uses, keyed by the address the web passes in (D43).
        /// </summary>
        public WebAuthReply WebAuthenticate(WebAuthRequest request)
        {
            string key = request.RemoteKey ?? string.Empty;
            if (_host.IsThrottled(key)) { return new WebAuthReply { Result = AuthResult.AuthThrottled }; }

            WebAuthReply reply;
            string name = request.Name ?? string.Empty;
            string password = request.Password ?? string.Empty;
            bool ran = _host.TryRunOnGameThread(() => Authenticate(name, password), out reply);
            if (!ran) { return new WebAuthReply { Result = AuthResult.AuthTryAgainLater }; }

            // A right password counts as a success for the throttle even for a retired character (the retirement is only shown after the password is right).
            _host.RecordLoginOutcome(key, reply.Result == AuthResult.AuthOk || reply.Result == AuthResult.AuthRetired);
            return reply;
        }

        private WebAuthReply Authenticate(string name, string password)
        {
            string nameKey = PlayerAccounts.Normalize(name);
            Player player = nameKey.Length == 0 ? null : FindByNameKey(nameKey);
            if (player != null)
            {
                // The cached live player can predate a change made elsewhere (the website still writes the database directly until its pages move to this service).
                player.SyncPasswordFromStore();
            }
            if (player == null || string.IsNullOrEmpty(player.Password) || !PasswordsEqual(player.Password, password))
            {
                return new WebAuthReply { Result = AuthResult.AuthBadCredentials };
            }
            if (player.Retired)
            {
                // Only reached with the right password, so this does not reveal anything to someone without it.
                _sessions.RevokeAllFor(player.Id);
                return new WebAuthReply { Result = AuthResult.AuthRetired };
            }
            AdminSession session = _sessions.Issue(player.Id, player.NameKey);
            return new WebAuthReply { Result = AuthResult.AuthOk, Token = session.Token, PlayerName = player.PlayerName, Role = ToContract(RoleOf(player)) };
        }

        /// <summary>Ends the session. Always succeeds, also for a token that is already gone.</summary>
        public Ack Logout(TokenRequest request)
        {
            _sessions.Revoke(request.Token);
            return new Ack { Ok = true };
        }

        /// <summary>The player name and CURRENT role behind the token.</summary>
        public SessionInfoReply GetSession(TokenRequest request)
        {
            Actor actor = Authorize(request.Token, AdminRole.Player);
            return new SessionInfoReply { PlayerName = actor.Player.PlayerName, Role = ToContract(actor.Role) };
        }

        /// <summary>The caller's own record (the player behind the token), read-only and without any credential.</summary>
        public OwnPlayerReply GetOwnPlayer(TokenRequest request)
        {
            Actor actor = Authorize(request.Token, AdminRole.Player);
            Player p = actor.Player;
            return new OwnPlayerReply
            {
                Name = p.PlayerName ?? string.Empty,
                Role = ToContract(actor.Role),
                ShortDescription = p.ShortDescription ?? string.Empty,
                LongDescription = p.LongDescription ?? string.Empty,
                Race = p.Race ?? string.Empty,
                Gender = p.Gender ?? string.Empty,
                Class = p.Class ?? string.Empty,
                Location = p.CurrentLocationId ?? string.Empty
            };
        }

        public ChangePasswordReply ChangePassword(ChangePasswordRequest request)
        {
            return Mutate(request.Token, AdminRole.Player, actor =>
            {
                string current = request.CurrentPassword ?? string.Empty;
                string replacement = request.NewPassword ?? string.Empty;
                Player player = actor.Player;
                player.SyncPasswordFromStore();
                // Wrong current is reported before any rule is revealed.
                if (string.IsNullOrEmpty(player.Password) || !PasswordsEqual(player.Password, current))
                {
                    return new ChangePasswordReply { Outcome = PasswordChangeOutcome.WrongCurrent };
                }
                string rule = PlayerAccounts.ValidatePassword(replacement);
                if (rule != null) { return new ChangePasswordReply { Outcome = PasswordChangeOutcome.Invalid, Message = rule }; }
                if (replacement != (request.ConfirmPassword ?? string.Empty)) { return new ChangePasswordReply { Outcome = PasswordChangeOutcome.Mismatch }; }
                if (PasswordsEqual(current, replacement)) { return new ChangePasswordReply { Outcome = PasswordChangeOutcome.Same }; }
                try
                {
                    return new ChangePasswordReply { Outcome = player.TryChangePassword(replacement) ? PasswordChangeOutcome.Changed : PasswordChangeOutcome.SaveFailed };
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    _log("Password change failed (" + ex.GetType().Name + ").");
                    return new ChangePasswordReply { Outcome = PasswordChangeOutcome.SaveFailed };
                }
            });
        }

        // ------------------------------------------------------------------ rooms (read)

        public ListRoomsReply ListRooms(TokenRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            var reply = new ListRoomsReply();
            ISnapshotRepository snapshots = _rooms as ISnapshotRepository;
            HashSet<string> cached = snapshots == null ? new HashSet<string>() : new HashSet<string>(snapshots.CachedIds(), StringComparer.Ordinal);
            foreach (BsonDocument document in _queries.ListRooms())
            {
                string key = document["_id"].ToString();
                // A room edited here may still be queued for the database; the live room is the truth.
                Room live = cached.Contains(key) ? _rooms.Get(key) : null;
                reply.Rooms.Add(live != null ? RoomFrom(live) : RoomFrom(document));
            }
            return reply;
        }

        public GetRoomReply GetRoom(GetRoomRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            Room room = string.IsNullOrWhiteSpace(request.Key) ? null : _rooms.Get(request.Key);
            return room == null ? new GetRoomReply { Found = false } : new GetRoomReply { Found = true, Room = RoomFrom(room) };
        }

        // ------------------------------------------------------------------ rooms (write)

        public RoomEditReply UpdateRoom(RoomEditRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor =>
            {
                try { return SaveRoom(actor, request); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { return RoomFailed("save", request.Key, ex); }
            });
        }

        private RoomEditReply SaveRoom(Actor actor, RoomEditRequest request)
        {
            string key = request.Key ?? string.Empty;
            Room live = string.IsNullOrWhiteSpace(key) ? null : _rooms.Get(key);
            if (live == null) { return RoomReply(AdminResult.NotFound); }

            AdminRoomValidator.Result checkedRoom = AdminRoomValidator.Validate(key, request.ShortDescription, request.LongDescription, request.Area, request.Exits, RoomExists);
            // Features are touched only when the caller says it sent them (features_present); an older caller leaves them alone.
            List<RoomFeature> newFeatures = null;
            if (request.FeaturesPresent)
            {
                AdminFeatureValidator.Result checkedFeatures = AdminFeatureValidator.Validate(request.Features);
                checkedRoom.Errors.AddRange(checkedFeatures.Errors);
                newFeatures = checkedFeatures.Features;
            }
            if (checkedRoom.Errors.Count > 0) { return RoomReply(AdminResult.Invalid, RoomFrom(live), checkedRoom.Errors); }
            FieldError dangling = DanglingError(checkedRoom, request.ConfirmDangling);
            if (dangling != null) { return RoomReply(AdminResult.Invalid, RoomFrom(live), new[] { dangling }); }

            int? x = Optional(request.HasX, request.X), y = Optional(request.HasY, request.Y), z = Optional(request.HasZ, request.Z);
            var set = new BsonDocument();
            var rows = new List<AuditChange>();
            DiffText(rows, set, "ShortDescription", live.ShortDescription, checkedRoom.Short);
            DiffText(rows, set, "LongDescription", live.LongDescription, checkedRoom.Long);
            DiffText(rows, set, "Area", live.Area ?? string.Empty, checkedRoom.Area);
            DiffNumber(rows, set, "X", live.X, x);
            DiffNumber(rows, set, "Y", live.Y, y);
            DiffNumber(rows, set, "Z", live.Z, z);
            BsonArray oldExits = ExitsToBson(live.Exits.Select(e => new RoomExit { Name = e.Name, To = e.DestinationId }));
            BsonArray newExits = ExitsToBson(checkedRoom.Exits);
            if (!oldExits.Equals(newExits)) { set["Exits"] = newExits; rows.Add(new AuditChange("Exits", oldExits, newExits)); }
            bool featuresChanged = false;
            if (newFeatures != null)
            {
                BsonArray oldFeatures = FeaturesToBson((live.Features ?? new List<Feature>()).Where(f => f != null).Select(FeatureToDetail));
                BsonArray newFeaturesBson = FeaturesToBson(newFeatures);
                if (!oldFeatures.Equals(newFeaturesBson))
                {
                    featuresChanged = true;
                    set["Features"] = MergedFeatureDocuments(live, newFeatures);
                    rows.Add(new AuditChange("Features", oldFeatures, newFeaturesBson));
                }
            }
            if (rows.Count == 0) { return RoomReply(AdminResult.NoChange, RoomFrom(live)); }

            List<AuditRecord> entries = BuildEntries(actor, "room", key, key, rows);
            if (!TryAudit(entries)) { return RoomReply(AdminResult.AuditFailed, RoomFrom(live)); }

            set["WebUpdatedAt"] = Now();
            bool saved = PersistRoomFields(live, set, () =>
            {
                live.ShortDescription = checkedRoom.Short;
                live.LongDescription = checkedRoom.Long;
                live.Area = checkedRoom.Area;
                live.X = x;
                live.Y = y;
                live.Z = z;
                live.Exits = checkedRoom.Exits.Select(e => new Exit { Name = e.Name, DestinationId = e.To }).ToList();
                live.RebuildExitCommands();
                if (featuresChanged) { ApplyFeatures(live, newFeatures); }
            });
            Finish(entries, saved);
            if (!saved) { return RoomReply(AdminResult.Failed, RoomFrom(live)); }
            live.TellRoom(RoomUpdatedMessage);
            return RoomReply(AdminResult.Ok, RoomFrom(live));
        }

        public RoomEditReply CreateRoom(RoomEditRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor =>
            {
                try { return MakeRoom(actor, request); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { return RoomFailed("create", request.Key, ex); }
            });
        }

        private RoomEditReply MakeRoom(Actor actor, RoomEditRequest request)
        {
            string key = (request.Key ?? string.Empty).Trim();
            AdminRoomValidator.Result checkedRoom = AdminRoomValidator.Validate(key, request.ShortDescription, request.LongDescription, request.Area, request.Exits, RoomExists);
            var errors = checkedRoom.Errors.ToList();
            if (key.IndexOf('.') < 0)
            {
                // New rooms are always authored: '<area prefix>.<local key>'.
                errors.Add(new FieldError { Field = "Key", Message = "A room key must look like area.room." });
            }
            else if (key.Length > RoomRules.MaxKey)
            {
                errors.Add(new FieldError { Field = "Key", Message = "The room key must be at most " + RoomRules.MaxKey + " characters." });
            }
            else if (checkedRoom.Area.Length > 0 && key.StartsWith(checkedRoom.Area + ".", StringComparison.Ordinal))
            {
                string localProblem = RoomRules.CheckLocalKey(key.Substring(checkedRoom.Area.Length + 1));
                if (localProblem != null) { errors.Add(new FieldError { Field = "Key", Message = char.ToUpperInvariant(localProblem[0]) + localProblem.Substring(1) }); }
            }
            // Backstop: the area must have a record (the website offers only existing areas).
            if (errors.Count == 0) { FieldError noArea = MissingAreaError(checkedRoom.Area, "Area"); if (noArea != null) { errors.Add(noArea); } }
            if (errors.Count > 0) { return RoomReply(AdminResult.Invalid, null, errors); }
            FieldError dangling = DanglingError(checkedRoom, request.ConfirmDangling);
            if (dangling != null) { return RoomReply(AdminResult.Invalid, null, new[] { dangling }); }
            if (RoomExists(key)) { return RoomReply(AdminResult.AlreadyExists); }

            int? x = Optional(request.HasX, request.X), y = Optional(request.HasY, request.Y), z = Optional(request.HasZ, request.Z);
            var room = new Room(_rooms)
            {
                Id = key,
                ShortDescription = checkedRoom.Short,
                LongDescription = checkedRoom.Long,
                Area = checkedRoom.Area,
                X = x,
                Y = y,
                Z = z,
                Exits = checkedRoom.Exits.Select(e => new Exit { Name = e.Name, DestinationId = e.To }).ToList()
            };
            var rows = new List<AuditChange>
            {
                new AuditChange("ShortDescription", BsonNull.Value, checkedRoom.Short),
                new AuditChange("LongDescription", BsonNull.Value, checkedRoom.Long),
                new AuditChange("Area", BsonNull.Value, checkedRoom.Area),
                new AuditChange("X", BsonNull.Value, ToBson(x)),
                new AuditChange("Y", BsonNull.Value, ToBson(y)),
                new AuditChange("Z", BsonNull.Value, ToBson(z)),
                new AuditChange("Exits", BsonNull.Value, ExitsToBson(checkedRoom.Exits)),
            };
            List<AuditRecord> entries = BuildEntries(actor, "room", key, key, rows);
            if (!TryAudit(entries)) { return RoomReply(AdminResult.AuditFailed); }

            bool saved;
            try
            {
                ISnapshotRepository snapshots = _rooms as ISnapshotRepository;
                if (snapshots != null && snapshots.Sink != null)
                {
                    BsonDocument whole = room.ToBsonDocument();
                    snapshots.Sink.Post(new PersistRequest { Collection = snapshots.CollectionName, Id = key, Kind = PersistKind.Replace, Doc = whole, Reason = "admin room create" });
                    saved = true;
                }
                else
                {
                    _rooms.Insert(room);
                    saved = true;
                }
            }
            catch (DuplicateKeyException)
            {
                Finish(entries, false);
                return RoomReply(AdminResult.AlreadyExists);
            }
            Finish(entries, saved);
            if (saved) { RegisterLiveRoom(room); }
            return saved ? RoomReply(AdminResult.Ok, RoomFrom(room)) : RoomReply(AdminResult.Failed);
        }

        public const string DefaultRoomCannotBeMovedMessage = "The default room can never be moved.";
        public const string MoveExitsLeftBehindMessage = "The room was moved, but some exits that led to it could not be updated. Edit those rooms to point at the new key.";

        public RoomEditReply MoveRoom(MoveRoomRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor =>
            {
                try { return RelocateRoom(actor, request); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { return RoomFailed("move", request.Key, ex); }
            });
        }

        // A move is "new key, same room": the room is copied under the new key, the old one is deleted, and everything that named the old key
        // (exits in other rooms, players' stored locations) is repointed. Players standing in it stay in it.
        private RoomEditReply RelocateRoom(Actor actor, MoveRoomRequest request)
        {
            string key = request.Key ?? string.Empty;
            string area = (request.NewArea ?? string.Empty).Trim();
            string local = (request.NewLocalKey ?? string.Empty).Trim();
            var errors = new List<FieldError>();
            string areaProblem = RoomRules.CheckAreaPrefix(area);
            if (areaProblem != null) { errors.Add(new FieldError { Field = "NewArea", Message = char.ToUpperInvariant(areaProblem[0]) + areaProblem.Substring(1) }); }
            string localProblem = RoomRules.CheckLocalKey(local);
            if (localProblem != null) { errors.Add(new FieldError { Field = "NewLocalKey", Message = char.ToUpperInvariant(localProblem[0]) + localProblem.Substring(1) }); }
            if (errors.Count == 0 && (area + "." + local).Length > RoomRules.MaxKey) { errors.Add(new FieldError { Field = "NewLocalKey", Message = "The room key must be at most " + RoomRules.MaxKey + " characters." }); }

            Room old = string.IsNullOrWhiteSpace(key) ? null : _rooms.Get(key);
            if (old == null) { return RoomReply(AdminResult.NotFound); }
            if (errors.Count == 0) { FieldError noArea = MissingAreaError(area, "NewArea"); if (noArea != null) { errors.Add(noArea); } }
            if (errors.Count > 0) { return RoomReply(AdminResult.Invalid, RoomFrom(old), errors); }
            string startKey = _host.StartRoomKey;
            if (startKey == null) { return RoomReply(AdminResult.Refused, RoomFrom(old), null, DefaultRoomNotConfiguredMessage); }
            if (string.Equals(key, startKey, StringComparison.Ordinal)) { return RoomReply(AdminResult.Refused, RoomFrom(old), null, DefaultRoomCannotBeMovedMessage); }
            string newKey = area + "." + local;
            if (string.Equals(newKey, key, StringComparison.Ordinal)) { return RoomReply(AdminResult.NoChange, RoomFrom(old)); }
            if (RoomExists(newKey)) { return RoomReply(AdminResult.AlreadyExists, RoomFrom(old)); }

            List<Room> referencing = _queries.RoomIdsWithExitTo(key).Where(id => id != key).Select(id => _rooms.Get(id)).Where(r => r != null).ToList();
            Func<IEnumerable<Exit>, List<Exit>> repoint = exits => exits.Select(e => new Exit { Name = e.Name, DestinationId = e.DestinationId == key ? newKey : e.DestinationId }).ToList();
            Func<IEnumerable<Exit>, BsonArray> asBson = exits => ExitsToBson(exits.Select(e => new RoomExit { Name = e.Name, To = e.DestinationId }));

            var entries = new List<AuditRecord>();
            entries.AddRange(BuildEntries(actor, "room", key, key, new List<AuditChange>
            {
                new AuditChange("Key", key, newKey),
                new AuditChange("Area", old.Area ?? string.Empty, area),
            }));
            var movedExits = repoint(old.Exits);
            if (old.Exits.Any(e => e.DestinationId == key))
            {
                entries.AddRange(BuildEntries(actor, "room", key, key, new List<AuditChange> { new AuditChange("Exits", asBson(old.Exits), asBson(movedExits)) }));
            }
            var newExitsByRoom = new Dictionary<string, List<Exit>>(StringComparer.Ordinal);
            foreach (Room other in referencing)
            {
                List<Exit> updated = repoint(other.Exits);
                newExitsByRoom[other.Id] = updated;
                entries.AddRange(BuildEntries(actor, "room", other.Id, other.Id, new List<AuditChange> { new AuditChange("Exits", asBson(other.Exits), asBson(updated)) }));
            }
            if (!TryAudit(entries)) { return RoomReply(AdminResult.AuditFailed, RoomFrom(old)); }

            var moved = new Room(_rooms)
            {
                Id = newKey,
                ShortDescription = old.ShortDescription,
                LongDescription = old.LongDescription,
                Plurals = old.Plurals,
                Adjectives = old.Adjectives,
                Handles = old.Handles,
                Components = old.Components,
                Behaviors = old.Behaviors,
                Search = old.Search,
                Area = area,
                X = old.X,
                Y = old.Y,
                Z = old.Z,
                Items = old.Items,
                Features = old.Features,
                Exits = movedExits,
            };
            try { _rooms.Insert(moved); }
            catch (DuplicateKeyException)
            {
                Finish(entries, false);
                return RoomReply(AdminResult.AlreadyExists, RoomFrom(old));
            }
            if (!_rooms.Delete(old))
            {
                // Nothing has moved yet: take the copy back out and leave the room as it was.
                _rooms.Delete(moved);
                ISnapshotRepository undo = _rooms as ISnapshotRepository;
                if (undo != null) { undo.Evict(newKey); }
                Finish(entries, false);
                return RoomReply(AdminResult.Failed, RoomFrom(old));
            }
            ISnapshotRepository snapshots = _rooms as ISnapshotRepository;
            if (snapshots != null) { snapshots.Evict(key); }
            MUD.Core.Events.EventDispatcher dispatcher = MUD.Core.Events.EventDispatcher.Current;
            if (dispatcher != null) { dispatcher.Heartbeats.Untrack(old); dispatcher.Heartbeats.Track(moved); }

            // Everyone inside stays inside, in the new object.
            List<Living> inside = old.Occupants.ToList();
            foreach (Living living in inside)
            {
                old.ExitRoom(living, null);
                living.CurrentLocation = moved;
                living.CurrentLocationId = newKey;
                moved.Occupants.Add(living);
            }
            foreach (Player player in inside.OfType<Player>()) { player.Save(true); }
            // Players who are not in the world keep their stored location.
            foreach (Player stored in _players.Search(p => p.CurrentLocationId == key).ToList())
            {
                stored.CurrentLocationId = newKey;
                _players.UpdateFields(stored.Id, new BsonDocument("CurrentLocationId", newKey));
            }

            // The insert and delete are the commit point. Exits in other rooms are repointed one document at a time (no transactions).
            var rejected = new List<string>();
            foreach (Room other in referencing)
            {
                List<Exit> updated = newExitsByRoom[other.Id];
                var set = new BsonDocument { { "Exits", asBson(updated) }, { "WebUpdatedAt", Now() } };
                bool done = PersistRoomFields(other, set, () => { other.Exits = updated; other.RebuildExitCommands(); });
                if (!done) { rejected.AddRange(entries.Where(e => e.EntityId == other.Id).Select(e => e.Id)); }
            }
            moved.RebuildExitCommands();
            TrySetOutcome(entries.Select(e => e.Id).Except(rejected).ToList(), true);
            TrySetOutcome(rejected, false);
            return rejected.Count == 0 ? RoomReply(AdminResult.Ok, RoomFrom(moved)) : RoomReply(AdminResult.Ok, RoomFrom(moved), null, MoveExitsLeftBehindMessage);
        }

        public RoomEditReply DeleteRoom(DeleteRoomRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor =>
            {
                try { return RemoveRoom(actor, request.Key ?? string.Empty); }
                catch (Exception ex) when (ex is not OutOfMemoryException) { return RoomFailed("delete", request.Key, ex); }
            });
        }

        private RoomEditReply RemoveRoom(Actor actor, string key)
        {
            string startKey = _host.StartRoomKey;
            if (startKey == null) { return RoomReply(AdminResult.Refused, null, null, DefaultRoomNotConfiguredMessage); }
            if (string.Equals(key, startKey, StringComparison.Ordinal)) { return RoomReply(AdminResult.Refused, null, null, DefaultRoomCannotBeDeletedMessage); }
            Room dead = string.IsNullOrWhiteSpace(key) ? null : _rooms.Get(key);
            if (dead == null) { return RoomReply(AdminResult.NotFound); }

            // Everything inside is moved to a random usable neighbour, else to the default room. If neither exists nothing could be moved, so refuse first.
            Room destination = ChooseDestination(dead, startKey);
            if (destination == null) { return RoomReply(AdminResult.Refused, RoomFrom(dead), null, NothingCouldBeMovedMessage); }

            List<Room> referencing = _queries.RoomIdsWithExitTo(key).Where(id => id != key).Select(id => _rooms.Get(id)).Where(r => r != null).ToList();

            var entries = new List<AuditRecord>();
            entries.AddRange(BuildEntries(actor, "room", key, key, new List<AuditChange>
            {
                new AuditChange("ShortDescription", dead.ShortDescription ?? string.Empty, BsonNull.Value),
                new AuditChange("LongDescription", dead.LongDescription ?? string.Empty, BsonNull.Value),
                new AuditChange("Area", dead.Area ?? string.Empty, BsonNull.Value),
                new AuditChange("X", ToBson(dead.X), BsonNull.Value),
                new AuditChange("Y", ToBson(dead.Y), BsonNull.Value),
                new AuditChange("Z", ToBson(dead.Z), BsonNull.Value),
                new AuditChange("Exits", ExitsToBson(dead.Exits.Select(e => new RoomExit { Name = e.Name, To = e.DestinationId })), BsonNull.Value),
            }));
            var keptByRoom = new Dictionary<string, List<Exit>>(StringComparer.Ordinal);
            foreach (Room other in referencing)
            {
                List<Exit> kept = other.Exits.Where(e => e.DestinationId != key).ToList();
                keptByRoom[other.Id] = kept;
                entries.AddRange(BuildEntries(actor, "room", other.Id, other.Id, new List<AuditChange>
                {
                    new AuditChange("Exits", ExitsToBson(other.Exits.Select(e => new RoomExit { Name = e.Name, To = e.DestinationId })), ExitsToBson(kept.Select(e => new RoomExit { Name = e.Name, To = e.DestinationId }))),
                }));
            }
            if (!TryAudit(entries)) { return RoomReply(AdminResult.AuditFailed, RoomFrom(dead)); }

            MoveContents(dead, destination);
            if (!_rooms.Delete(dead))
            {
                Finish(entries, false);
                return RoomReply(AdminResult.Failed, RoomFrom(dead));
            }
            ISnapshotRepository snapshots = _rooms as ISnapshotRepository;
            if (snapshots != null) { snapshots.Evict(key); }

            // The delete is the commit point. Exits that pointed at the room are removed afterwards, one document at a time (no transactions);
            // a failure there leaves a dangling exit, which the game already guards against.
            var rejected = new List<string>();
            foreach (Room other in referencing)
            {
                List<Exit> kept = keptByRoom[other.Id];
                var set = new BsonDocument { { "Exits", ExitsToBson(kept.Select(e => new RoomExit { Name = e.Name, To = e.DestinationId })) }, { "WebUpdatedAt", Now() } };
                bool stripped = PersistRoomFields(other, set, () => { other.Exits = kept; other.RebuildExitCommands(); });
                if (!stripped) { rejected.AddRange(entries.Where(e => e.EntityId == other.Id).Select(e => e.Id)); }
            }
            TrySetOutcome(entries.Select(e => e.Id).Except(rejected).ToList(), true);
            TrySetOutcome(rejected, false);
            return rejected.Count == 0 ? RoomReply(AdminResult.Ok) : RoomReply(AdminResult.Ok, null, null, ExitsLeftBehindMessage);
        }

        // Everything in the room goes to the destination; players there are shown the new room and saved so the location sticks.
        private void MoveContents(Room dead, Room destination)
        {
            List<Living> movedLivings = dead.Occupants.ToList();
            List<InventoryItem> movedItems = dead.Items.ToList();
            foreach (Living living in movedLivings)
            {
                dead.ExitRoom(living, null);
                living.CurrentLocation = destination;
                living.CurrentLocationId = destination.Id;
                destination.Occupants.Add(living);
            }
            if (movedItems.Count > 0)
            {
                destination.Items.AddRange(movedItems);
                dead.Items.Clear();
                destination.SaveState();
            }
            List<string> names = movedLivings.Select(l => l.ShortDescription).Concat(movedItems.Select(i => i.ShortDescription)).ToList();
            if (names.Count > 0)
            {
                destination.TellRoom(string.Format(RoomDeletedArrivalFormat, MUD.Core.Formatting.StringExtensions.GetListText(names)));
            }
            foreach (Player player in movedLivings.OfType<Player>())
            {
                player.ReceiveMessage(destination.Examine(player));
                player.Save(true);
            }
        }

        private Room ChooseDestination(Room dead, string startKey)
        {
            var neighbours = new List<Room>();
            foreach (Exit exit in dead.Exits ?? new List<Exit>())
            {
                if (exit == null || string.IsNullOrEmpty(exit.DestinationId) || exit.DestinationId == dead.Id) { continue; }
                if (neighbours.Any(n => n.Id == exit.DestinationId)) { continue; }
                Room room = _rooms.Get(exit.DestinationId);
                if (room != null) { neighbours.Add(room); }
            }
            if (neighbours.Count > 0) { return neighbours[_random(neighbours.Count)]; }
            if (string.IsNullOrWhiteSpace(startKey) || startKey == dead.Id) { return null; }
            return _rooms.Get(startKey);
        }

        // ------------------------------------------------------------------ players

        public ListPlayersReply ListPlayers(ListPlayersRequest request)
        {
            Authorize(request.Token, AdminRole.Admin);
            int pageSize = request.PageSize <= 0 ? DefaultPageSize : Math.Min(request.PageSize, MaxPageSize);
            int page = Math.Max(1, request.Page);
            int total;
            List<BsonDocument> documents = _queries.ListPlayers(request.NameFilter, request.Descending, page, pageSize, out total);
            var reply = new ListPlayersReply { Total = total, Page = page, PageSize = pageSize };
            foreach (BsonDocument document in documents) { reply.Items.Add(SummaryFrom(document)); }
            return reply;
        }

        public GetPlayerReply GetPlayer(GetPlayerRequest request)
        {
            Authorize(request.Token, AdminRole.Admin);
            Player player = FindByNameKey(Normalize(request.NameKey));
            return player == null ? new GetPlayerReply { Found = false } : new GetPlayerReply { Found = true, Player = DetailFrom(player) };
        }

        public UpdatePlayerReply UpdatePlayer(UpdatePlayerRequest request)
        {
            return Mutate(request.Token, AdminRole.Admin, actor =>
            {
                try { return SavePlayer(actor, request); }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // Generic result: no exception text, no values.
                    _log("Player save failed (" + ex.GetType().Name + ").");
                    return new UpdatePlayerReply { Result = AdminResult.Failed };
                }
            });
        }

        private UpdatePlayerReply SavePlayer(Actor actor, UpdatePlayerRequest request)
        {
            Player target = FindByNameKey(Normalize(request.NameKey));
            if (target == null) { return new UpdatePlayerReply { Result = AdminResult.NotFound }; }
            AdminRole targetRole = RoleOf(target);
            PlayerDetail current = DetailFrom(target);

            // Defensive last-admin guard, ahead of the generic rule so it is reported as such.
            if (request.HasRole && AdminPlayerRules.WouldRemoveLastAdmin(targetRole, request.Role, _queries.CountAdmins()))
            {
                return new UpdatePlayerReply { Result = AdminResult.LastAdmin, Player = current };
            }
            if (!AdminPlayerRules.CanEdit(actor.Role, targetRole)) { return new UpdatePlayerReply { Result = AdminResult.Forbidden, Player = current }; }

            AdminRole? roleChange = request.HasRole && request.Role != AdminRole.Unspecified && request.Role != targetRole ? request.Role : (AdminRole?)null;
            if (roleChange.HasValue && !AdminPlayerRules.CanSetRole(actor.Role, targetRole, roleChange.Value)) { return new UpdatePlayerReply { Result = AdminResult.Forbidden, Player = current }; }
            bool? retiredChange = request.HasRetired && request.Retired != target.Retired ? request.Retired : (bool?)null;
            if (retiredChange.HasValue && !AdminPlayerRules.CanSetRetired(actor.Role, targetRole)) { return new UpdatePlayerReply { Result = AdminResult.Forbidden, Player = current }; }

            AdminPlayerValidator.Result checkedPlayer = AdminPlayerValidator.Validate(request.ShortDescription, request.LongDescription, request.Race, request.Gender, request.Class);
            if (checkedPlayer.Errors.Count > 0)
            {
                var invalid = new UpdatePlayerReply { Result = AdminResult.Invalid, Player = current };
                invalid.Errors.AddRange(checkedPlayer.Errors);
                return invalid;
            }

            var set = new BsonDocument();
            var rows = new List<AuditChange>();
            DiffOptionalText(rows, set, PlayerFields.ShortDescription, target.ShortDescription, checkedPlayer.Short);
            DiffOptionalText(rows, set, PlayerFields.LongDescription, target.LongDescription, checkedPlayer.Long);
            DiffOptionalText(rows, set, PlayerFields.Race, target.Race, checkedPlayer.Race);
            DiffOptionalText(rows, set, PlayerFields.Gender, target.Gender, checkedPlayer.Gender);
            DiffOptionalText(rows, set, PlayerFields.Class, target.Class, checkedPlayer.Class);
            bool textChanged = rows.Count > 0;
            if (retiredChange.HasValue)
            {
                set[PlayerFields.Retired] = retiredChange.Value;
                rows.Add(new AuditChange(PlayerFields.Retired, target.Retired, retiredChange.Value));
            }
            Roles newRoles = null;
            if (roleChange.HasValue)
            {
                newRoles = new Roles { Creator = roleChange.Value >= AdminRole.Creator, Admin = roleChange.Value == AdminRole.Admin, CurrentDirectory = target.Roles.CurrentDirectory };
                set[PlayerFields.Roles] = newRoles.ToBsonDocument();
                rows.Add(new AuditChange("Role", targetRole.ToString(), roleChange.Value.ToString()));
            }
            if (rows.Count == 0) { return new UpdatePlayerReply { Result = AdminResult.NoChange, Player = current }; }

            List<AuditRecord> entries = BuildEntries(actor, "player", target.Id, target.PlayerName, rows);
            if (!TryAudit(entries)) { return new UpdatePlayerReply { Result = AdminResult.AuditFailed, Player = current }; }

            set[PlayerFields.WebUpdatedAt] = Now();
            bool written = _players.UpdateFields(target.Id, set);
            Finish(entries, written);
            if (!written) { return new UpdatePlayerReply { Result = AdminResult.Failed, Player = current }; }

            // Persisted; now the live object follows. Player.Save never writes these fields, so a later save cannot undo the edit.
            if (textChanged)
            {
                target.ShortDescription = checkedPlayer.Short;
                target.LongDescription = checkedPlayer.Long;
                target.Race = checkedPlayer.Race;
                target.Gender = checkedPlayer.Gender;
                target.Class = checkedPlayer.Class;
            }
            if (retiredChange.HasValue) { target.Retired = retiredChange.Value; }
            if (newRoles != null)
            {
                target.Roles.Creator = newRoles.Creator;
                target.Roles.Admin = newRoles.Admin;
            }

            bool online = _host.IsOnline(target);
            if (retiredChange == true)
            {
                // Retirement ends every web session at once, and an online player is told and logged out through the normal quit and save path.
                _sessions.RevokeAllFor(target.Id);
                if (online)
                {
                    target.ReceiveMessage(PlayerAccounts.RetiredMessage);
                    target.Quit();
                }
            }
            else if (online)
            {
                if (textChanged) { target.ReceiveMessage(PlayerDescriptionChangedMessage); }
                if (roleChange.HasValue) { target.ReceiveMessage(PlayerRolesChangedMessage); }
            }
            // A demotion ends the player's web sessions too (D31); the role is also re-read on every call, so nothing waits for this.
            if (roleChange.HasValue && roleChange.Value < targetRole) { _sessions.RevokeAllFor(target.Id); }
            return new UpdatePlayerReply { Result = AdminResult.Ok, Player = DetailFrom(target) };
        }

        // ------------------------------------------------------------------ audit

        public ListAuditReply ListAudit(ListAuditRequest request)
        {
            Authorize(request.Token, AdminRole.Admin);
            int pageSize = request.PageSize <= 0 ? DefaultPageSize : Math.Min(request.PageSize, MaxPageSize);
            int page = Math.Max(1, request.Page);
            AuditPage result = _audit.Query(request.Entity, request.EntityId, page, pageSize);
            var reply = new ListAuditReply { Total = result.Total, Page = page, PageSize = pageSize };
            foreach (AuditRecord row in result.Items)
            {
                reply.Items.Add(new AuditRow
                {
                    Id = row.Id,
                    AtUnixMs = new DateTimeOffset(DateTime.SpecifyKind(row.At, DateTimeKind.Utc)).ToUnixTimeMilliseconds(),
                    ActorId = row.ActorId ?? string.Empty,
                    ActorName = row.ActorName ?? string.Empty,
                    ActorRole = row.ActorRole ?? string.Empty,
                    Entity = row.Entity ?? string.Empty,
                    EntityId = row.EntityId ?? string.Empty,
                    EntityName = row.EntityName ?? string.Empty,
                    Field = row.Field ?? string.Empty,
                    OldValue = Render(row.Old),
                    NewValue = Render(row.New),
                    Source = row.Source ?? string.Empty,
                    Applied = row.Applied,
                    Rejected = row.Rejected
                });
            }
            return reply;
        }

        // ------------------------------------------------------------------ modules

        public const string ModuleNotFoundMessage = "No module with that name is known to core.";
        public const string ModulesUnavailableMessage = "The module supervisor is not running.";
        public const string ModuleAlreadyRunningMessage = "The module is already running or starting; nothing was changed.";
        public const string ModuleAlreadyStoppedMessage = "The module is already stopped; nothing was changed.";
        public const string ModuleAlreadyStoppingMessage = "The module is already stopping; nothing was changed.";
        public const string ModuleStartQueuedMessage = "Start requested. The module changes state shortly; refresh the list to see it.";
        public const string ModuleStopQueuedMessage = "Stop requested. The module changes state shortly; refresh the list to see it.";
        public const string ModuleRestartQueuedMessage = "Restart requested. The module changes state shortly; refresh the list to see it.";

        /// <summary>Every module the supervisor knows (directory discovery, D67), running or not. Creator or admin.</summary>
        public ListModulesReply ListModules(TokenRequest request)
        {
            Authorize(request.Token, AdminRole.Creator);
            var reply = new ListModulesReply();
            IReadOnlyList<ModuleStatus> statuses = _modules == null ? null : _modules.GetStatuses();
            if (statuses != null) { reply.Modules.AddRange(statuses.Select(ModuleInfoFrom)); }
            return reply;
        }

        public ModuleActionReply StartModule(ModuleRequest request)
        {
            return ControlModule(request, "start");
        }

        public ModuleActionReply StopModule(ModuleRequest request)
        {
            return ControlModule(request, "stop");
        }

        public ModuleActionReply RestartModule(ModuleRequest request)
        {
            return ControlModule(request, "restart");
        }

        // Admin only (a stopped module changes the whole game). The role check and the request both run on the game thread; the supervisor
        // itself only queues the work for its own thread, where the process work happens. Audit first: no row, no request.
        private ModuleActionReply ControlModule(ModuleRequest request, string action)
        {
            string name = request.Name ?? string.Empty;
            return Mutate(request.Token, AdminRole.Admin, actor =>
            {
                if (_modules == null) { return ModuleReply(AdminResult.Refused, ModulesUnavailableMessage, null); }
                IReadOnlyList<ModuleStatus> all = _modules.GetStatuses();
                if (all == null) { return ModuleReply(AdminResult.Refused, ModulesUnavailableMessage, null); }
                ModuleStatus status = all.FirstOrDefault(s => string.Equals(s.Name, name, StringComparison.Ordinal));
                if (status == null) { return ModuleReply(AdminResult.NotFound, ModuleNotFoundMessage, null); }

                bool starting = action == "start";
                bool restarting = action == "restart";
                // Restart of a stopped module is a start (the supervisor does the same), so it gets the start's checks and is audited as one.
                if (restarting && status.State == ModuleState.Stopped) { starting = true; action = "start"; restarting = false; }
                if (restarting)
                {
                    if (status.State == ModuleState.Stopping) { return ModuleReply(AdminResult.Refused, ModuleAlreadyStoppingMessage, status); }
                    // A restart ends in a start when the module is not Ready, so a module that cannot start is refused here too.
                    if (!string.IsNullOrEmpty(status.Problem) || status.FolderMissing) { return ModuleReply(AdminResult.Refused, "The module cannot be restarted: " + (status.Problem ?? "its folder is gone") + ".", status); }
                }
                else if (starting)
                {
                    if (status.State == ModuleState.Starting || status.State == ModuleState.Connected || status.State == ModuleState.Ready) { return ModuleReply(AdminResult.NoChange, ModuleAlreadyRunningMessage, status); }
                    if (status.State == ModuleState.Stopping) { return ModuleReply(AdminResult.Refused, ModuleAlreadyStoppingMessage, status); }
                    // The supervisor logs and ignores a start for a module it cannot start (no executable, bad --describe, contract mismatch).
                    if (!string.IsNullOrEmpty(status.Problem) || status.FolderMissing) { return ModuleReply(AdminResult.Refused, "The module cannot be started: " + (status.Problem ?? "its folder is gone") + ".", status); }
                }
                else
                {
                    if (status.State == ModuleState.Stopped) { return ModuleReply(AdminResult.NoChange, ModuleAlreadyStoppedMessage, status); }
                    if (status.State == ModuleState.Stopping) { return ModuleReply(AdminResult.NoChange, ModuleAlreadyStoppingMessage, status); }
                }

                List<AuditRecord> entries = BuildEntries(actor, "module", status.Name, status.Name, new List<AuditChange>
                {
                    new AuditChange("Action", BsonNull.Value, action),
                });
                if (!TryAudit(entries)) { return ModuleReply(AdminResult.AuditFailed, null, status); }

                string answer = restarting ? _modules.RequestRestart(status.Name) : starting ? _modules.RequestStart(status.Name) : _modules.RequestStop(status.Name);
                bool queued = answer != null;
                Finish(entries, queued);
                if (!queued) { return ModuleReply(AdminResult.Refused, ModulesUnavailableMessage, status); }
                return ModuleReply(AdminResult.Ok, restarting ? ModuleRestartQueuedMessage : starting ? ModuleStartQueuedMessage : ModuleStopQueuedMessage, status);
            });
        }

        private static ModuleActionReply ModuleReply(AdminResult result, string message, ModuleStatus status)
        {
            var reply = new ModuleActionReply { Result = result, Message = message ?? string.Empty };
            if (status != null) { reply.Module = ModuleInfoFrom(status); }
            return reply;
        }

        public static ModuleInfo ModuleInfoFrom(ModuleStatus status)
        {
            string failure = !string.IsNullOrEmpty(status.Problem) ? status.Problem
                : status.State == ModuleState.Refused && !string.IsNullOrEmpty(status.RefusalReason) ? "core refused it: " + status.RefusalReason
                : status.Detail;
            return new ModuleInfo
            {
                Name = status.Name ?? string.Empty,
                Version = status.Version ?? string.Empty,
                State = (ModuleRunState)((int)status.State + 1),
                Running = status.State == ModuleState.Connected || status.State == ModuleState.Ready,
                Required = status.Required,
                LastFailure = failure ?? string.Empty,
                FolderMissing = status.FolderMissing
            };
        }

        // ------------------------------------------------------------------ authorization

        // Checks the token against the CURRENT state of the player (D31). A session whose player is gone or retired is ended.
        private Actor Authorize(string token, AdminRole minimum)
        {
            AdminSession session = _sessions.Find(token);
            if (session == null) { throw new AdminCallException(AdminFailure.Unauthenticated, "The session is not valid."); }
            Player player = _players.Get(session.PlayerId);
            if (player == null || player.Retired)
            {
                _sessions.Revoke(session.Token);
                throw new AdminCallException(AdminFailure.Unauthenticated, "The session is not valid.");
            }
            AdminRole role = RoleOf(player);
            if (role < minimum) { throw new AdminCallException(AdminFailure.Forbidden, "The current role does not allow this."); }
            return new Actor { Session = session, Player = player, Role = role };
        }

        // Runs a world-changing call on the game thread, with the token check inside the same hop so the role it acts on is the role at that moment.
        private T Mutate<T>(string token, AdminRole minimum, Func<Actor, T> work)
        {
            Outcome<T> outcome;
            bool ran = _host.TryRunOnGameThread(() =>
            {
                try { return new Outcome<T> { Value = work(Authorize(token, minimum)) }; }
                catch (AdminCallException ex) { return new Outcome<T> { Failure = ex.Failure, Detail = ex.Message }; }
            }, out outcome);
            if (!ran || outcome == null) { throw new AdminCallException(AdminFailure.Unavailable, "The game did not run the call. Try again."); }
            if (outcome.Failure.HasValue) { throw new AdminCallException(outcome.Failure.Value, outcome.Detail); }
            return outcome.Value;
        }

        public static AdminRole RoleOf(Player player)
        {
            if (player.Roles.Admin) { return AdminRole.Admin; }
            if (player.Roles.Creator) { return AdminRole.Creator; }
            return AdminRole.Player;
        }

        // ------------------------------------------------------------------ helpers

        private sealed class AuditChange
        {
            public AuditChange(string field, BsonValue oldValue, BsonValue newValue)
            {
                Field = field;
                Old = oldValue;
                New = newValue;
            }

            public string Field;
            public BsonValue Old;
            public BsonValue New;
        }

        private Player FindByNameKey(string nameKey)
        {
            if (string.IsNullOrEmpty(nameKey)) { return null; }
            return _players.Search(p => p.NameKey == nameKey).FirstOrDefault();
        }

        private static string Normalize(string nameKey)
        {
            return PlayerAccounts.Normalize(nameKey);
        }

        private bool RoomExists(string key)
        {
            return !string.IsNullOrWhiteSpace(key) && (_rooms.Get(key) != null || _rooms.GetRawDocument(key) != null);
        }

        private DateTime Now()
        {
            return _clock.Now.ToUniversalTime();
        }

        // Puts a newly created room into the live set so the next lookup finds the same object.
        private void RegisterLiveRoom(Room room)
        {
            // Repository.Insert already caches the instance; with a sink the queued write is not cached yet, so a later Get loads it from the database.
        }

        // The edit reaches the database through the persistence worker when there is one (so writes of one room stay in order), else directly.
        // apply changes the live room; with a direct write it runs only after the database accepted the change.
        private bool PersistRoomFields(Room room, BsonDocument set, Action apply)
        {
            ISnapshotRepository snapshots = _rooms as ISnapshotRepository;
            if (snapshots != null && snapshots.Sink != null)
            {
                apply();
                snapshots.Sink.Post(new PersistRequest
                {
                    Collection = snapshots.CollectionName,
                    Id = room.Id,
                    Kind = PersistKind.SetFields,
                    Doc = set,
                    Fallback = room.ToBsonDocument(),
                    Reason = "admin room edit"
                });
                return true;
            }
            if (!_rooms.UpdateFields(room.Id, set)) { return false; }
            apply();
            return true;
        }

        // ------------------------------------------------------------------ room features

        // A live Feature as the web sees it: only the fields the web can represent. Never includes behaviors or components.
        private static RoomFeature FeatureToDetail(Feature feature)
        {
            var detail = new RoomFeature
            {
                Short = feature.ShortDescription ?? string.Empty,
                Long = feature.LongDescription ?? string.Empty,
                RoomLine = feature.RoomLine ?? string.Empty,
                Detail = feature.DetailDescription ?? string.Empty
            };
            detail.Handles.AddRange((feature.Handles ?? new List<string>()).Where(w => w != null));
            detail.Adjectives.AddRange((feature.Adjectives ?? new List<string>()).Where(w => w != null));
            detail.Plurals.AddRange((feature.Plurals ?? new List<string>()).Where(w => w != null));
            detail.TemplateId = feature.TemplateId ?? string.Empty;
            detail.TemplateVersion = feature.TemplateVersion ?? 0;
            return detail;
        }

        public static BsonArray FeaturesToBson(IEnumerable<RoomFeature> features)
        {
            return new BsonArray(features.Select(f => new BsonDocument
            {
                { "Short", f.Short },
                { "Long", f.Long },
                { "RoomLine", string.IsNullOrEmpty(f.RoomLine) ? (BsonValue)BsonNull.Value : f.RoomLine },
                { "Detail", string.IsNullOrEmpty(f.Detail) ? (BsonValue)BsonNull.Value : f.Detail },
                { "Handles", new BsonArray(f.Handles) },
                { "Adjectives", new BsonArray(f.Adjectives) },
                { "Plurals", new BsonArray(f.Plurals) }
            }));
        }

        private static Feature MatchingFeature(Room room, string shortText)
        {
            return (room.Features ?? new List<Feature>()).FirstOrDefault(f => f != null && string.Equals(f.ShortDescription, shortText, StringComparison.OrdinalIgnoreCase));
        }

        // The stored Features array after the edit. A feature that matches a stored one by short keeps everything the web cannot
        // represent (id, behaviors, components): only the text and word fields are overwritten in a copy of its stored form.
        private static BsonArray MergedFeatureDocuments(Room room, List<RoomFeature> wanted)
        {
            var merged = new BsonArray();
            foreach (RoomFeature feature in wanted)
            {
                Feature existing = MatchingFeature(room, feature.Short);
                BsonDocument doc = existing != null ? existing.ToBsonDocument() : new Feature { Id = room.Id + "#" + feature.Short.ToLowerInvariant() }.ToBsonDocument();
                doc["ShortDescription"] = feature.Short;
                doc["LongDescription"] = feature.Long;
                doc["RoomLine"] = string.IsNullOrEmpty(feature.RoomLine) ? (BsonValue)BsonNull.Value : feature.RoomLine;
                doc["DetailDescription"] = string.IsNullOrEmpty(feature.Detail) ? (BsonValue)BsonNull.Value : feature.Detail;
                doc["Handles"] = new BsonArray(feature.Handles);
                doc["Adjectives"] = new BsonArray(feature.Adjectives);
                doc["Plurals"] = new BsonArray(feature.Plurals);
                merged.Add(doc);
            }
            return merged;
        }

        // The live room gets the same result as MergedFeatureDocuments: matched Feature objects are kept (so their behaviors and
        // components stay) and have their text and word fields set; new ones are added; the list order is the requested order.
        // Items and occupants are never touched.
        private static void ApplyFeatures(Room room, List<RoomFeature> wanted)
        {
            var result = new List<Feature>();
            foreach (RoomFeature feature in wanted)
            {
                Feature live = MatchingFeature(room, feature.Short) ?? new Feature { Id = room.Id + "#" + feature.Short.ToLowerInvariant() };
                live.ShortDescription = feature.Short;
                live.LongDescription = feature.Long;
                live.RoomLine = string.IsNullOrEmpty(feature.RoomLine) ? null : feature.RoomLine;
                live.DetailDescription = string.IsNullOrEmpty(feature.Detail) ? null : feature.Detail;
                live.Handles = feature.Handles.ToList();
                live.Adjectives = feature.Adjectives.ToList();
                live.Plurals = feature.Plurals.ToList();
                result.Add(live);
            }
            room.Features = result;
        }

        private static FieldError DanglingError(AdminRoomValidator.Result checkedRoom, bool confirmed)
        {
            if (checkedRoom.Dangling.Count == 0 || confirmed) { return null; }
            return new FieldError
            {
                Field = "ConfirmDangling",
                Message = "Some exits lead to rooms that do not exist (" + string.Join(", ", checkedRoom.Dangling) + "). Confirm to save them anyway."
            };
        }

        private static int? Optional(bool has, int value)
        {
            return has ? value : (int?)null;
        }

        private static BsonValue ToBson(int? value)
        {
            return value.HasValue ? new BsonInt32(value.Value) : (BsonValue)BsonNull.Value;
        }

        public static BsonArray ExitsToBson(IEnumerable<RoomExit> exits)
        {
            return new BsonArray(exits.Select(e => new BsonDocument { { "Name", e.Name }, { "DestinationId", e.To } }));
        }

        private static void DiffText(List<AuditChange> rows, BsonDocument set, string field, string oldValue, string newValue)
        {
            if (string.Equals(oldValue, newValue, StringComparison.Ordinal)) { return; }
            set[field] = newValue;
            rows.Add(new AuditChange(field, oldValue ?? string.Empty, newValue));
        }

        // Blank is null (unplaced), never 0.
        private static void DiffNumber(List<AuditChange> rows, BsonDocument set, string field, int? oldValue, int? newValue)
        {
            if (oldValue == newValue) { return; }
            set[field] = ToBson(newValue);
            rows.Add(new AuditChange(field, ToBson(oldValue), ToBson(newValue)));
        }

        // null and empty are the same "unset" value.
        private static void DiffOptionalText(List<AuditChange> rows, BsonDocument set, string field, string oldValue, string newValue)
        {
            string before = string.IsNullOrEmpty(oldValue) ? null : oldValue;
            string after = string.IsNullOrEmpty(newValue) ? null : newValue;
            if (string.Equals(before, after, StringComparison.Ordinal)) { return; }
            BsonValue newBson = after == null ? (BsonValue)BsonNull.Value : after;
            set[field] = newBson;
            rows.Add(new AuditChange(field, before == null ? (BsonValue)BsonNull.Value : before, newBson));
        }

        private List<AuditRecord> BuildEntries(Actor actor, string entity, string entityId, string entityName, IEnumerable<AuditChange> rows)
        {
            DateTime now = Now();
            return rows.Select(r => new AuditRecord
            {
                Id = ObjectId.GenerateNewId().ToString(),
                At = now,
                ActorId = actor.Player.Id,
                ActorName = actor.Player.PlayerName,
                ActorRole = actor.Role.ToString(),
                Entity = entity,
                EntityId = entityId,
                EntityName = entityName,
                Field = r.Field,
                Old = r.Old,
                New = r.New,
                Source = "web"
            }).ToList();
        }

        // Audit-first: if the record cannot be written the edit is refused and nothing changes.
        private bool TryAudit(IReadOnlyList<AuditRecord> entries)
        {
            try { _audit.Insert(entries); return true; }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                _log("Audit write failed (" + ex.GetType().Name + "); the edit was refused.");
                return false;
            }
        }

        private void Finish(IReadOnlyList<AuditRecord> entries, bool applied)
        {
            TrySetOutcome(entries.Select(e => e.Id).ToList(), applied);
        }

        private void TrySetOutcome(IReadOnlyList<string> ids, bool applied)
        {
            if (ids.Count == 0) { return; }
            try { _audit.SetOutcome(ids, applied); }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // The row stays unapplied, which marks it for review; the edit itself is not undone.
                _log("Could not mark audit rows as " + (applied ? "applied" : "rejected") + " (" + ex.GetType().Name + ").");
            }
        }

        private RoomEditReply RoomFailed(string what, string key, Exception ex)
        {
            // Generic result: no exception text, no values.
            _log("Room " + what + " failed for " + key + " (" + ex.GetType().Name + ").");
            return RoomReply(AdminResult.Failed);
        }

        private static RoomEditReply RoomReply(AdminResult result, RoomDetail room = null, IEnumerable<FieldError> errors = null, string message = null)
        {
            var reply = new RoomEditReply { Result = result };
            if (room != null) { reply.Room = room; }
            if (errors != null) { reply.Errors.AddRange(errors); }
            if (message != null) { reply.Message = message; }
            return reply;
        }

        // Constant time over the content: both sides are hashed to a fixed length first so length does not leak.
        public static bool PasswordsEqual(string expected, string supplied)
        {
            byte[] a = SHA256.HashData(Encoding.UTF8.GetBytes(expected ?? string.Empty));
            byte[] b = SHA256.HashData(Encoding.UTF8.GetBytes(supplied ?? string.Empty));
            return CryptographicOperations.FixedTimeEquals(a, b);
        }

        private static AdminRole ToContract(AdminRole role)
        {
            return role;
        }

        private static string Render(BsonValue value)
        {
            if (value == null || value.IsBsonNull) { return "null"; }
            try { return value.ToJson(); }
            catch (Exception) { return value.ToString(); }
        }

        private static RoomBehavior BehaviorToDetail(string hook, string module, string type, int priority, BsonDocument config)
        {
            return new RoomBehavior { Hook = hook ?? string.Empty, Module = module ?? string.Empty, Type = type ?? string.Empty, Priority = priority, ConfigJson = CompactJson(config) };
        }

        private static RoomSearchEntry SearchToDetail(string id, string target, int refreshMilliseconds, string rewardShort, RoomSearchDiscovery discovery)
        {
            var detail = new RoomSearchEntry { Id = id ?? string.Empty, Target = target ?? string.Empty, RefreshMs = refreshMilliseconds, RewardShort = rewardShort ?? string.Empty };
            if (discovery != null) { detail.Discovery = discovery; }
            return detail;
        }

        // Compact JSON text of a behavior config; "{}" for null or empty. Never throws.
        private static string CompactJson(BsonDocument config)
        {
            if (config == null || config.ElementCount == 0) { return "{}"; }
            try
            {
                using (var stream = new System.IO.MemoryStream())
                {
                    using (var writer = new System.Text.Json.Utf8JsonWriter(stream))
                    {
                        WriteJson(writer, config);
                    }
                    return System.Text.Encoding.UTF8.GetString(stream.ToArray());
                }
            }
            catch (Exception) { return "{}"; }
        }

        private static void WriteJson(System.Text.Json.Utf8JsonWriter writer, BsonValue value)
        {
            switch (value.BsonType)
            {
                case BsonType.Document:
                    writer.WriteStartObject();
                    foreach (BsonElement element in value.AsBsonDocument)
                    {
                        writer.WritePropertyName(element.Name);
                        WriteJson(writer, element.Value);
                    }
                    writer.WriteEndObject();
                    break;
                case BsonType.Array:
                    writer.WriteStartArray();
                    foreach (BsonValue item in value.AsBsonArray) { WriteJson(writer, item); }
                    writer.WriteEndArray();
                    break;
                case BsonType.String: writer.WriteStringValue(value.AsString); break;
                case BsonType.Boolean: writer.WriteBooleanValue(value.AsBoolean); break;
                case BsonType.Int32: writer.WriteNumberValue(value.AsInt32); break;
                case BsonType.Int64: writer.WriteNumberValue(value.AsInt64); break;
                case BsonType.Double:
                    if (double.IsNaN(value.AsDouble) || double.IsInfinity(value.AsDouble)) { writer.WriteStringValue(value.AsDouble.ToString(System.Globalization.CultureInfo.InvariantCulture)); }
                    else { writer.WriteNumberValue(value.AsDouble); }
                    break;
                case BsonType.Null: writer.WriteNullValue(); break;
                default: writer.WriteStringValue(value.ToString()); break;
            }
        }

        private static RoomDetail RoomFrom(Room room)
        {
            var detail = new RoomDetail
            {
                Key = room.Id ?? string.Empty,
                ShortDescription = room.ShortDescription ?? string.Empty,
                LongDescription = room.LongDescription ?? string.Empty,
                Area = room.Area ?? string.Empty
            };
            if (room.X.HasValue) { detail.X = room.X.Value; }
            if (room.Y.HasValue) { detail.Y = room.Y.Value; }
            if (room.Z.HasValue) { detail.Z = room.Z.Value; }
            foreach (Exit exit in room.Exits ?? new List<Exit>())
            {
                if (exit != null && exit.Name != null && exit.DestinationId != null) { detail.Exits.Add(new RoomExit { Name = exit.Name, To = exit.DestinationId }); }
            }
            foreach (Feature feature in room.Features ?? new List<Feature>())
            {
                if (feature != null) { detail.Features.Add(FeatureToDetail(feature)); }
            }
            foreach (MUD.Core.Components.BehaviorRef behavior in room.Behaviors ?? new List<MUD.Core.Components.BehaviorRef>())
            {
                if (behavior != null) { detail.Behaviors.Add(BehaviorToDetail(behavior.Hook, behavior.Module, behavior.Type, behavior.Priority, behavior.Config)); }
            }
            foreach (MUD.Core.Components.SearchEntry entry in room.Search.Entries)
            {
                if (entry != null) { detail.Searches.Add(SearchToDetail(entry.EntryId, entry.Target, entry.RefreshMilliseconds, entry.Reward == null ? null : entry.Reward.ShortDescription, entry.Discovery == null ? null : new RoomSearchDiscovery { Id = entry.Discovery.FlagId ?? string.Empty, Found = entry.Discovery.Found ?? string.Empty, Again = entry.Discovery.Again ?? string.Empty })); }
            }
            if (room.Gen != null)
            {
                detail.Gen = new RoomGen { Map = room.Gen.Map ?? string.Empty, Terrain = room.Gen.Terrain ?? string.Empty, Seed = room.Gen.Seed ?? string.Empty, LibVersion = room.Gen.LibVersion ?? 0 };
            }
            return detail;
        }

        private static RoomDetail RoomFrom(BsonDocument document)
        {
            var detail = new RoomDetail
            {
                Key = document["_id"].ToString(),
                ShortDescription = Text(document, "ShortDescription") ?? string.Empty,
                LongDescription = Text(document, "LongDescription") ?? string.Empty,
                Area = Text(document, "Area") ?? string.Empty
            };
            int? number = Number(document, "X");
            if (number.HasValue) { detail.X = number.Value; }
            number = Number(document, "Y");
            if (number.HasValue) { detail.Y = number.Value; }
            number = Number(document, "Z");
            if (number.HasValue) { detail.Z = number.Value; }
            BsonValue exits;
            if (document.TryGetValue("Exits", out exits) && exits.IsBsonArray)
            {
                foreach (BsonValue element in exits.AsBsonArray)
                {
                    if (!element.IsBsonDocument) { continue; }
                    string name = Text(element.AsBsonDocument, "Name");
                    string to = Text(element.AsBsonDocument, "DestinationId");
                    if (name != null && to != null) { detail.Exits.Add(new RoomExit { Name = name, To = to }); }
                }
            }
            BsonValue features;
            if (document.TryGetValue("Features", out features) && features.IsBsonArray)
            {
                foreach (BsonValue element in features.AsBsonArray)
                {
                    if (!element.IsBsonDocument) { continue; }
                    BsonDocument f = element.AsBsonDocument;
                    var feature = new RoomFeature
                    {
                        Short = Text(f, "ShortDescription") ?? string.Empty,
                        Long = Text(f, "LongDescription") ?? string.Empty,
                        RoomLine = Text(f, "RoomLine") ?? string.Empty,
                        Detail = Text(f, "DetailDescription") ?? string.Empty
                    };
                    feature.Handles.AddRange(Words(f, "Handles"));
                    feature.Adjectives.AddRange(Words(f, "Adjectives"));
                    feature.Plurals.AddRange(Words(f, "Plurals"));
                    feature.TemplateId = Text(f, "TemplateId") ?? string.Empty;
                    feature.TemplateVersion = Number(f, "TemplateVersion") ?? 0;
                    detail.Features.Add(feature);
                }
            }
            BsonValue behaviors;
            if (document.TryGetValue("behaviors", out behaviors) && behaviors.IsBsonArray)
            {
                foreach (BsonValue element in behaviors.AsBsonArray)
                {
                    if (!element.IsBsonDocument) { continue; }
                    BsonDocument b = element.AsBsonDocument;
                    BsonValue config;
                    detail.Behaviors.Add(BehaviorToDetail(Text(b, "hook"), Text(b, "module"), Text(b, "type"), Number(b, "priority") ?? 0, b.TryGetValue("config", out config) && config.IsBsonDocument ? config.AsBsonDocument : null));
                }
            }
            BsonValue search;
            if (document.TryGetValue("search", out search) && search.IsBsonDocument && search.AsBsonDocument.TryGetValue("entries", out BsonValue entries) && entries.IsBsonArray)
            {
                foreach (BsonValue element in entries.AsBsonArray)
                {
                    if (!element.IsBsonDocument) { continue; }
                    BsonDocument e = element.AsBsonDocument;
                    BsonValue reward;
                    string rewardShort = e.TryGetValue("reward", out reward) && reward.IsBsonDocument ? Text(reward.AsBsonDocument, "ShortDescription") : null;
                    BsonValue discovery;
                    RoomSearchDiscovery found = null;
                    if (e.TryGetValue("discovery", out discovery) && discovery.IsBsonDocument)
                    {
                        found = new RoomSearchDiscovery { Id = Text(discovery.AsBsonDocument, "id") ?? string.Empty, Found = Text(discovery.AsBsonDocument, "found") ?? string.Empty, Again = Text(discovery.AsBsonDocument, "again") ?? string.Empty };
                    }
                    detail.Searches.Add(SearchToDetail(Text(e, "id"), Text(e, "target"), Number(e, "refreshMilliseconds") ?? 0, rewardShort, found));
                }
            }
            BsonValue gen;
            if (document.TryGetValue("gen", out gen) && gen.IsBsonDocument)
            {
                detail.Gen = new RoomGen
                {
                    Map = Text(gen.AsBsonDocument, "map") ?? string.Empty,
                    Terrain = Text(gen.AsBsonDocument, "terrain") ?? string.Empty,
                    Seed = Text(gen.AsBsonDocument, "seed") ?? string.Empty,
                    LibVersion = Number(gen.AsBsonDocument, "libVersion") ?? 0
                };
            }
            return detail;
        }

        private static PlayerDetail DetailFrom(Player player)
        {
            return new PlayerDetail
            {
                NameKey = player.NameKey ?? string.Empty,
                Name = player.PlayerName ?? string.Empty,
                ShortDescription = player.ShortDescription ?? string.Empty,
                LongDescription = player.LongDescription ?? string.Empty,
                Race = player.Race ?? string.Empty,
                Gender = player.Gender ?? string.Empty,
                Class = player.Class ?? string.Empty,
                Retired = player.Retired,
                Role = RoleOf(player)
            };
        }

        private static PlayerSummary SummaryFrom(BsonDocument document)
        {
            return new PlayerSummary
            {
                NameKey = Text(document, "NameKey") ?? string.Empty,
                Name = Text(document, "PlayerName") ?? string.Empty,
                Race = Text(document, "Race") ?? string.Empty,
                Class = Text(document, "Class") ?? string.Empty,
                Gender = Text(document, "Gender") ?? string.Empty,
                Location = Text(document, "CurrentLocationId") ?? string.Empty,
                Role = RoleFrom(document),
                Retired = Flag(document, "Retired")
            };
        }

        // The role stored on a player document: the roles component, admin before creator (D45).
        public static AdminRole RoleFrom(BsonDocument document)
        {
            BsonValue roles;
            if (!document.TryGetValue(PlayerFields.Roles, out roles) || !roles.IsBsonDocument) { return AdminRole.Player; }
            if (Flag(roles.AsBsonDocument, "admin")) { return AdminRole.Admin; }
            if (Flag(roles.AsBsonDocument, "creator")) { return AdminRole.Creator; }
            return AdminRole.Player;
        }

        private static string Text(BsonDocument document, string name)
        {
            BsonValue value;
            return document.TryGetValue(name, out value) && !value.IsBsonNull ? value.ToString() : null;
        }

        private static IEnumerable<string> Words(BsonDocument document, string name)
        {
            BsonValue value;
            if (!document.TryGetValue(name, out value) || !value.IsBsonArray) { return new string[0]; }
            return value.AsBsonArray.Where(v => v.IsString).Select(v => v.AsString).ToList();
        }

        private static int? Number(BsonDocument document, string name)
        {
            BsonValue value;
            return document.TryGetValue(name, out value) && value.IsNumeric ? value.ToInt32() : (int?)null;
        }

        private static bool Flag(BsonDocument document, string name)
        {
            BsonValue value;
            return document.TryGetValue(name, out value) && value.IsBoolean && value.AsBoolean;
        }
    }
}
