using System;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MUD.Core.Commands;
using MUD.Core.Formatting;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;
using MUD.CoreApi;
using MUD.Core.Sessions;
using System.Threading;

namespace MUD.Core.GameObjects
{
    public class Player : Living
    {
        private IPlayerRepository _playerRepository;

        // The session this player is attached to and where the player's output goes (null before login).
        private SessionId _sessionId;
        private IFrontEndSink _frontEndSink;
        private string _remoteKey = string.Empty;

        private CommandQueue _commandQueue;

        // Autosave interval (specs/GAME_LOOP.md 8.2).
        private static readonly TimeSpan AutosaveInterval = TimeSpan.FromMinutes(10);

        // Password writes handed to the persistence worker and not yet confirmed. While any are outstanding the
        // stored password is older than the in-memory one, so SyncPasswordFromStore must not "adopt" it.
        private int _unconfirmedPasswordWrites;

        // True once Quit() has started for the current session; cleared when a new session is attached.
        private bool _isQuitting;

        public string PlayerName { get; set; }

        // Lowercase, trimmed PlayerName. Login and create match on this by exact equality; a unique index guards it.
        public string NameKey { get; set; }

        public string Password { get; set; }

        // Serialises persistence and password changes (Save runs from the heartbeat, quit and commands).
        private readonly object _saveLock = new object();

        // True only while TryChangePassword is saving a password chosen in-game; only then may Save write it.
        private bool _passwordChangePending;

        // When set, input lines go here instead of the command queue (see SetPromptHandler).
        private volatile Action<string> _promptHandler;

        public string SelectedTerm { get; set; }

        // Website-owned (specs/WEB_EDITING.md 4.2, 15). Rev is the revision of the stored document this object last
        // applied; the website bumps it on every edit and the game never writes it. Retired characters cannot log in.
        public int Rev { get; set; }

        public bool Retired { get; set; }

        // Set by an in-game role change (promote) so the next save also writes the roles component. Otherwise the game
        // leaves roles alone: the website owns them.
        private volatile bool _rolesChanged;

        /// <summary>Call after changing the roles component in-game, before Save.</summary>
        public void MarkRolesChanged() { _rolesChanged = true; }

        [Flags]
        public enum WebChanges { None = 0, Text = 1, Roles = 2, Retired = 4 }

        /// <summary>
        /// Applies the website-owned fields of the stored document to this live player (game thread only):
        /// ShortDescription, LongDescription, Race, Gender, Class, Retired and the creator/admin flags of the roles component, plus Rev.
        /// Inventory, location, password and every other field are left alone. Announces nothing.
        /// </summary>
        public WebChanges ApplyWebEdit(BsonDocument doc)
        {
            var changes = WebChanges.None;
            if (SetText(doc, PlayerFields.ShortDescription, ShortDescription, v => ShortDescription = v)) { changes |= WebChanges.Text; }
            if (SetText(doc, PlayerFields.LongDescription, LongDescription, v => LongDescription = v)) { changes |= WebChanges.Text; }
            if (SetText(doc, PlayerFields.Race, Race, v => Race = v)) { changes |= WebChanges.Text; }
            if (SetText(doc, PlayerFields.Gender, Gender, v => Gender = v)) { changes |= WebChanges.Text; }
            if (SetText(doc, PlayerFields.Class, Class, v => Class = v)) { changes |= WebChanges.Text; }

            BsonValue retired = doc.GetValue(PlayerFields.Retired, BsonNull.Value);
            bool nowRetired = retired.IsBoolean && retired.AsBoolean;
            if (nowRetired != Retired) { Retired = nowRetired; changes |= WebChanges.Retired; }

            bool hasCreator, hasAdmin;
            StoredRoles(doc, out hasCreator, out hasAdmin);
            if (Roles.Creator != hasCreator) { Roles.Creator = hasCreator; changes |= WebChanges.Roles; }
            if (Roles.Admin != hasAdmin) { Roles.Admin = hasAdmin; changes |= WebChanges.Roles; }

            BsonValue rev = doc.GetValue(PlayerFields.Rev, BsonNull.Value);
            Rev = rev.IsNumeric ? rev.ToInt32() : 0;
            return changes;
        }

        private static bool SetText(BsonDocument doc, string field, string current, Action<string> set)
        {
            BsonValue value = doc.GetValue(field, BsonNull.Value);
            if (!value.IsString && !value.IsBsonNull) { return false; }
            string updated = value.IsString ? value.AsString : null;
            if (string.Equals(updated, current, StringComparison.Ordinal)) { return false; }
            set(updated);
            return true;
        }

        // The website owns the creator and admin flags inside the stored roles document. Anything else (the creator's
        // current directory) is the game's and is left alone.
        private static void StoredRoles(BsonDocument doc, out bool hasCreator, out bool hasAdmin)
        {
            hasCreator = false;
            hasAdmin = false;
            BsonValue roles;
            if (!doc.TryGetValue(PlayerFields.Roles, out roles) || !roles.IsBsonDocument) { return; }
            BsonValue value;
            if (roles.AsBsonDocument.TryGetValue("creator", out value) && value.IsBoolean) { hasCreator = value.AsBoolean; }
            if (roles.AsBsonDocument.TryGetValue("admin", out value) && value.IsBoolean) { hasAdmin = value.AsBoolean; }
        }

        // The document the game may write: everything except the website-owned fields. The roles component is included
        // only after an in-game role change.
        private BsonDocument GameOwnedFields(BsonDocument whole)
        {
            bool includeRoles = _rolesChanged;
            _rolesChanged = false;
            var set = new BsonDocument();
            foreach (var element in whole)
            {
                if (Array.IndexOf(PlayerFields.NotWrittenByGame, element.Name) >= 0 &&
                    !(includeRoles && element.Name == PlayerFields.Roles)) { continue; }
                set[element.Name] = element.Value;
            }
            return set;
        }

        /// <summary>
        /// Adopts the website-owned fields of the stored document when its Rev or Retired flag differs, so a player
        /// cached before a web edit shows the current text, roles and retirement. Never throws.
        /// </summary>
        public bool SyncWebFieldsFromStore()
        {
            lock (_saveLock)
            {
                if (string.IsNullOrWhiteSpace(Id)) { return false; }
                try
                {
                    BsonDocument stored = _playerRepository.GetRawDocument(Id);
                    if (stored == null) { return false; }
                    BsonValue rev = stored.GetValue(PlayerFields.Rev, BsonNull.Value);
                    int storedRev = rev.IsNumeric ? rev.ToInt32() : 0;
                    BsonValue retired = stored.GetValue(PlayerFields.Retired, BsonNull.Value);
                    bool storedRetired = retired.IsBoolean && retired.AsBoolean;
                    if (storedRev == Rev && storedRetired == Retired) { return false; }
                    ApplyWebEdit(stored);
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        // Input and disconnects reach the player through the game loop (World routes lines to
        // DataReceivedHandler and calls Quit on a Disconnected item), so no event handlers are attached here.
        /// <summary>The id of the session this player is attached to; empty before login.</summary>
        [BsonIgnore]
        public SessionId SessionId { get { return _sessionId; } }

        /// <summary>Where this player's output goes; null before login. It never blocks (decision D42).</summary>
        [BsonIgnore]
        public IFrontEndSink OutputSink { get { return _frontEndSink; } }

        /// <summary>
        /// Attaches the player to a session: its id, the sink that receives its output, and the remote key the login
        /// throttle uses. Attaching a different session starts a new session, so Quit can run again.
        /// </summary>
        public void Attach(SessionId sessionId, IFrontEndSink sink, string remoteKey = null)
        {
            if (sink != null && (sessionId != _sessionId || !ReferenceEquals(sink, _frontEndSink)))
            {
                _isQuitting = false; // a new session
            }
            _sessionId = sessionId;
            _frontEndSink = sink;
            _remoteKey = remoteKey ?? string.Empty;
            if (sink != null) { sink.SetTerminal(sessionId, SelectedTerm); }
        }

        /// <summary>Changes the persisted terminal choice and tells the session's front end, which does the conversion.</summary>
        public void ChangeTerminal(string terminalName)
        {
            SelectedTerm = terminalName;
            IFrontEndSink sink = _frontEndSink;
            if (sink != null) { sink.SetTerminal(_sessionId, terminalName); }
        }

        [BsonIgnore]
        public PlayerConnectionStatuses ConnectionStatus { get; set; }

        public Player(World world, CommandQueue commandQueue, IPlayerRepository playerRepository) : base(world)
        {
            _commandQueue = commandQueue;
            _playerRepository = playerRepository;
        }

        /// <summary>The session as seen by prompted input (echo control, prompts), backed by the sink; null when not attached. Overridable for tests.</summary>
        [BsonIgnore]
        public virtual IPromptChannel PromptChannel
        {
            get { return _frontEndSink == null ? null : new SinkPromptChannel(_sessionId, _frontEndSink, _remoteKey); }
        }

        [BsonIgnore]
        public bool HasPrompt { get { return _promptHandler != null; } }

        /// <summary>
        /// While a handler is set, every line the player types goes to it and is NOT resolved as a command,
        /// queued, or kept anywhere (used for passwords). The handler must call ClearPromptHandler when done.
        /// </summary>
        public void SetPromptHandler(Action<string> handler)
        {
            _promptHandler = handler;
        }

        public void ClearPromptHandler()
        {
            _promptHandler = null;
        }

        private IClock Clock { get { return _world != null ? _world.Clock : SystemClock.Instance; } }

        // The persistence sink for this player's collection, or null when saves are written synchronously
        // (before the game thread exists, and in tests that use a plain fake repository).
        private IPersistSink Sink
        {
            get
            {
                var snapshots = _playerRepository as ISnapshotRepository;
                return snapshots == null ? null : snapshots.Sink;
            }
        }

        /// <summary>
        /// Saves the player. With a persistence sink (normal operation) this snapshots the player and hands it to
        /// the worker without touching the database, and returns true meaning "queued"; the outcome is reported to
        /// the player later. Without a sink the write happens here and the result is the database's.
        /// </summary>
        public bool Save(bool quiet = false)
        {
            return SavePlayer(quiet, false, false);
        }

        private bool SavePlayer(bool quiet, bool final, bool passwordChange)
        {
            if (!quiet) { ReceiveMessage("%^Bold%^%^Yellow%^Saving...%^Reset%^"); }
            if (Sink != null) { return PostSnapshot(quiet, final, passwordChange); }
            return SaveSynchronously();
        }

        private bool PostSnapshot(bool quiet, bool final, bool passwordChange)
        {
            BsonDocument snapshot;
            BsonDocument gameFields;
            lock (_saveLock)
            {
                // The Password it carries must not be older than the stored one (the website may have changed it
                // since this player was loaded).
                SyncPasswordFromStore();
                snapshot = this.ToBsonDocument();
                gameFields = GameOwnedFields(snapshot);
                if (passwordChange) { _unconfirmedPasswordWrites++; }
            }
            _lastSave = Clock.Now;

            var request = new PersistRequest
            {
                Collection = ((ISnapshotRepository)_playerRepository).CollectionName,
                Id = Id,
                // Field-level $set of the game-owned fields; the whole snapshot is only written when nothing is stored
                // yet (a new character). Website-owned fields and Rev are never rewritten from this object.
                Kind = PersistKind.SetFields,
                Doc = gameFields,
                Fallback = snapshot,
                Final = final,
                Reason = final ? "logout" : "player save"
            };
            if (passwordChange)
            {
                request.OnCompleted = persisted => { lock (_saveLock) { _unconfirmedPasswordWrites--; } };
            }
            if (!quiet)
            {
                request.OnFailed = attempts => Notify("%^Bold%^%^Red%^Saving failed; it will be retried.%^Reset%^");
                request.OnCompleted = Chain(request.OnCompleted, persisted =>
                {
                    if (persisted) { Notify("%^Bold%^%^Yellow%^Saved.%^Reset%^"); }
                });
            }
            if (final && _world != null) { _world.BeginFinalSave(this, request); }
            Sink.Post(request);
            return true;
        }

        private static Action<bool> Chain(Action<bool> first, Action<bool> second)
        {
            if (first == null) { return second; }
            return v => { first(v); second(v); };
        }

        // Tells a still-connected player about a persistence outcome (callbacks arrive on the game thread).
        private void Notify(string message)
        {
            if (ConnectionStatus == PlayerConnectionStatuses.LoggedIn && _frontEndSink != null) { ReceiveMessage(message); }
        }

        private bool SaveSynchronously()
        {
            lock (_saveLock)
            {
                if (string.IsNullOrWhiteSpace(Id))
                {
                    _playerRepository.Insert(this);
                    _lastSave = DateTime.Now;
                    return true;
                }
                else
                {
                    // The Password it carries must not be older than the stored one (the website may have changed it
                    // since this player was loaded). Only game-owned fields are written; the whole document only
                    // when nothing is stored yet.
                    SyncPasswordFromStore();
                    if (_playerRepository.UpdateFields(Id, GameOwnedFields(this.ToBsonDocument())) || _playerRepository.Update(this))
                    {
                        _lastSave = DateTime.Now;
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Adopts the stored password if it differs from the in-memory one, so a stale object never overwrites a
        /// password changed elsewhere. Does nothing while an in-game change is being saved. Returns true if the
        /// in-memory password was replaced. Never throws: on a read failure the in-memory value is kept.
        /// </summary>
        public bool SyncPasswordFromStore()
        {
            lock (_saveLock)
            {
                if (_passwordChangePending || _unconfirmedPasswordWrites > 0 || string.IsNullOrWhiteSpace(Id)) { return false; }
                try
                {
                    BsonDocument stored = _playerRepository.GetRawDocument(Id);
                    BsonValue value;
                    if (stored == null || !stored.TryGetValue("Password", out value) || !value.IsString) { return false; }
                    if (value.AsString == Password) { return false; }
                    Password = value.AsString;
                    return true;
                }
                catch (Exception)
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// Sets and saves a new password chosen in-game. If the save fails the old password stays in memory.
        /// </summary>
        public bool TryChangePassword(string newPassword)
        {
            lock (_saveLock)
            {
                string oldPassword = Password;
                Password = newPassword;
                _passwordChangePending = true;
                bool saved = false;
                try
                {
                    saved = SavePlayer(true, false, Sink != null);
                }
                finally
                {
                    _passwordChangePending = false;
                    if (!saved) { Password = oldPassword; }
                }
                return saved;
            }
        }

        /// <summary>
        /// Logout (specs/GAME_LOOP.md 8.4), on the game thread, once per session: farewell, leave the world,
        /// final snapshot to the persistence worker, then a flushing close so the farewell reaches the client.
        /// Logout never waits on the database: a failed final save is retried by the worker (and spooled).
        /// </summary>
        public void Quit()
        {
            if (_isQuitting) { return; }
            _isQuitting = true;
            _promptHandler = null;
            if (_commandQueue != null) { _commandQueue.Clear(this); }

            ReceiveMessage("Thanks for playing!");
            ConnectionStatus = PlayerConnectionStatuses.Offline;
            if (_world != null) { _world.RemovePlayer(this); }
            SavePlayer(true, true, false);
            if (_world != null) { _world.RetireConnection(_sessionId); }
            IFrontEndSink sink = _frontEndSink;
            if (sink != null)
            {
                sink.Close(_sessionId, string.Empty, true);
                sink.SessionEnded(_sessionId, _world != null && _world.IsStopping ? SessionEndReason.WorldStopping : SessionEndReason.Quit);
            }
        }

        /// <summary>
        /// Sends the message to the player's sink on the message's channel, as markup: the terminal conversion is
        /// not done here (the sink the world attaches does it). Dropped when the player has no session.
        /// </summary>
        public override void ReceiveMessage(string message)
        {
            IFrontEndSink sink = _frontEndSink;
            if (sink == null) { return; }
            sink.Output(_sessionId, DeliveryChannel, message, false);
        }

        public override void DoHeartbeat()
        {
            base.DoHeartbeat();

            if (ConnectionStatus == PlayerConnectionStatuses.LoggedIn)
            {
                if (Clock.Now.Subtract(_lastSave).TotalMinutes >= AutosaveInterval.TotalMinutes)
                {
                    Save();
                }
            }
        }

        /// <summary>
        /// Handles one input line from this player. Runs on the game thread (World routes lines here from the
        /// inbound queue); it resolves and parses the command there, then queues it.
        /// </summary>
        public void DataReceivedHandler(object sender, string message)
        {
            // check to see if this matches a command that the player has available.
            // if it does, add that command to the queue
            var promptHandler = _promptHandler;
            if (promptHandler != null)
            {
                promptHandler(message);
                return;
            }

            ICommand matchedCommand = ResolveCommand(message);
            var sensitiveCommand = matchedCommand as ISensitiveInputCommand;
            if (sensitiveCommand != null)
            {
                // Keep arguments (which may be a password) out of the queued command text.
                string redacted = sensitiveCommand.Redact(message);
                if (redacted != message)
                {
                    message = redacted;
                    ReceiveMessage("Do not type a password on the command line. You will be prompted for it.");
                }
            }
            if (matchedCommand != null) // Move this logic to something else..something to match Command classes with input.
            {
                _commandQueue.AddCommand(new CommandExecution(this, message, matchedCommand));
            }
            else
            {
                ReceiveMessage("Command not recognized.");
            }
        }
    }
}