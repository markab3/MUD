using System;
using System.Linq;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MongoDB.Driver;
using MUD.Core.Commands;
using MUD.Core.Formatting;
using MUD.Core.Repositories.Interfaces;
using MUD.Telnet;
using System.Threading;

namespace MUD.Core.GameObjects
{
    public class Player : Living
    {
        private IPlayerRepository _playerRepository;

        private Client _connection;

        private CommandQueue _commandQueue;

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

        [BsonIgnore]
        public Client Connection
        {
            get { return _connection; }
            set
            {
                // If connection is changing, drop event handlers for the old one.
                if (_connection != null && value != _connection)
                {
                    _connection.DataReceived -= DataReceivedHandler;
                    _connection.ClientDisconnected -= ClientDisconnectedHandler;
                }
                if (value != null && value != _connection)
                {
                    _isQuitting = false; // a new connection starts a new session
                }
                _connection = value;
                if (value != null)
                {
                    _connection.DataReceived += DataReceivedHandler;
                    _connection.ClientDisconnected += ClientDisconnectedHandler;
                }
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

        public bool Save(bool quiet = false)
        {
            if (!quiet) { ReceiveMessage("%^Bold%^%^Yellow%^Saving...%^Reset%^"); }
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
                if (_passwordChangePending || string.IsNullOrWhiteSpace(Id)) { return false; }
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
                    saved = Save(true);
                }
                finally
                {
                    _passwordChangePending = false;
                    if (!saved) { Password = oldPassword; }
                }
                return saved;
            }
        }

        public void Quit()
        {
            // Connection.Disconnect() re-enters through ClientDisconnectedHandler, so run the sequence once per session.
            if (_isQuitting) { return; }
            _isQuitting = true;
            _promptHandler = null;

            ReceiveMessage("Thanks for playing!");
            ConnectionStatus = PlayerConnectionStatuses.Offline;
            if (Save())
            {
                _world.RemovePlayer(this);
                Connection.Disconnect();
            }
            else
            {
                // Uh oh!
                // Save failed, so the player is still online: allow another quit attempt.
                _isQuitting = false;
            }
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
                if (DateTime.Now.Subtract(_lastSave).TotalMinutes >= 10)
                {
                    Save();
                }
            }
        }

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

        private void ClientDisconnectedHandler(object sender, Client e)
        {
            // I think this is what we want for now... 
            // I think in other muds the player object doesn't get removed unless you quit via the actual command. That way you can't just kill your connection and avoid death or whatever.
            // We would need to add a timeout feature...
            e.DataReceived -= DataReceivedHandler;
            e.ClientDisconnected -= ClientDisconnectedHandler;
            Quit();
            //ConnectionStatus = EPlayerConnectionStatus.NetDead; // Maybe just online/offline? Not sure when net dead is a thing...
        }
    }
}