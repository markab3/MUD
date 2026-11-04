using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using MongoDB.Bson;
using MongoDB.Bson.Serialization.Attributes;
using MUD.CoreApi;
using MUD.Core.Commands;
using MUD.Core.Events;
using MUD.Core.Formatting;

namespace MUD.Core.GameObjects
{
    public class Living : GameObject, MUD.Core.Components.IBehaviorHolder
    {
        // Behavior entries (owner decisions D5, D53): stored as the embedded array "behaviors", on players and NPCs.
        // Not dispatched yet.
        [BsonElement("behaviors")]
        public List<MUD.Core.Components.BehaviorRef> Behaviors
        {
            // Never null, including for a stored document that has no behaviors element.
            get { return _behaviors ?? (_behaviors = new List<MUD.Core.Components.BehaviorRef>()); }
            set { _behaviors = value; }
        }

        private List<MUD.Core.Components.BehaviorRef> _behaviors;

        // Creator and admin roles (owner decisions D45, G30): the core component "roles", stored as the embedded
        // document "roles": { creator, admin, currentDirectory }. The website reads creator and admin from it.
        [BsonElement("roles")]
        public MUD.Core.Components.Roles Roles
        {
            // Never null, including for a stored document that has no roles element.
            get { return _roles ?? (_roles = new MUD.Core.Components.Roles()); }
            set { _roles = value; }
        }

        private MUD.Core.Components.Roles _roles;

        protected Room _currentLocation;

        protected World _world;

        public string Gender { get; set; }

        public string Race { get; set; }

        public string CurrentLocationId { get; set; }

        public string Class { get; set; }

        public string[] KnownCommands { get; set; }

        public List<InventoryItem> Inventory { get; set; } = new List<InventoryItem>();

        public List<InventoryItem> WornItems { get; set; } = new List<InventoryItem>();

        public List<InventoryItem> HeldItems { get; set; } = new List<InventoryItem>();

        // Game time before which this living may not act (see CommandQueue). Replaces the old IsBusy flag.
        [BsonIgnore]
        public System.DateTime NextActionAt { get; set; } = System.DateTime.MinValue;

        [BsonIgnore]
        public Room CurrentLocation
        {
            get
            {
                if (_currentLocation == null && !string.IsNullOrWhiteSpace(CurrentLocationId))
                {
                    _currentLocation = _world.GetRoom(CurrentLocationId);
                }
                return _currentLocation;
            }
            set { _currentLocation = value; } // Maybe make this private so we have to load via ID?
        }

        public Living(World world)
        {
            _world = world;
        }

        public virtual void ReceiveMessage(string message) { }

        /// <summary>
        /// Delivers a message tagged with an output channel. By default it is the same as the untagged
        /// ReceiveMessage(string), so a Living that only overrides that method still receives every message.
        /// Untagged messages are on the System channel.
        /// </summary>
        public virtual void ReceiveMessage(string message, OutputChannel channel)
        {
            // The channel is carried to the one-argument method (which subclasses override) for the duration of the call.
            OutputChannel previousChannel = _deliveryChannel;
            _deliveryChannel = channel;
            try { ReceiveMessage(message); }
            finally { _deliveryChannel = previousChannel; }
        }

        private OutputChannel _deliveryChannel = OutputChannel.System;

        /// <summary>The channel of the message being delivered by ReceiveMessage right now; System for untagged messages.</summary>
        [BsonIgnore]
        protected OutputChannel DeliveryChannel { get { return _deliveryChannel; } }

        public virtual void DoHeartbeat()
        {
            // Heartbeat behavior entries are resolved to in-process handlers by (Module, Type). An entry nothing is
            // registered for is dormant and skipped (D10, D60). Event-bus dispatch comes in phase 3; until then this
            // is the only caller, and it still reaches players only (World.doHeartbeat).
            foreach (var entry in Behaviors.Where(b => b != null && b.Hook == MUD.Core.Components.BehaviorHooks.Heartbeat).ToList())
            {
                if (MUD.Core.Components.InProcessBehaviorRegistry.Shared.TryGetHeartbeat(entry.Module, entry.Type, out var handler))
                {
                    handler(this, entry);
                }
            }
        }

        public override string Examine()
        {
            return string.Format("Here stands {0}, a prime example of {1} {2}.", ShortDescription, Race.GetArticle(), Race);
        }

        public GameObject[] MatchObjects(string input, bool includeInventory = true, bool includeRoomItems = true, bool includeRoomOccupants = true, bool includeRoomFeatures = true)
        {
            // Trim whitespace.
            input = input.Trim().ToLower();

            // support adding a number at the start - so "2 swords" would get the first 2 swords in the room.
            int? quantity = null;
            if (input.Contains(" "))
            {
                var quantityMatches = Regex.Match(input, "^(\\d+) (.*)$");
                if (quantityMatches.Success)
                {
                    quantity = int.Parse(quantityMatches.Groups[1].Value);
                    input = Regex.Replace(input, "^(\\d+) (.*)$", "$2");
                }
            }

            // Support adding a number at the end - so "sword 2" would get the second sword in the room.
            int? ordinalValue = null;
            if (input.Contains(" "))
            {
                var ordinalMatches = Regex.Match(input, "^(.*) (\\d+)$");
                if (ordinalMatches.Success)
                {
                    ordinalValue = int.Parse(ordinalMatches.Groups[2].Value);
                    input = Regex.Replace(input, "^(.*) (\\d+)$", "$1");
                }
            }

            List<GameObject> searchList = new List<GameObject>();
            if (includeInventory) { searchList.AddRange(this.Inventory ?? new List<InventoryItem>()); }// Check inventory of items in the living's inventory
            if (includeRoomItems) { searchList.AddRange(CurrentLocation.Items ?? new List<InventoryItem>()); }// Check inventory of items in the living's inventory
            if (includeRoomOccupants) { searchList.AddRange(CurrentLocation.Occupants ?? new List<Living>()); }// Check the living occupants of the current room
            if (includeRoomFeatures) { searchList.AddRange(CurrentLocation.Features ?? new List<Feature>()); }// Check the features of the current room

            // Return all (or quantity of) the things that match on plurals.
            GameObject[] matches = searchList.Where(g => g.IsPluralMatchForString(input)).ToArray();
            if (matches != null && matches.Length >= 1)
            {
                if (quantity != null && quantity.Value <= matches.Count())
                {
                    return matches.Take(quantity.Value).ToArray(); // Return first quantity matches
                }
                else
                {
                    return matches; // Return all plural matches
                }
            }

            // If nothing matches on plurals, return the first (or ordinal) item that matches otherwise.
            matches = searchList.Where(g => g.IsMatchForString(input)).ToArray();

            if (ordinalValue != null && ordinalValue < matches.Count())
            {
                return new GameObject[] { matches[ordinalValue.Value] }; // Return the ordinalValue-th match
            }

            return matches; // Return all singular matches
        }

        public void MoveToRoom(string roomIdToEnter, string viaExitName = null)
        {
            var roomToEnter = _world.GetRoom(roomIdToEnter);
            if (roomToEnter == null)
            {
                ReceiveMessage("The way is blocked.");
                return;
            }

            MoveToRoom(roomToEnter, string.Format("{0} leaves.", this.ShortDescription), null, "walk", viaExitName);
        }

        // ---- Parking while a Before chain runs (CORE_API 2.3; owner decisions D15, D62) ----
        //
        // While a Before chain waits, the living is parked: CommandQueue holds its further commands ("Command queued."),
        // but it still receives output and can still be targeted, killed, moved or made to quit. "stop" ends the wait
        // unless the commit has begun (the commit is one synchronous step on the game thread, so a stop can only arrive
        // before it). Game thread only.
        private readonly List<BeforeChain> _waitingChains = new List<BeforeChain>();

        [BsonIgnore]
        public bool IsParked { get { return _waitingChains.Count > 0; } }

        internal void RegisterWaitingChain(BeforeChain chain)
        {
            _waitingChains.Add(chain);
        }

        internal void UnregisterWaitingChain(BeforeChain chain)
        {
            _waitingChains.Remove(chain);
        }

        // Ends every Before chain this living is waiting on; each chain's completion step then runs with a Cancelled
        // result. Returns how many were cancelled.
        public int CancelWaitingChains()
        {
            if (_waitingChains.Count == 0) { return 0; }
            BeforeChain[] waiting = _waitingChains.ToArray();
            foreach (BeforeChain chain in waiting) { chain.Cancel(); }
            return waiting.Length;
        }

        // Starts a Before chain on the world's dispatcher and parks this living while it waits. afterwards runs exactly
        // once: inline when the answer is immediate, otherwise later on the game thread (parking has ended by then).
        internal void RunBefore(string eventName, IReadOnlyList<EventParty> parties, IReadOnlyDictionary<string, object> payload, bool sequential, Action<BeforeOutcome> afterwards)
        {
            BeforeChain chain = null;
            chain = _world.Events.BeginBefore(eventName, parties, payload, sequential, outcome =>
            {
                if (chain != null) { _waitingChains.Remove(chain); }
                afterwards(outcome);
            });
            if (chain != null && !chain.IsComplete) { _waitingChains.Add(chain); }
        }

        private const string NoLongerPossibleMessage = "Things changed while you waited, so you did not do that.";

        /// <summary>
        /// Moves to an already resolved room. A null entranceMessage uses the room's default arrival message.
        ///
        /// One entity.move event covers leaving and entering (D4). When the mover, the room being left or the room being
        /// entered carries a living.exit or living.enter entry (D54), the move is a Before chain: the mover is parked,
        /// every handler is waited for, any Deny vetoes, and the first Deny to arrive gives the message (D6, D7, D15). If
        /// all allow, the move is re-validated (still online, still in the room it left, exit still present) and then
        /// committed, followed by After notifications. With no entries anywhere the move happens at once, as before.
        /// </summary>
        public void MoveToRoom(Room roomToEnter, string exitMessage, string entranceMessage, string reason = "walk", string viaExitName = null)
        {
            Room fromRoom = CurrentLocation;
            EventDispatcher dispatcher = _world == null ? null : _world.Events;
            bool hooked = dispatcher != null
                && (EventDispatcher.HasEntry(this, MUD.Core.Components.BehaviorHooks.LivingExit)
                    || EventDispatcher.HasEntry(this, MUD.Core.Components.BehaviorHooks.LivingEnter)
                    || EventDispatcher.HasEntry(fromRoom, MUD.Core.Components.BehaviorHooks.LivingExit)
                    || EventDispatcher.HasEntry(roomToEnter, MUD.Core.Components.BehaviorHooks.LivingEnter));

            if (!hooked)
            {
                CommitMove(fromRoom, roomToEnter, exitMessage, entranceMessage);
                AnnounceMove(dispatcher, false, fromRoom, roomToEnter, reason, viaExitName);
                return;
            }

            RunBefore(EventNames.EntityMove, MoveParties(fromRoom, roomToEnter), MovePayload(fromRoom, roomToEnter, reason, viaExitName), false, outcome =>
            {
                switch (outcome.Result)
                {
                    case BeforeResult.Allowed:
                        if (!StillCanMove(fromRoom, roomToEnter, viaExitName)) { ReceiveMessage(NoLongerPossibleMessage); return; }
                        CommitMove(fromRoom, roomToEnter, exitMessage, entranceMessage);
                        AnnounceMove(dispatcher, true, fromRoom, roomToEnter, reason, viaExitName);
                        break;
                    case BeforeResult.Denied:
                        ReceiveMessage(string.IsNullOrEmpty(outcome.Message) ? "Something stops you." : outcome.Message);
                        break;
                    case BeforeResult.Cancelled:
                        ReceiveMessage("You stop.");
                        break;
                    default:
                        ReceiveMessage("Something stops you.");
                        break;
                }
            });
        }

        // The re-validation step before commit (D15, D62): the world may have changed while the chain waited.
        private bool StillCanMove(Room fromRoom, Room toRoom, string viaExitName)
        {
            var player = this as Player;
            if (player != null && player.ConnectionStatus == PlayerConnectionStatuses.Offline) { return false; }
            if (!ReferenceEquals(CurrentLocation, fromRoom)) { return false; }
            if (viaExitName != null)
            {
                if (fromRoom == null || fromRoom.Exits == null) { return false; }
                if (!fromRoom.Exits.Any(e => e != null && string.Equals(e.Name, viaExitName, System.StringComparison.OrdinalIgnoreCase) && e.DestinationId == toRoom.Id)) { return false; }
            }
            return true;
        }

        private void CommitMove(Room fromRoom, Room roomToEnter, string exitMessage, string entranceMessage)
        {
            if (fromRoom != null)
            {
                fromRoom.ExitRoom(this, exitMessage);
            }

            CurrentLocation = roomToEnter;
            CurrentLocationId = roomToEnter.Id;

            roomToEnter.EnterRoom(this, entranceMessage);
            ReceiveMessage(roomToEnter.Examine(this));
        }

        // How a mover's own entries are matched (D54): the mover is a party under BOTH hooks, because it both leaves one
        // room and enters another. The room being left is consulted under living.exit and the room being entered under
        // living.enter. The event stays one entity.move (D4); a Deny from any of them vetoes the whole move (D6).
        private static readonly string[] MoverHooks = { MUD.Core.Components.BehaviorHooks.LivingExit, MUD.Core.Components.BehaviorHooks.LivingEnter };
        private static readonly string[] ExitHooks = { MUD.Core.Components.BehaviorHooks.LivingExit };
        private static readonly string[] EnterHooks = { MUD.Core.Components.BehaviorHooks.LivingEnter };

        private List<EventParty> MoveParties(Room fromRoom, Room toRoom)
        {
            var parties = new List<EventParty>(3) { new EventParty("mover", this, MoverHooks) };
            if (fromRoom != null) { parties.Add(new EventParty("from", fromRoom, ExitHooks)); }
            parties.Add(new EventParty("to", toRoom, EnterHooks));
            return parties;
        }

        private static Dictionary<string, object> MovePayload(Room fromRoom, Room toRoom, string reason, string viaExitName)
        {
            return new Dictionary<string, object>
            {
                { "from", fromRoom == null ? null : fromRoom.Id },
                { "to", toRoom.Id },
                { "via_exit", viaExitName },
                { "reason", reason }
            };
        }

        // The After phase: notifications to the mover and both rooms. Nothing is built unless a party carries an entry or
        // an observer is listening.
        private void AnnounceMove(EventDispatcher dispatcher, bool hooked, Room fromRoom, Room toRoom, string reason, string viaExitName)
        {
            if (dispatcher == null) { return; }
            if (!hooked && !dispatcher.WantsChangeEvents) { return; }
            dispatcher.EmitAfter(EventNames.EntityMove, MoveParties(fromRoom, toRoom), MovePayload(fromRoom, toRoom, reason, viaExitName));
        }

        /// <summary>
        /// Moves items between this living's room and inventory for get and drop (event item.move, hooks item.take and
        /// item.drop, D3, D54). commit does the move and returns the objects it really moved. When the living, an item or
        /// the room carries an entry for the hook, each item runs its own Before chain (all waited for together, any Deny
        /// keeps that item where it is), the living is parked meanwhile, and commit only gets the items that were allowed
        /// and are still worth moving; otherwise commit runs at once with every item, as before.
        /// </summary>
        internal void MoveItemsThrough(string hook, string reason, IList<InventoryItem> items, Func<IList<InventoryItem>, IList<GameObject>> commit)
        {
            Room room = CurrentLocation;
            EventDispatcher dispatcher = _world == null ? null : _world.Events;
            bool hooked = dispatcher != null && (EventDispatcher.HasEntry(this, hook) || EventDispatcher.HasEntry(room, hook));
            for (int index = 0; dispatcher != null && !hooked && index < items.Count; index++)
            {
                hooked = EventDispatcher.HasEntry(items[index], hook);
            }

            if (!hooked)
            {
                IList<GameObject> movedNow = commit(items);
                AnnounceItemMoves(dispatcher, false, hook, reason, room, movedNow);
                return;
            }

            var allowed = new List<InventoryItem>();
            var denials = new List<string>();
            int remaining = items.Count;
            bool cancelled = false;
            Action finish = () =>
            {
                if (cancelled) { ReceiveMessage("You stop."); return; }
                var player = this as Player;
                if ((player != null && player.ConnectionStatus == PlayerConnectionStatuses.Offline) || !ReferenceEquals(CurrentLocation, room))
                {
                    ReceiveMessage(NoLongerPossibleMessage);
                    return;
                }
                foreach (string denial in denials) { ReceiveMessage(denial); }
                if (allowed.Count == 0) { return; }
                IList<GameObject> moved = commit(allowed);
                AnnounceItemMoves(dispatcher, true, hook, reason, room, moved);
            };

            foreach (InventoryItem item in items.ToList())
            {
                InventoryItem current = item;
                var parties = new List<EventParty>(3)
                {
                    new EventParty("item", current, new[] { hook }),
                    new EventParty("actor", this, new[] { hook }),
                    new EventParty("room", room, new[] { hook })
                };
                var payload = new Dictionary<string, object> { { "reason", reason }, { "actor", Id }, { "item", current.Id }, { "room", room.Id } };
                RunBefore(EventNames.ItemMove, parties, payload, false, outcome =>
                {
                    if (outcome.Result == BeforeResult.Allowed) { allowed.Add(current); }
                    else if (outcome.Result == BeforeResult.Cancelled) { cancelled = true; }
                    else { denials.Add(string.IsNullOrEmpty(outcome.Message) ? "You can't " + reason + " " + current.ShortDescription + "." : outcome.Message); }
                    if (--remaining == 0) { finish(); }
                });
            }
        }

        private void AnnounceItemMoves(EventDispatcher dispatcher, bool hooked, string hook, string reason, Room room, IList<GameObject> moved)
        {
            if (dispatcher == null || moved == null || moved.Count == 0) { return; }
            if (!hooked && !dispatcher.WantsChangeEvents) { return; }
            foreach (GameObject item in moved)
            {
                var parties = new List<EventParty>(3)
                {
                    new EventParty("item", item, new[] { hook }),
                    new EventParty("actor", this, new[] { hook }),
                    new EventParty("room", room, new[] { hook })
                };
                dispatcher.EmitAfter(EventNames.ItemMove, parties, new Dictionary<string, object> { { "reason", reason }, { "actor", Id }, { "item", item.Id }, { "room", room.Id } });
            }
        }

        /// <summary>
        /// Checks the player, their environment, their equipment, and the global default commands to find a command that matches the provided input.
        /// </summary>
        public ICommand ResolveCommand(string input)
        {
            ICommand matchedCommand = null;

            if (CurrentLocation != null)
            {
                matchedCommand = CurrentLocation.RoomCommandSource.GetCommandFromInput(input);
            }
            if (matchedCommand != null) { return matchedCommand; }

            matchedCommand = GetKnownCommandSource().GetCommandFromInput(input);
            if (matchedCommand != null) { return matchedCommand; }

            if (Roles.Admin)
            {
                matchedCommand = _world.CreatorCommands.GetCommandFromInput(input);
                if (matchedCommand != null) { return matchedCommand; }
            }

            if (Roles.Creator)
            {
                matchedCommand = _world.CreatorCommands.GetCommandFromInput(input);
                if (matchedCommand != null) { return matchedCommand; }
            }

            matchedCommand = _world.DefaultCommands.GetCommandFromInput(input);
            if (matchedCommand != null) { return matchedCommand; }

            return null;
        }

        public CommandSource GetKnownCommandSource()
        {
            if (KnownCommands != null)
            {
                return _world.AllCommands.GetSubset(KnownCommands);
            }
            return new CommandSource();
        }

        public override bool IsMatchForString(string input)
        {
            return input == Gender?.ToLower()
                    || input == Race?.ToLower()
                    || input == Class?.ToLower()
                    || base.IsMatchForString(input);
        }
    }
}