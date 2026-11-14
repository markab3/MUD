using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MUD.Contracts;
using MUD.Modules.Sdk;

namespace MUD.Modules.Temple
{
    /// <summary>
    /// The temple module (card P5-2, MODULES section 8): trapped rooms that drop a walker into a pit, and a lever that disarms a group of
    /// traps until they rearm by themselves. Flavor only: nothing is ever blocked when the module is down (D60, fail open).
    ///
    /// Behavior types (builders attach them with the creator behavior command):
    ///   DesertTempleTrap   hook living.enter, Before phase, on the trapped room.
    ///                      Config { "pit_room": "temple.pit", "group": "main", "actor_msg": "The floor gives way!",
    ///                               "room_msg": "vanishes through the floor!", "rearm_after_s": 600 }.
    ///                      An armed trap answers Deny with actor_msg and emits a redirect (D6): a new entity.move of the walker to the pit
    ///                      room (reason "trap"), the component temple.trap { armed: false }, and a message to the room being left.
    ///   TempleLeverRoom    hook command.entered, on the room that holds the lever (D14: Features are inert, so the lever is room level).
    ///                      Config { "verb": "pull", "noun": "lever", "group": "main", "traps": ["temple.corridor_2", ...], "rearm_after_s": 600 }.
    ///                      A line that is not "verb noun" is answered Allow, not handled, and core goes on as if the module were not there.
    ///                      "verb noun" is taken (handled flag, D63): the first pull disarms every trap named in Config.traps; later pulls
    ///                      before the rearm get "The lever will not budge."
    ///
    /// The two-player race (G6, D62): a module is one process and the SDK hands it one delivery at a time. The trap marks itself spent
    /// in memory BEFORE the first await, so the second player who entered at the same moment finds it spent, is answered Allow and walks in
    /// safely. No core-side precondition is involved. The lever works the same way for two players pulling together.
    ///
    /// Where state lives (D13). Snapshots carry core components only (D57) and core does not yet answer GetEntity, so the module decides
    /// from its own state documents and never reads a component back:
    ///   State document "traps"   { "&lt;trap room id&gt;": { group, rearm_at } }   the traps that are spent (sprung or disarmed); absent means armed
    ///   State document "levers"  { "&lt;lever room id&gt;": { group, rearm_at } }  the levers that are pulled; absent means ready
    ///   Component temple.trap { armed, group }  on a trap room, shown to builders and other behaviors
    ///   Component temple.lever { pulled }       on the lever room
    /// Times are Unix milliseconds. State is written BEFORE the components that mirror it, so after a crash the world is behind state and
    /// the start-up reconciliation (OnStart) brings it level; it is idempotent. Timers do not survive a restart (D74): OnStart recreates them.
    ///
    /// Capabilities requested: emit:entity.move, emit:component.set, emit:message.send, state, timer.schedule, read.
    /// </summary>
    public sealed class TempleModule
    {
        public const string ModuleName = "temple";
        public const string TrapType = "DesertTempleTrap";
        public const string LeverType = "TempleLeverRoom";

        public const string TrapComponent = "temple.trap";
        public const string LeverComponent = "temple.lever";
        public const string TrapsKey = "traps";
        public const string LeversKey = "levers";
        public const string TrapReason = "trap";

        public const string DefaultActorMessage = "The floor gives way!";
        public const string DefaultRoomMessage = "vanishes through the floor!";
        public const string LeverRefusedMessage = "The lever will not budge.";
        public const string LeverPulledMessage = "You haul the lever. Somewhere deep in the stone, gears grind.";
        public const int DefaultRearmSeconds = 600;

        private const string TrapTimerPrefix = "rearm-trap-";
        private const string LeverTimerPrefix = "rearm-lever-";

        private sealed class Spent
        {
            public string Group;
            public long RearmAt;
        }

        private readonly Func<DateTimeOffset> _clock;
        private readonly int _defaultRearmSeconds;
        private readonly Dictionary<string, Spent> _traps = new Dictionary<string, Spent>(StringComparer.Ordinal);
        private readonly Dictionary<string, Spent> _levers = new Dictionary<string, Spent>(StringComparer.Ordinal);

        public TempleModule(Func<DateTimeOffset> clock = null, int defaultRearmSeconds = DefaultRearmSeconds)
        {
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _defaultRearmSeconds = defaultRearmSeconds > 0 ? defaultRearmSeconds : DefaultRearmSeconds;
        }

        /// <summary>The trap rooms the module currently holds as spent. For tests and the log.</summary>
        public IReadOnlyCollection<string> SpentTraps { get { return _traps.Keys.ToList(); } }

        /// <summary>The lever rooms the module currently holds as pulled. For tests and the log.</summary>
        public IReadOnlyCollection<string> PulledLevers { get { return _levers.Keys.ToList(); } }

        public static ModuleDefinition Define(TempleModule instance = null)
        {
            TempleModule module = instance ?? new TempleModule();
            return ModuleDefinition.Create(ModuleName, "1.0.0")
                .Request("emit:entity.move", "emit:component.set", "emit:message.send", "state", "timer.schedule", "read")
                .Behavior(TrapType, new[] { "living.enter" }, module.OnTrapAsync)
                .Behavior(LeverType, new[] { "command.entered" }, module.OnLeverAsync)
                .Start(module.OnStartAsync)
                .Timer(module.OnTimerAsync);
        }

        private long Now { get { return _clock().ToUnixTimeMilliseconds(); } }

        // ================= start-up reconciliation (D13, D74) =================

        private async Task OnStartAsync(ModuleContext context)
        {
            if (!context.IsGranted("state") || !context.IsGranted("timer.schedule"))
            {
                context.Log("The temple module needs the 'state' and 'timer.schedule' capabilities; without them no trap or lever will work.");
                return;
            }

            await LoadAsync(context, TrapsKey, _traps);
            await LoadAsync(context, LeversKey, _levers);
            long now = Now;

            // Spent traps: past due ones are rearmed at once, the others are shown as spent again and get their timer back.
            foreach (KeyValuePair<string, Spent> entry in _traps.ToList())
            {
                if (entry.Value.RearmAt <= now)
                {
                    _traps.Remove(entry.Key);
                    await context.SetComponentAsync(entry.Key, TrapComponent, TrapData(true, entry.Value.Group));
                    continue;
                }
                await context.SetComponentAsync(entry.Key, TrapComponent, TrapData(false, entry.Value.Group));
                await ScheduleAsync(context, TrapTimerPrefix + entry.Key, entry.Value.RearmAt, entry.Key);
            }

            foreach (KeyValuePair<string, Spent> entry in _levers.ToList())
            {
                if (entry.Value.RearmAt <= now)
                {
                    _levers.Remove(entry.Key);
                    await context.SetComponentAsync(entry.Key, LeverComponent, LeverData(false));
                    continue;
                }
                await context.SetComponentAsync(entry.Key, LeverComponent, LeverData(true));
                await ScheduleAsync(context, LeverTimerPrefix + entry.Key, entry.Value.RearmAt, entry.Key);
            }

            await SweepOrphansAsync(context);
            await SaveAsync(context, TrapsKey, _traps);
            await SaveAsync(context, LeversKey, _levers);
            context.Log("Temple reconciled: " + _traps.Count + " spent trap(s), " + _levers.Count + " pulled lever(s).");
        }

        private static async Task LoadAsync(ModuleContext context, string key, Dictionary<string, Spent> target)
        {
            target.Clear();
            Struct document = await context.GetStateAsync(key);
            if (document == null) { return; }
            foreach (KeyValuePair<string, Value> field in document.Fields)
            {
                if (field.Value.KindCase != Value.KindOneofCase.StructValue) { continue; }
                Struct body = field.Value.StructValue;
                target[field.Key] = new Spent { Group = Payloads.GetText(body, "group") ?? string.Empty, RearmAt = (long)Payloads.GetNumber(body, "rearm_at", 0) };
            }
        }

        // Components the state documents do not know (a lost document, a hand edit): put back to the resting value. Needs core to answer
        // QueryEntities (D58); core answers Unimplemented today, so the sweep is logged and skipped.
        private async Task SweepOrphansAsync(ModuleContext context)
        {
            if (!context.IsGranted("read")) { return; }
            try
            {
                string token = null;
                do
                {
                    QueryEntitiesReply reply = await context.QueryEntitiesAsync(component: TrapComponent, pageToken: token);
                    foreach (EntityView entity in reply.Entities)
                    {
                        if (_traps.ContainsKey(entity.EntityId)) { continue; }
                        context.Log("Rearming an orphaned trap at " + entity.EntityId + ".");
                        await context.SetComponentAsync(entity.EntityId, TrapComponent, TrapData(true, string.Empty));
                    }
                    token = string.IsNullOrEmpty(reply.NextPageToken) ? null : reply.NextPageToken;
                } while (token != null);

                token = null;
                do
                {
                    QueryEntitiesReply reply = await context.QueryEntitiesAsync(component: LeverComponent, pageToken: token);
                    foreach (EntityView entity in reply.Entities)
                    {
                        if (_levers.ContainsKey(entity.EntityId)) { continue; }
                        context.Log("Resetting an orphaned lever at " + entity.EntityId + ".");
                        await context.SetComponentAsync(entity.EntityId, LeverComponent, LeverData(false));
                    }
                    token = string.IsNullOrEmpty(reply.NextPageToken) ? null : reply.NextPageToken;
                } while (token != null);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                context.Log("Core does not answer QueryEntities yet: the sweep for orphaned temple components is skipped.");
            }
            catch (RpcException ex)
            {
                context.Log("The sweep for orphaned temple components failed: " + ex.Status.Detail);
            }
        }

        // ================= timers =================

        private async Task OnTimerAsync(TimerFired fired, ModuleContext context)
        {
            if (!context.IsGranted("state") || !context.IsGranted("timer.schedule") || fired.TimerId == null) { return; }

            if (fired.TimerId.StartsWith(TrapTimerPrefix, StringComparison.Ordinal))
            {
                await RearmTrapAsync(context, fired.TimerId.Substring(TrapTimerPrefix.Length));
            }
            else if (fired.TimerId.StartsWith(LeverTimerPrefix, StringComparison.Ordinal))
            {
                await ResetLeverAsync(context, fired.TimerId.Substring(LeverTimerPrefix.Length));
            }
        }

        // Idempotent (D11): a trap that is not spent does nothing.
        private async Task RearmTrapAsync(ModuleContext context, string roomId)
        {
            Spent spent;
            if (!_traps.TryGetValue(roomId, out spent)) { return; }
            if (spent.RearmAt > Now + 50)
            {
                // Spent again since this timer was set (sprung after a lever disarm, or the reverse): the later time is the one that counts.
                await ScheduleAsync(context, TrapTimerPrefix + roomId, spent.RearmAt, roomId);
                return;
            }
            _traps.Remove(roomId);
            if (!await SaveAsync(context, TrapsKey, _traps)) { _traps[roomId] = spent; return; }
            await context.SetComponentAsync(roomId, TrapComponent, TrapData(true, spent.Group));
        }

        private async Task ResetLeverAsync(ModuleContext context, string roomId)
        {
            Spent spent;
            if (!_levers.TryGetValue(roomId, out spent)) { return; }
            if (spent.RearmAt > Now + 50)
            {
                await ScheduleAsync(context, LeverTimerPrefix + roomId, spent.RearmAt, roomId);
                return;
            }
            _levers.Remove(roomId);
            if (!await SaveAsync(context, LeversKey, _levers)) { _levers[roomId] = spent; return; }
            await context.SetComponentAsync(roomId, LeverComponent, LeverData(false));
        }

        // ================= behaviors =================

        // Before a living enters a room that carries the entry (D54). Deny plus a new entity.move is the redirect of D6.
        private async Task<BehaviorResult> OnTrapAsync(BehaviorCall call, ModuleContext context)
        {
            if (!call.IsBefore || !context.IsGranted("state") || !context.IsGranted("timer.schedule")) { return BehaviorResult.Allow(); }

            PartySnapshot mover = call.Party("mover");
            PartySnapshot room = call.Party("to");
            if (mover == null || room == null) { return BehaviorResult.Allow(); }
            if (Payloads.GetText(call.Payload, "reason") == TrapReason) { return BehaviorResult.Allow(); } // never trap the fall itself

            string pitRoom = Payloads.GetText(call.Config, "pit_room");
            if (string.IsNullOrEmpty(pitRoom) || pitRoom == room.EntityId)
            {
                context.Log("The trap at " + room.EntityId + " has no usable pit_room in its config; it does nothing.");
                return BehaviorResult.Allow();
            }

            // The race (G6, D62): spent in memory before the first await, so the second walker sees the first one's result.
            if (_traps.ContainsKey(room.EntityId)) { return BehaviorResult.Allow(); }
            string group = Payloads.GetText(call.Config, "group") ?? string.Empty;
            int rearmSeconds = (int)Payloads.GetNumber(call.Config, "rearm_after_s", _defaultRearmSeconds);
            long rearmAt = Now + Math.Max(1, rearmSeconds) * 1000L;
            _traps[room.EntityId] = new Spent { Group = group, RearmAt = rearmAt };

            if (!await SaveAsync(context, TrapsKey, _traps))
            {
                _traps.Remove(room.EntityId); // not saved: the trap does not spring (fail open)
                return BehaviorResult.Allow();
            }
            await ScheduleAsync(context, TrapTimerPrefix + room.EntityId, rearmAt, room.EntityId);

            PartySnapshot from = call.Party("from");
            string fromRoom = from != null ? from.EntityId : Payloads.GetText(call.Payload, "from");
            string roomMessage = Payloads.GetText(call.Config, "room_msg") ?? DefaultRoomMessage;
            var emitted = new List<EventRecord>();
            if (!string.IsNullOrEmpty(fromRoom)) { emitted.Add(ModuleEvents.Message(fromRoom, NameOf(mover) + " " + roomMessage, ModuleEvents.Room)); }
            emitted.Add(ModuleEvents.SetComponent(room.EntityId, TrapComponent, TrapData(false, group, mover.EntityId)));
            emitted.Add(ModuleEvents.MoveEntity(mover.EntityId, pitRoom, TrapReason));
            context.Log("Trap at " + room.EntityId + " sprung by " + mover.EntityId + "; rearms in " + rearmSeconds + " s.");
            return BehaviorResult.Deny(Payloads.GetText(call.Config, "actor_msg") ?? DefaultActorMessage).WithEmitted(emitted.ToArray());
        }

        // A typed line offered to the room's lever (D63). Only "verb noun" is taken; any other line is not handled and goes on.
        private async Task<BehaviorResult> OnLeverAsync(BehaviorCall call, ModuleContext context)
        {
            if (!call.IsBefore || !context.IsGranted("state") || !context.IsGranted("timer.schedule")) { return BehaviorResult.Allow(); }

            string verb = (Payloads.GetText(call.Config, "verb") ?? "pull").Trim();
            string noun = (Payloads.GetText(call.Config, "noun") ?? "lever").Trim();
            string[] words = (Payloads.GetText(call.Payload, "line") ?? string.Empty).Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length != 2 || !string.Equals(words[0], verb, StringComparison.OrdinalIgnoreCase) || !string.Equals(words[1], noun, StringComparison.OrdinalIgnoreCase))
            {
                return BehaviorResult.Allow();
            }

            PartySnapshot actor = call.Party("actor");
            PartySnapshot room = call.Party("room");
            if (actor == null || room == null) { return BehaviorResult.Allow(); }

            // The race (D62): the second puller finds the lever already pulled.
            if (_levers.ContainsKey(room.EntityId))
            {
                return BehaviorResult.AllowAndHandled().WithEmitted(ModuleEvents.Message(actor.EntityId, LeverRefusedMessage, ModuleEvents.System));
            }

            string group = Payloads.GetText(call.Config, "group") ?? string.Empty;
            int rearmSeconds = (int)Payloads.GetNumber(call.Config, "rearm_after_s", _defaultRearmSeconds);
            long rearmAt = Now + Math.Max(1, rearmSeconds) * 1000L;
            List<string> trapRooms = TextList(call.Config, "traps");

            var previousTraps = new Dictionary<string, Spent>(_traps, StringComparer.Ordinal);
            _levers[room.EntityId] = new Spent { Group = group, RearmAt = rearmAt };
            foreach (string trapRoom in trapRooms)
            {
                Spent existing;
                long later = _traps.TryGetValue(trapRoom, out existing) ? Math.Max(existing.RearmAt, rearmAt) : rearmAt;
                _traps[trapRoom] = new Spent { Group = group, RearmAt = later };
            }

            if (!await SaveAsync(context, LeversKey, _levers) || !await SaveAsync(context, TrapsKey, _traps))
            {
                _levers.Remove(room.EntityId);
                _traps.Clear();
                foreach (KeyValuePair<string, Spent> entry in previousTraps) { _traps[entry.Key] = entry.Value; }
                return BehaviorResult.Allow();
            }

            await ScheduleAsync(context, LeverTimerPrefix + room.EntityId, rearmAt, room.EntityId);
            var emitted = new List<EventRecord>
            {
                ModuleEvents.SetComponent(room.EntityId, LeverComponent, LeverData(true)),
                ModuleEvents.Message(actor.EntityId, LeverPulledMessage, ModuleEvents.System),
                ModuleEvents.Message(room.EntityId, NameOf(actor) + " pulls a lever. Gears grind in the walls.", ModuleEvents.Room)
            };
            foreach (string trapRoom in trapRooms)
            {
                await ScheduleAsync(context, TrapTimerPrefix + trapRoom, _traps[trapRoom].RearmAt, trapRoom);
                emitted.Add(ModuleEvents.SetComponent(trapRoom, TrapComponent, TrapData(false, group)));
            }
            context.Log("Lever at " + room.EntityId + " pulled by " + actor.EntityId + "; " + trapRooms.Count + " trap(s) disarmed for " + rearmSeconds + " s.");
            return BehaviorResult.AllowAndHandled().WithEmitted(emitted.ToArray());
        }

        // ================= helpers =================

        private static string NameOf(PartySnapshot party)
        {
            string name = party.Components == null ? null : Payloads.GetText(party.Components, "shortDescription");
            return string.IsNullOrEmpty(name) ? "Someone" : name;
        }

        private static Struct TrapData(bool armed, string group, string firedBy = null)
        {
            Struct data = Payloads.Map(("armed", armed), ("group", group ?? string.Empty));
            if (firedBy != null) { data.Fields["fired_by"] = Value.ForString(firedBy); }
            return data;
        }

        private static Struct LeverData(bool pulled)
        {
            return Payloads.Map(("pulled", pulled));
        }

        private static async Task ScheduleAsync(ModuleContext context, string timerId, long atUnixMilliseconds, string roomId)
        {
            try { await context.ScheduleTimerAtAsync(timerId, DateTimeOffset.FromUnixTimeMilliseconds(atUnixMilliseconds), Payloads.Map(("room", roomId))); }
            catch (ModuleCallException ex) { context.Log("Timer '" + timerId + "' was not scheduled: " + ex.Message); }
        }

        private static async Task<bool> SaveAsync(ModuleContext context, string key, Dictionary<string, Spent> entries)
        {
            var document = new Struct();
            foreach (KeyValuePair<string, Spent> entry in entries)
            {
                document.Fields[entry.Key] = Value.ForStruct(Payloads.Map(("group", entry.Value.Group), ("rearm_at", entry.Value.RearmAt)));
            }
            try
            {
                await context.SetStateAsync(key, document);
                return true;
            }
            catch (ModuleCallException ex)
            {
                context.Log("The state document '" + key + "' was not saved: " + ex.Message);
                return false;
            }
        }

        private static List<string> TextList(Struct map, string key)
        {
            var result = new List<string>();
            Value value;
            if (map != null && map.Fields.TryGetValue(key, out value) && value.KindCase == Value.KindOneofCase.ListValue)
            {
                foreach (Value item in value.ListValue.Values)
                {
                    if (item.KindCase == Value.KindOneofCase.StringValue && item.StringValue.Length > 0) { result.Add(item.StringValue); }
                }
            }
            return result;
        }
    }
}
