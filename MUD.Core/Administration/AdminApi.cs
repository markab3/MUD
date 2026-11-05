using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using MongoDB.Bson;
using MUD.Contracts;
using MUD.Core.Components;
using MUD.Core.GameObjects;
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
    public sealed class AdminApi
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

        public AdminApi(IAdminHost host, IPlayerRepository players, IRoomRepository rooms, IAdminQueries queries, IAuditStore audit,
            AdminSessions sessions, IClock clock, Action<string> log, Func<int, int> random = null)
        {
            _host = host ?? throw new ArgumentNullException(nameof(host));
            _players = players ?? throw new ArgumentNullException(nameof(players));
            _rooms = rooms ?? throw new ArgumentNullException(nameof(rooms));
            _queries = queries ?? throw new ArgumentNullException(nameof(queries));
            _audit = audit ?? throw new ArgumentNullException(nameof(audit));
            _sessions = sessions ?? throw new ArgumentNullException(nameof(sessions));
            _clock = clock ?? SystemClock.Instance;
            _log = log ?? (_ => { });
            _random = random ?? (max => new Random().Next(max));
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
            player.SyncWebFieldsFromStore();
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
            Authorize(request.Token, AdminRole.Creator);
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
            Authorize(request.Token, AdminRole.Creator);
            Player player = FindByNameKey(Normalize(request.NameKey));
            return player == null ? new GetPlayerReply { Found = false } : new GetPlayerReply { Found = true, Player = DetailFrom(player) };
        }

        public UpdatePlayerReply UpdatePlayer(UpdatePlayerRequest request)
        {
            return Mutate(request.Token, AdminRole.Creator, actor =>
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
            // A cached player can predate a change written elsewhere (the website editor, until its pages move to this service).
            target.SyncWebFieldsFromStore();
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
