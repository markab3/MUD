using System;
using System.Collections.Generic;
using System.Linq;
using MongoDB.Bson;
using MUD.Core.GameObjects;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;

namespace MUD.Core.Repositories
{
    public enum PlayerSaveResult
    {
        Saved,
        NameTaken,
        Failed
    }

    // Player name rules and lookups, kept apart from World so they can be tested with a fake repository.
    public class PlayerAccounts
    {
        public const int MinNameLength = 3;
        public const int MaxNameLength = 16;
        public const int MinPasswordLength = 6;
        public const int MaxPasswordLength = 64;

        private static readonly HashSet<string> ReservedNames = new HashSet<string>
        {
            "admin", "administrator", "system", "god", "quit", "who", "create", "connect", "self", "me", "you", "look"
        };

        private readonly IPlayerRepository _repository;
        private readonly Action<string> _log;

        public PlayerAccounts(IPlayerRepository repository, Action<string> log)
        {
            _repository = repository;
            _log = log ?? (_ => { });
        }

        // The form a typed name is stored and compared in: trimmed, lowercase.
        public static string Normalize(string name)
        {
            return (name ?? string.Empty).Trim().ToLowerInvariant();
        }

        // The display form: trimmed, otherwise as typed.
        public static string Clean(string name)
        {
            return (name ?? string.Empty).Trim();
        }

        // Returns null when the name is acceptable for a new character, otherwise a message for the player.
        public static string ValidateName(string name)
        {
            name = Clean(name);
            if (name.Length < MinNameLength || name.Length > MaxNameLength)
            {
                return string.Format("Names must be {0} to {1} letters long.", MinNameLength, MaxNameLength);
            }
            if (!name.All(c => (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')))
            {
                return "Names may only contain the letters A-Z (no digits, spaces or symbols).";
            }
            if (ReservedNames.Contains(Normalize(name)))
            {
                return string.Format("The name {0} is reserved. Please choose another.", name);
            }
            return null;
        }

        // Returns null when the password is acceptable for a password change, otherwise a message for the player.
        // MUD.Web/Services/PasswordRules.cs holds the same rules and the same messages; keep them in step
        // (PasswordRuleParityTests checks it).
        public static string ValidatePassword(string password)
        {
            if (password == null || password.Length < MinPasswordLength || password.Length > MaxPasswordLength)
            {
                return string.Format("Passwords must be {0} to {1} characters long.", MinPasswordLength, MaxPasswordLength);
            }
            if (password.Any(char.IsControl))
            {
                return "Passwords may not contain control characters.";
            }
            return null;
        }

        // Exact match on NameKey, never a substring match.
        public Player FindByName(string typedName)
        {
            var key = Normalize(typedName);
            if (key.Length == 0) { return null; }
            return _repository.Search(p => p.NameKey == key).FirstOrDefault();
        }

        public Player Authenticate(string typedName, string password)
        {
            var player = FindByName(typedName);
            if (player != null)
            {
                // The cached live player can predate a password change made elsewhere (the website).
                player.SyncPasswordFromStore();
            }
            return player != null && player.Password == password ? player : null;
        }

        public PlayerSaveResult TrySave(Player player)
        {
            try
            {
                return player.Save() ? PlayerSaveResult.Saved : PlayerSaveResult.Failed;
            }
            catch (DuplicateKeyException)
            {
                return PlayerSaveResult.NameTaken;
            }
        }

        // Startup: backfill NameKey, then create the unique index. Never throws and never deletes or renames a player.
        public void Initialize()
        {
            try
            {
                BackfillNameKeys();
            }
            catch (Exception ex)
            {
                _log("Player NameKey backfill failed: " + ex.Message);
            }

            try
            {
                _repository.CreateNameKeyIndex();
            }
            catch (Exception ex)
            {
                _log("Could not create the unique index on Player.NameKey; continuing without it: " + ex.Message);
            }
        }

        // Sets NameKey on stored players lacking it. Records whose key would collide with another record
        // are logged and left untouched (no key, no rename, no delete) for an operator to resolve.
        public void BackfillNameKeys()
        {
            var players = _repository.GetAll().ToList();

            var groups = players
                .Where(p => !string.IsNullOrWhiteSpace(string.IsNullOrEmpty(p.NameKey) ? p.PlayerName : p.NameKey))
                .GroupBy(p => string.IsNullOrEmpty(p.NameKey) ? Normalize(p.PlayerName) : p.NameKey);

            foreach (var group in groups)
            {
                var members = group.ToList();
                if (members.Count > 1)
                {
                    _log(string.Format("Player name collision on '{0}', NameKey not set for: {1}", group.Key,
                        string.Join(", ", members.Select(p => string.Format("{0} (id {1})", p.PlayerName, p.Id)))));
                    continue;
                }

                var player = members[0];
                if (!string.IsNullOrEmpty(player.NameKey)) { continue; }

                if (_repository.UpdateFields(player.Id, new BsonDocument("NameKey", group.Key)))
                {
                    player.NameKey = group.Key;
                }
                else
                {
                    _log(string.Format("Could not set NameKey for player {0} (id {1}).", player.PlayerName, player.Id));
                }
            }
        }
    }
}
