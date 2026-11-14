using System.Collections.Generic;
using MUD.Core.Components;
using MUD.Core.GameObjects;

namespace MUD.Core.Events
{
    // The entities that carry a heartbeat behavior entry (owner decision D56). Core delivers the heartbeat to these and to
    // nothing else, so an entity without an entry costs nothing. Game thread only.
    //
    // Players are not kept here: the world already ticks every logged-in player (autosave), and that tick delivers the
    // player's own entries. An entity whose last heartbeat entry has gone is dropped from the index the next time the
    // heartbeat reaches it.
    public class HeartbeatIndex
    {
        private readonly List<GameObject> _entities = new List<GameObject>();
        private readonly HashSet<GameObject> _members = new HashSet<GameObject>();

        public int Count { get { return _entities.Count; } }

        public bool Contains(GameObject entity)
        {
            return entity != null && _members.Contains(entity);
        }

        // Adds the entity when it carries a heartbeat entry. Returns true when it was added by this call.
        public bool Track(GameObject entity)
        {
            if (entity == null || entity is Player) { return false; }
            if (!EventDispatcher.HasEntry(entity, BehaviorHooks.Heartbeat)) { return false; }
            if (!_members.Add(entity)) { return false; }
            _entities.Add(entity);
            return true;
        }

        public bool Untrack(GameObject entity)
        {
            if (entity == null || !_members.Remove(entity)) { return false; }
            _entities.Remove(entity);
            return true;
        }

        // Copies the members, in the order they were added, into the list (cleared first), so the caller can deliver while
        // the index changes underneath it.
        public void CopyTo(List<GameObject> destination)
        {
            destination.Clear();
            destination.AddRange(_entities);
        }
    }
}
