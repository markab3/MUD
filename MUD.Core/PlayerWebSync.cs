using System;
using System.Collections.Generic;
using System.Linq;
using MUD.Core.GameObjects;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;

namespace MUD.Core
{
    /// <summary>
    /// Brings website player edits into the running game (specs/WEB_EDITING.md section 6.3, owner decisions in section 15).
    /// The website writes the `player` collection directly and bumps each document's Rev; every 5 seconds the game compares
    /// the stored Rev of the ONLINE players with the Rev each live player holds.
    ///
    /// Poll runs on a worker thread and only reads _id and Rev. Apply runs on the game thread (the world is unlocked) and
    /// touches only the website-owned fields (Player.ApplyWebEdit). Because Player.Save never writes those fields, an edit
    /// is neither lost nor reverted by the player's next save.
    /// </summary>
    public class PlayerWebSync
    {
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

        public const string DescriptionChangedMessage = "Something about you feels different...";

        public const string RolesChangedMessage = "Your status has changed.";

        public const string RetiredMessage = "This character has been retired and can no longer be played.";

        public class PollResult
        {
            /// <summary>The online player ids that were asked about.</summary>
            public List<string> Asked { get; set; } = new List<string>();

            /// <summary>Stored Rev per player that still has a document.</summary>
            public Dictionary<string, int> Revisions { get; set; } = new Dictionary<string, int>();
        }

        private readonly IPlayerRepository _players;
        private readonly ISnapshotRepository _snapshots;
        private readonly Func<IEnumerable<Player>> _online;
        private readonly Action<string> _log;
        private DateTime _nextPoll = DateTime.MinValue;
        private int _polling;

        public PlayerWebSync(IPlayerRepository players, Func<IEnumerable<Player>> online, Action<string> log = null)
        {
            _players = players;
            _snapshots = players as ISnapshotRepository;
            _online = online;
            _log = log ?? Console.WriteLine;
        }

        /// <summary>True once per PollInterval. Game thread.</summary>
        public bool IsDue(DateTime now)
        {
            if (now < _nextPoll) { return false; }
            _nextPoll = now + PollInterval;
            return true;
        }

        /// <summary>
        /// Starts a poll unless one is still running. Game thread: the online ids are collected here, the query runs on a pool
        /// thread and the result is posted back to the game thread.
        /// </summary>
        public void StartPoll(Func<Action, bool> postToGame)
        {
            if (_snapshots == null) { return; }
            if (System.Threading.Interlocked.Exchange(ref _polling, 1) == 1) { return; }
            List<string> ids = _online().Select(p => p.Id).Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().ToList();
            if (ids.Count == 0) { System.Threading.Interlocked.Exchange(ref _polling, 0); return; }
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    PollResult result = Poll(ids);
                    postToGame(() => Apply(result));
                }
                catch (Exception ex)
                {
                    _log("Web player poll failed: " + ex.GetType().Name);
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _polling, 0);
                }
            });
        }

        /// <summary>Any thread. One query for the Rev of the given players; does not touch the cache.</summary>
        public PollResult Poll(List<string> ids)
        {
            return new PollResult { Asked = ids, Revisions = _snapshots.GetRevisions(ids) };
        }

        /// <summary>Game thread only. Applies what a poll found.</summary>
        public void Apply(PollResult result)
        {
            var online = _online().ToList();
            foreach (string id in result.Asked)
            {
                try
                {
                    int stored;
                    if (!result.Revisions.TryGetValue(id, out stored)) { continue; }
                    Player player = online.FirstOrDefault(p => p.Id == id);
                    if (player == null || stored == player.Rev) { continue; }

                    var raw = _players.GetRawDocument(id);
                    if (raw == null) { continue; }
                    Player.WebChanges changes = player.ApplyWebEdit(raw);

                    if ((changes & Player.WebChanges.Retired) != 0 && player.Retired)
                    {
                        // Tell them, then log them out through the normal quit/save path.
                        player.ReceiveMessage(RetiredMessage);
                        player.Quit();
                        continue;
                    }
                    if ((changes & Player.WebChanges.Text) != 0) { player.ReceiveMessage(DescriptionChangedMessage); }
                    if ((changes & Player.WebChanges.Roles) != 0) { player.ReceiveMessage(RolesChangedMessage); }
                }
                catch (Exception ex)
                {
                    _log("Applying a web player change failed for " + id + ": " + ex.GetType().Name);
                }
            }
        }
    }
}
