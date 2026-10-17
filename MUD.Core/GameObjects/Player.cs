using System;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MUD.Core.Commands;
using MUD.Core.Formatting;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;
using MUD.Telnet;
using System.Threading;

namespace MUD.Core.GameObjects
{
    public class Player : Living
    {
        private IPlayerRepository _playerRepository;

        private IClientConnection _connection;

        private CommandQueue _commandQueue;

        // Autosave interval (specs/GAME_LOOP.md 8.2).
        private static readonly TimeSpan AutosaveInterval = TimeSpan.FromMinutes(10);

        // Password writes handed to the persistence worker and not yet confirmed. While any are outstanding the
        // stored password is older than the in-memory one, so SyncPasswordFromStore must not "adopt" it.
        private int _unconfirmedPasswordWrites;

        // True once Quit() has started for the current connection; cleared when a new connection is attached.
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

        // Input and disconnects reach the player through the game loop (World routes lines to
        // DataReceivedHandler and calls Quit on a Disconnected item), so no event handlers are attached here.
        [BsonIgnore]
        public IClientConnection Connection
        {
            get { return _connection; }
            set
            {
                if (value != null && value != _connection)
                {
                    _isQuitting = false; // a new connection starts a new session
                }
                _connection = value;
            }
        }

        [BsonIgnore]
        public PlayerConnectionStatuses ConnectionStatus { get; set; }

        public Player(World world, CommandQueue commandQueue, IPlayerRepository playerRepository) : base(world)
        {
            _commandQueue = commandQueue;
            _playerRepository = playerRepository;
        }

        /// <summary>The connection as seen by prompted input (echo control, prompts). Overridable for tests.</summary>
        [BsonIgnore]
        public virtual ILoginConnection PromptChannel { get { return _connection; } }

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
            lock (_saveLock)
            {
                // Replace writes the whole document, so first make sure the Password it carries is not older
                // than the stored one (the website may have changed it since this player was loaded).
                SyncPasswordFromStore();
                snapshot = this.ToBsonDocument();
                if (passwordChange) { _unconfirmedPasswordWrites++; }
            }
            _lastSave = Clock.Now;

            var request = new PersistRequest
            {
                Collection = ((ISnapshotRepository)_playerRepository).CollectionName,
                Id = Id,
                Kind = PersistKind.Replace,
                Doc = snapshot,
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
            if (ConnectionStatus == PlayerConnectionStatuses.LoggedIn && _connection != null) { ReceiveMessage(message); }
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
                    // Update replaces the whole document, so first make sure the Password it carries is not
                    // older than the stored one (the website may have changed it since this player was loaded).
                    SyncPasswordFromStore();
                    if (_playerRepository.Update(this))
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
        /// final snapshot to the persistence worker, then a flushing disconnect so the farewell reaches the client.
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
            if (_world != null) { _world.RetireConnection(_connection); }
            _connection.Disconnect(true);
        }

        public override void ReceiveMessage(string message)
        {
            var selectedTerminalHandler = _world.TerminalHandlers.FirstOrDefault(th => th.TerminalName.ToLower() == SelectedTerm?.ToLower() || (th.Aliases != null && th.Aliases.Any(a => a.ToLower() == SelectedTerm?.ToLower())));
            if (selectedTerminalHandler != null)
            {
                message = selectedTerminalHandler.ResolveOutput(message);
            }
            _connection.Send(message);
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