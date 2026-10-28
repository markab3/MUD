using System;
using MUD.Core.GameObjects;
using MUD.Core.Repositories;
using MUD.Core.Sessions;
using MUD.Telnet;

namespace MUD.Core.Commands
{
    public class ChpwdCommand : ICommand, ISensitiveInputCommand
    {
        private readonly World _world;

        public ChpwdCommand(World world)
        {
            _world = world;
        }

        public string[] CommandKeywords { get => new string[] { "chpwd" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Changes your password. You are prompted for your current password, the new one and a confirmation; nothing you type is echoed.\r\n \r\nSyntax: chpwd"; }

        // Anything typed after the keyword could be a password: never keep it.
        public string Redact(string input)
        {
            return CommandKeywords[0];
        }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return null;
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            var player = (Player)commandIssuer;
            new PasswordChangeFlow(player, player.PromptChannel, _world == null ? null : _world.LoginThrottle).Start();
        }
    }

    /// <summary>
    /// The prompted "Current password / New password / Confirm new password" exchange. Lines typed while it runs
    /// go to the player's prompt handler, never to the command queue, so passwords are not kept or logged.
    /// </summary>
    public class PasswordChangeFlow
    {
        public const string BlockedMessage = "Too many failed password attempts. Please try again later.";
        public const string WrongCurrentMessage = "Password not changed.";
        public const string MismatchMessage = "The passwords did not match. Password not changed.";
        public const string SameMessage = "The new password must be different from the current one. Password not changed.";
        public const string SaveFailedMessage = "Your password could not be saved, so it was not changed. Please try again later.";
        public const string SuccessMessage = "Your password has been changed.";

        private enum Step { Current, New, Confirm }

        private readonly Player _player;
        private readonly IPromptChannel _connection;
        private readonly LoginThrottle _throttle;
        private Step _step;
        private string _current;
        private string _new;

        public PasswordChangeFlow(Player player, IPromptChannel connection, LoginThrottle throttle)
        {
            _player = player;
            _connection = connection;
            _throttle = throttle;
        }

        public void Start()
        {
            if (_connection == null) { return; }
            string key = _connection.RemoteKey ?? string.Empty;
            if (_throttle != null && _throttle.IsBlocked(key))
            {
                _connection.Send(BlockedMessage);
                return;
            }
            _step = Step.Current;
            _player.SetPromptHandler(HandleLine);
            _connection.SetEchoHidden(true);
            _connection.SendPrompt("Current password: ");
        }

        private void HandleLine(string line)
        {
            line = line ?? string.Empty;
            switch (_step)
            {
                case Step.Current:
                    _connection.SetEchoHidden(false);
                    _connection.Send(string.Empty); // The client did not echo the user's Enter.
                    if (!CheckCurrent(line)) { return; }
                    _current = line;
                    _step = Step.New;
                    _connection.SetEchoHidden(true);
                    _connection.SendPrompt("New password: ");
                    return;

                case Step.New:
                    _connection.SetEchoHidden(false);
                    _connection.Send(string.Empty);
                    string problem = PlayerAccounts.ValidatePassword(line);
                    if (problem == null && line == _current) { problem = SameMessage; }
                    if (problem != null) { Finish(problem); return; }
                    _new = line;
                    _step = Step.Confirm;
                    _connection.SetEchoHidden(true);
                    _connection.SendPrompt("Confirm new password: ");
                    return;

                default:
                    _connection.SetEchoHidden(false);
                    _connection.Send(string.Empty);
                    if (line != _new) { Finish(MismatchMessage); return; }
                    string newPassword = _new;
                    Finish(null);
                    _connection.Send(_player.TryChangePassword(newPassword) ? SuccessMessage : SaveFailedMessage);
                    return;
            }
        }

        private bool CheckCurrent(string supplied)
        {
            string key = _connection.RemoteKey ?? string.Empty;
            if (_throttle != null && _throttle.IsBlocked(key))
            {
                Finish(BlockedMessage);
                return false;
            }
            // Pick up a password changed on the website since this player was loaded.
            _player.SyncPasswordFromStore();
            if (_player.Password != null && _player.Password == supplied)
            {
                return true;
            }
            if (_throttle != null) { _throttle.RecordFailure(key); }
            Finish(WrongCurrentMessage);
            return false;
        }

        private void Finish(string message)
        {
            _player.ClearPromptHandler();
            _current = null;
            _new = null;
            if (message != null) { _connection.Send(message); }
        }
    }
}
