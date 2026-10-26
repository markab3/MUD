using System;
using System.Collections.Generic;
using System.Linq;
using MUD.Core.Formatting;
using MUD.Core.GameObjects;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;

namespace MUD.Core
{
    /// <summary>
    /// Brings website room edits and deletions into the running game (specs/WEB_EDITING.md section 6, owner decisions in
    /// section 15). The website writes the `room` collection directly and bumps each document's Rev; every 5 seconds the
    /// game compares the stored Rev of its live rooms with the Rev it holds.
    ///
    /// Poll runs on a worker thread and only reads _id and Rev. Apply runs on the game thread (the world is unlocked)
    /// and touches only the web allow-list of a room, never Items, features or occupants' own state.
    /// </summary>
    public class RoomWebSync
    {
        public static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);

        public const string UpdatedMessage = "Something feels different...";

        public const string DeletedArrivalFormat = "A sudden draft of magical wind deposits {0} in the room!";

        public class PollResult
        {
            /// <summary>The live room ids that were asked about.</summary>
            public List<string> Asked { get; set; } = new List<string>();

            /// <summary>Stored Rev per room that still has a document.</summary>
            public Dictionary<string, int> Revisions { get; set; } = new Dictionary<string, int>();
        }

        private readonly IRoomRepository _rooms;
        private readonly ISnapshotRepository _snapshots;
        private readonly Func<string> _startRoomKey;
        private readonly Func<int, int> _random;
        private readonly Action<string> _log;
        private readonly HashSet<string> _stuck = new HashSet<string>();
        private DateTime _nextPoll = DateTime.MinValue;
        private int _polling;

        public RoomWebSync(IRoomRepository rooms, Func<string> startRoomKey, Func<int, int> random = null, Action<string> log = null)
        {
            _rooms = rooms;
            _snapshots = rooms as ISnapshotRepository;
            _startRoomKey = startRoomKey;
            _random = random ?? (max => new Random().Next(max));
            _log = log ?? Console.WriteLine;
        }

        /// <summary>True once per PollInterval; the caller then starts a poll. Game thread.</summary>
        public bool IsDue(DateTime now)
        {
            if (now < _nextPoll) { return false; }
            _nextPoll = now + PollInterval;
            return true;
        }

        /// <summary>Starts a poll unless one is still running. Game thread. Runs the query on a pool thread and posts the result back.</summary>
        public void StartPoll(Func<Action, bool> postToGame)
        {
            if (_snapshots == null) { return; }
            if (System.Threading.Interlocked.Exchange(ref _polling, 1) == 1) { return; }
            System.Threading.Tasks.Task.Run(() =>
            {
                try
                {
                    PollResult result = Poll();
                    if (!postToGame(() => Apply(result))) { System.Threading.Interlocked.Exchange(ref _polling, 0); return; }
                }
                catch (Exception ex)
                {
                    _log("Web edit poll failed: " + ex.GetType().Name);
                }
                finally
                {
                    System.Threading.Interlocked.Exchange(ref _polling, 0);
                }
            });
        }

        /// <summary>Any thread. One query for the Rev of every live room; does not touch the cache.</summary>
        public PollResult Poll()
        {
            var asked = _snapshots.CachedIds();
            return new PollResult { Asked = asked, Revisions = _snapshots.GetRevisions(asked) };
        }

        /// <summary>Game thread only. Applies what a poll found.</summary>
        public void Apply(PollResult result)
        {
            foreach (string id in result.Asked)
            {
                try
                {
                    Room live = _rooms.Get(id);
                    if (live == null) { continue; }

                    int stored;
                    if (!result.Revisions.TryGetValue(id, out stored))
                    {
                        ApplyDeleted(live);
                    }
                    else if (stored != live.Rev)
                    {
                        ApplyEdited(live);
                    }
                }
                catch (Exception ex)
                {
                    _log("Applying a web room change failed for " + id + ": " + ex.GetType().Name);
                }
            }
        }

        private void ApplyEdited(Room live)
        {
            var raw = _rooms.GetRawDocument(live.Id);
            if (raw == null) { return; } // deleted since the poll; the next poll handles it
            live.ApplyWebEdit(raw);
            live.TellRoom(UpdatedMessage);
        }

        // The room's document is gone. Move everything inside to a random usable neighbour, else the default room.
        private void ApplyDeleted(Room dead)
        {
            Room destination = ChooseDestination(dead);
            if (destination == null)
            {
                // Nothing to move them to. Keep the room alive in memory rather than dropping players into the void.
                if (_stuck.Add(dead.Id))
                {
                    _log("Room " + dead.Id + " was deleted but no neighbouring room or default room exists; its occupants were left in place.");
                }
                return;
            }
            _stuck.Remove(dead.Id);

            var movedLivings = dead.Occupants.ToList();
            var movedItems = dead.Items.ToList();

            foreach (var living in movedLivings)
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

            var names = movedLivings.Select(l => l.ShortDescription).Concat(movedItems.Select(i => i.ShortDescription)).ToList();
            if (names.Count > 0)
            {
                destination.TellRoom(string.Format(DeletedArrivalFormat, names.GetListText()));
            }
            foreach (var player in movedLivings.OfType<Player>())
            {
                player.ReceiveMessage(destination.Examine(player));
                player.Save(true); // persist the new location
            }

            // Drop the dead room from the cache so nothing reaches it again.
            if (_snapshots != null) { _snapshots.Evict(dead.Id); }
        }

        private Room ChooseDestination(Room dead)
        {
            var neighbours = new List<Room>();
            foreach (var exit in (dead.Exits ?? new List<Exit>()))
            {
                if (exit == null || string.IsNullOrEmpty(exit.DestinationId) || exit.DestinationId == dead.Id) { continue; }
                if (neighbours.Any(n => n.Id == exit.DestinationId)) { continue; }
                var room = _rooms.Get(exit.DestinationId);
                if (room != null) { neighbours.Add(room); }
            }
            if (neighbours.Count > 0) { return neighbours[_random(neighbours.Count)]; }

            string startKey = _startRoomKey();
            if (string.IsNullOrWhiteSpace(startKey) || startKey == dead.Id) { return null; }
            return _rooms.Get(startKey);
        }
    }
}
