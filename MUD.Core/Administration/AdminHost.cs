using System;
using MUD.Core.GameObjects;

namespace MUD.Core.Administration
{
    /// <summary>
    /// What the Admin service needs from the running game. World provides it (WorldAdminHost); tests provide a fake that runs work on the
    /// calling thread. Everything that touches live game objects runs through TryRunOnGameThread (owner rule: all world change goes
    /// through the single game thread).
    /// </summary>
    public interface IAdminHost
    {
        /// <summary>
        /// Runs the work on the game thread and waits for it, for a bounded time. Returns false when the work did not run (the game is
        /// stopping, the queue is full or the wait ran out before it started). A work item that throws also returns false.
        /// </summary>
        bool TryRunOnGameThread<T>(Func<T> work, out T result);

        /// <summary>The default room key, or null when none is configured. That room can never be deleted.</summary>
        string StartRoomKey { get; }

        /// <summary>The login throttle shared with telnet (D43). The key is the address the front end passes in.</summary>
        bool IsThrottled(string remoteKey);

        void RecordLoginOutcome(string remoteKey, bool success);

        /// <summary>True while the player is in the world (game thread only).</summary>
        bool IsOnline(Player player);
    }

    /// <summary>The Admin service's view of a running World.</summary>
    public sealed class WorldAdminHost : IAdminHost
    {
        private readonly World _world;
        private readonly string _startRoomKey;

        public WorldAdminHost(World world, string startRoomKey)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _startRoomKey = string.IsNullOrWhiteSpace(startRoomKey) ? null : startRoomKey;
        }

        public string StartRoomKey { get { return _startRoomKey; } }

        public bool TryRunOnGameThread<T>(Func<T> work, out T result)
        {
            return _world.TryRunOnGameThread(work, out result);
        }

        public bool IsThrottled(string remoteKey)
        {
            return _world.IsThrottled(remoteKey);
        }

        public void RecordLoginOutcome(string remoteKey, bool success)
        {
            _world.RecordLoginOutcome(remoteKey, success);
        }

        public bool IsOnline(Player player)
        {
            return _world.Players.Contains(player);
        }
    }
}
