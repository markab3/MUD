using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MUD.Contracts;
using MUD.Modules.Sdk;

namespace MUD.Modules.Desert
{
    /// <summary>
    /// The desert module (card P5-1, MODULES section 7): a sandstorm appears now and then, drifts from room to room, scours the livings
    /// who walk into its room, and ends by itself (D11: the effect times itself out and undoes itself idempotently).
    ///
    /// Behavior types (builders attach them to exterior desert rooms with the creator behavior command):
    ///   DesertStormRoom      hook heartbeat. Config { "next": ["room id", ...], "step_s": 10 }. When the storm is in this room and its step is due,
    ///                        the storm moves to the next room of the list. The list is authored adjacency: the module cannot see exits.
    ///   DesertStormExposure  hook living.enter, After phase. Config { "blind_s": 30 }. A living who arrives in the storm's room is scoured:
    ///                        a message, the component desert.sand_blind, and a timer that removes the component again.
    ///
    /// Where state lives (D13):
    ///   Component desert.storm on the room holding the storm   { storm_id, step_at, intensity }          player-visible, a builder can read it.
    ///   Component desert.sand_blind on a scoured living        { until, storm_id }
    ///   Feature   "a wall of swirling sand" on the storm room  shown by look (D71, D72); identified by its short text.
    ///   State document "storms"   { "<storm id>": { room, ends_at, step_at, steps, intensity } }   (times are Unix milliseconds)
    ///   State document "spawner"  { next_spawn_at }
    ///   State document "blind"    { "<living id>": until }
    ///
    /// Snapshots carry core components only (D57), so the module decides from its own state documents and never needs to read the storm
    /// component back. State is written BEFORE the components that mirror it: a crash between the two leaves state ahead of the world,
    /// and the start-up reconciliation (OnStart) makes the world match state again. Reconciliation is idempotent: running it twice changes nothing.
    /// The one thing state cannot reveal is a component left on an entity that state does not know (a lost state document, a hand edit);
    /// OnStart sweeps for those with QueryEntities (D58) when core answers it. Core answers Unimplemented today, so the sweep is logged and skipped.
    ///
    /// Capabilities requested: emit:component.set, emit:component.remove, emit:message.send, emit:feature.add, emit:feature.remove, state, timer.schedule, read.
    /// The module is flavor only (D60: fail open): when it is down nothing is blocked and nobody is told anything.
    /// </summary>
    public sealed class DesertModule
    {
        public const string ModuleName = "desert";
        public const string StormRoomType = "DesertStormRoom";
        public const string StormExposureType = "DesertStormExposure";

        public const string StormComponent = "desert.storm";
        public const string SandBlindComponent = "desert.sand_blind";
        public const string StormsKey = "storms";
        public const string SpawnerKey = "spawner";
        public const string BlindKey = "blind";
        public const string SpawnTimerId = "storm-spawn";
        public const string StormFeatureText = "a wall of swirling sand";

        private const string Intensity = "moderate";

        private sealed class Storm
        {
            public string Id;
            public string Room;
            public long EndsAt;
            public long StepAt;
            public int Steps;
            public string Intensity;
        }

        private readonly DesertSettings _settings;
        private readonly Func<DateTimeOffset> _clock;
        private readonly Random _random;
        private readonly Dictionary<string, Storm> _storms = new Dictionary<string, Storm>(StringComparer.Ordinal);
        private readonly Dictionary<string, long> _blind = new Dictionary<string, long>(StringComparer.Ordinal);
        private long _nextSpawnAt;

        public DesertModule(DesertSettings settings = null, Func<DateTimeOffset> clock = null, Random random = null)
        {
            _settings = settings ?? new DesertSettings();
            _clock = clock ?? (() => DateTimeOffset.UtcNow);
            _random = random ?? new Random();
        }

        /// <summary>The storms the module currently knows: storm id to room id. For tests and the log.</summary>
        public IReadOnlyDictionary<string, string> StormRooms
        {
            get { return _storms.ToDictionary(pair => pair.Key, pair => pair.Value.Room, StringComparer.Ordinal); }
        }

        public static ModuleDefinition Define(DesertModule instance = null)
        {
            DesertModule module = instance ?? new DesertModule();
            return ModuleDefinition.Create(ModuleName, "1.0.0")
                .Request("emit:component.set", "emit:component.remove", "emit:message.send", "emit:feature.add", "emit:feature.remove", "state", "timer.schedule", "read")
                .Behavior(StormRoomType, new[] { "heartbeat" }, module.OnHeartbeatAsync)
                .Behavior(StormExposureType, new[] { "living.enter" }, module.OnEnterAsync)
                .Start(module.OnStartAsync)
                .Timer(module.OnTimerAsync);
        }

        private long Now { get { return _clock().ToUnixTimeMilliseconds(); } }

        // ================= start-up reconciliation (D13, D74) =================

        private async Task OnStartAsync(ModuleContext context)
        {
            if (!context.IsGranted("state") || !context.IsGranted("timer.schedule"))
            {
                context.Log("The desert module needs the 'state' and 'timer.schedule' capabilities; without them no storm will run.");
                return;
            }

            await LoadStateAsync(context);
            long now = Now;

            // Storms: finished ones are cleaned up, running ones are made visible again and their end timers recreated (timers do not survive a reconnect, D74).
            foreach (Storm storm in _storms.Values.ToList())
            {
                if (storm.EndsAt <= now)
                {
                    await RemoveStormVisibleAsync(context, storm);
                    _storms.Remove(storm.Id);
                    continue;
                }
                await context.EmitAsync(StormVisibleEvents(storm));
                await ScheduleAsync(context, StormEndTimerId(storm.Id), storm.EndsAt, Payloads.Map(("storm", storm.Id)));
            }

            // Scoured livings: past due ones lose the component, the others get their timer back.
            foreach (KeyValuePair<string, long> entry in _blind.ToList())
            {
                if (entry.Value <= now)
                {
                    await context.RemoveComponentAsync(entry.Key, SandBlindComponent);
                    _blind.Remove(entry.Key);
                    continue;
                }
                await ScheduleAsync(context, BlindTimerId(entry.Key), entry.Value, Payloads.Map(("living", entry.Key)));
            }

            await SweepOrphansAsync(context);

            // The spawner: keep the saved time when there is one (past means soon), otherwise start a full interval from now.
            if (_nextSpawnAt <= 0) { _nextSpawnAt = now + _settings.SpawnIntervalSeconds * 1000L; }
            await ScheduleAsync(context, SpawnTimerId, _nextSpawnAt, null);

            await SaveStormsAsync(context);
            await SaveBlindAsync(context);
            await SaveSpawnerAsync(context);
            context.Log("Desert reconciled: " + _storms.Count + " storm(s), " + _blind.Count + " scoured living(s).");
        }

        private async Task LoadStateAsync(ModuleContext context)
        {
            _storms.Clear();
            _blind.Clear();
            _nextSpawnAt = 0;

            Struct storms = await context.GetStateAsync(StormsKey);
            if (storms != null)
            {
                foreach (KeyValuePair<string, Value> field in storms.Fields)
                {
                    if (field.Value.KindCase != Value.KindOneofCase.StructValue) { continue; }
                    Struct body = field.Value.StructValue;
                    string room = Payloads.GetText(body, "room");
                    if (string.IsNullOrEmpty(room)) { continue; }
                    _storms[field.Key] = new Storm
                    {
                        Id = field.Key,
                        Room = room,
                        EndsAt = (long)Payloads.GetNumber(body, "ends_at", 0),
                        StepAt = (long)Payloads.GetNumber(body, "step_at", 0),
                        Steps = (int)Payloads.GetNumber(body, "steps", 0),
                        Intensity = Payloads.GetText(body, "intensity") ?? Intensity
                    };
                }
            }

            Struct blind = await context.GetStateAsync(BlindKey);
            if (blind != null)
            {
                foreach (KeyValuePair<string, Value> field in blind.Fields)
                {
                    if (field.Value.KindCase == Value.KindOneofCase.NumberValue) { _blind[field.Key] = (long)field.Value.NumberValue; }
                }
            }

            Struct spawner = await context.GetStateAsync(SpawnerKey);
            _nextSpawnAt = (long)Payloads.GetNumber(spawner, "next_spawn_at", 0);
        }

        // Components the state documents do not know. Needs core to answer QueryEntities (D58); today it answers Unimplemented.
        private async Task SweepOrphansAsync(ModuleContext context)
        {
            if (!context.IsGranted("read")) { return; }
            try
            {
                string token = null;
                do
                {
                    QueryEntitiesReply reply = await context.QueryEntitiesAsync(component: StormComponent, pageToken: token);
                    foreach (EntityView entity in reply.Entities)
                    {
                        if (_storms.Values.Any(storm => storm.Room == entity.EntityId)) { continue; }
                        context.Log("Removing an orphaned storm from " + entity.EntityId + ".");
                        await context.EmitAsync(ModuleEvents.RemoveComponent(entity.EntityId, StormComponent), ModuleEvents.RemoveFeature(entity.EntityId, StormFeatureText));
                    }
                    token = string.IsNullOrEmpty(reply.NextPageToken) ? null : reply.NextPageToken;
                } while (token != null);

                token = null;
                do
                {
                    QueryEntitiesReply reply = await context.QueryEntitiesAsync(component: SandBlindComponent, pageToken: token);
                    foreach (EntityView entity in reply.Entities)
                    {
                        if (_blind.ContainsKey(entity.EntityId)) { continue; }
                        context.Log("Removing an orphaned sand blindness from " + entity.EntityId + ".");
                        await context.RemoveComponentAsync(entity.EntityId, SandBlindComponent);
                    }
                    token = string.IsNullOrEmpty(reply.NextPageToken) ? null : reply.NextPageToken;
                } while (token != null);
            }
            catch (RpcException ex) when (ex.StatusCode == StatusCode.Unimplemented)
            {
                context.Log("Core does not answer QueryEntities yet: the sweep for orphaned desert components is skipped.");
            }
            catch (RpcException ex)
            {
                context.Log("The sweep for orphaned desert components failed: " + ex.Status.Detail);
            }
        }

        // ================= timers =================

        private async Task OnTimerAsync(TimerFired fired, ModuleContext context)
        {
            if (!context.IsGranted("state") || !context.IsGranted("timer.schedule")) { return; }

            if (fired.TimerId == SpawnTimerId)
            {
                await SpawnAsync(context);
                return;
            }

            string stormId = Payloads.GetText(fired.Payload, "storm");
            if (stormId == null && fired.TimerId != null && fired.TimerId.StartsWith("storm-", StringComparison.Ordinal) && fired.TimerId.EndsWith("-end", StringComparison.Ordinal))
            {
                stormId = fired.TimerId.Substring("storm-".Length, fired.TimerId.Length - "storm-".Length - "-end".Length);
            }
            if (stormId != null)
            {
                await EndStormAsync(context, stormId);
                return;
            }

            string livingId = Payloads.GetText(fired.Payload, "living");
            if (livingId == null && fired.TimerId != null && fired.TimerId.StartsWith("blind-", StringComparison.Ordinal))
            {
                livingId = fired.TimerId.Substring("blind-".Length);
            }
            if (livingId != null) { await EndBlindAsync(context, livingId); }
        }

        private async Task SpawnAsync(ModuleContext context)
        {
            long now = Now;
            _nextSpawnAt = now + _settings.SpawnIntervalSeconds * 1000L;
            await SaveSpawnerAsync(context);
            await ScheduleAsync(context, SpawnTimerId, _nextSpawnAt, null);

            if (_storms.Count > 0) { return; } // one storm at a time
            if (_settings.StartRooms == null || _settings.StartRooms.Count == 0)
            {
                context.Log("No start rooms are configured; no storm is spawned.");
                return;
            }

            string room = _settings.StartRooms[_random.Next(_settings.StartRooms.Count)];
            var storm = new Storm
            {
                Id = "s" + now,
                Room = room,
                EndsAt = now + _settings.StormDurationSeconds * 1000L,
                StepAt = now + _settings.StepSeconds * 1000L,
                Steps = 0,
                Intensity = Intensity
            };
            _storms[storm.Id] = storm;
            if (!await SaveStormsAsync(context)) { _storms.Remove(storm.Id); return; }

            var events = new List<EventRecord>(StormVisibleEvents(storm));
            events.Add(ModuleEvents.Message(room, "A sandstorm rolls in.", ModuleEvents.Room));
            await context.EmitAsync(events);
            await ScheduleAsync(context, StormEndTimerId(storm.Id), storm.EndsAt, Payloads.Map(("storm", storm.Id)));
            context.Log("Storm " + storm.Id + " spawned in " + room + ".");
        }

        // Idempotent (D11): a storm that is already gone does nothing.
        private async Task EndStormAsync(ModuleContext context, string stormId)
        {
            Storm storm;
            if (!_storms.TryGetValue(stormId, out storm)) { return; }
            _storms.Remove(stormId);
            if (!await SaveStormsAsync(context)) { _storms[stormId] = storm; return; }
            await RemoveStormVisibleAsync(context, storm);
            await context.SendMessageAsync(storm.Room, "The sandstorm dies away.", ModuleEvents.Room);
            context.Log("Storm " + stormId + " ended in " + storm.Room + ".");
        }

        private async Task EndBlindAsync(ModuleContext context, string livingId)
        {
            long until;
            if (!_blind.TryGetValue(livingId, out until)) { return; }
            if (until > Now + 50)
            {
                // Scoured again since this timer was set: the later end time is the one that counts.
                await ScheduleAsync(context, BlindTimerId(livingId), until, Payloads.Map(("living", livingId)));
                return;
            }
            _blind.Remove(livingId);
            if (!await SaveBlindAsync(context)) { _blind[livingId] = until; return; }
            await context.RemoveComponentAsync(livingId, SandBlindComponent);
        }

        // ================= behaviors =================

        // Heartbeat on a room that carries the entry (D56). Moves the storm on when its step is due; a room without the storm does nothing.
        private async Task<BehaviorResult> OnHeartbeatAsync(BehaviorCall call, ModuleContext context)
        {
            PartySnapshot self = call.Party("self");
            if (self == null || _storms.Count == 0 || !context.IsGranted("state")) { return BehaviorResult.Allow(); }

            Storm storm = _storms.Values.FirstOrDefault(each => each.Room == self.EntityId);
            long now = Now;
            if (storm == null || now < storm.StepAt) { return BehaviorResult.Allow(); }

            int stepSeconds = (int)Payloads.GetNumber(call.Config, "step_s", _settings.StepSeconds);
            List<string> next = TextList(call.Config, "next");
            string previousRoom = storm.Room;
            int previousSteps = storm.Steps;
            long previousStepAt = storm.StepAt;

            storm.Steps++;
            storm.StepAt = now + Math.Max(1, stepSeconds) * 1000L;
            if (next.Count > 0) { storm.Room = next[(storm.Steps - 1) % next.Count]; }
            bool moved = storm.Room != previousRoom;

            if (storm.Steps >= _settings.MaxSteps)
            {
                // The storm has drifted as far as it may: it ends here and now; its end timer later finds nothing to do.
                _storms.Remove(storm.Id);
                if (!await SaveStormsAsync(context)) { _storms[storm.Id] = storm; storm.Room = previousRoom; storm.Steps = previousSteps; storm.StepAt = previousStepAt; return BehaviorResult.Allow(); }
                Storm ended = new Storm { Id = storm.Id, Room = previousRoom };
                await RemoveStormVisibleAsync(context, ended);
                await context.SendMessageAsync(previousRoom, "The sandstorm dies away.", ModuleEvents.Room);
                return BehaviorResult.Allow();
            }

            if (!await SaveStormsAsync(context))
            {
                storm.Room = previousRoom; storm.Steps = previousSteps; storm.StepAt = previousStepAt;
                return BehaviorResult.Allow();
            }

            var emitted = new List<EventRecord>();
            if (moved)
            {
                emitted.Add(ModuleEvents.RemoveComponent(previousRoom, StormComponent));
                emitted.Add(ModuleEvents.RemoveFeature(previousRoom, StormFeatureText));
                emitted.Add(ModuleEvents.Message(previousRoom, "The storm moves on.", ModuleEvents.Room));
                emitted.AddRange(StormVisibleEvents(storm));
                emitted.Add(ModuleEvents.Message(storm.Room, "A sandstorm rolls in.", ModuleEvents.Room));
            }
            else
            {
                emitted.Add(ModuleEvents.SetComponent(storm.Room, StormComponent, StormComponentData(storm)));
            }
            return BehaviorResult.Allow().WithEmitted(emitted.ToArray());
        }

        // A living arrives in a room carrying the entry (D54). After phase only; the Before phase has nothing to say.
        private async Task<BehaviorResult> OnEnterAsync(BehaviorCall call, ModuleContext context)
        {
            if (call.IsBefore || !context.IsGranted("state") || !context.IsGranted("timer.schedule")) { return BehaviorResult.Allow(); }

            PartySnapshot mover = call.Party("mover");
            PartySnapshot destination = call.Party("to");
            if (mover == null || destination == null) { return BehaviorResult.Allow(); }

            Storm storm = _storms.Values.FirstOrDefault(each => each.Room == destination.EntityId);
            if (storm == null) { return BehaviorResult.Allow(); }

            int blindSeconds = (int)Payloads.GetNumber(call.Config, "blind_s", _settings.BlindSeconds);
            long until = Now + Math.Max(1, blindSeconds) * 1000L;
            long previous;
            bool had = _blind.TryGetValue(mover.EntityId, out previous);
            _blind[mover.EntityId] = until;
            if (!await SaveBlindAsync(context))
            {
                if (had) { _blind[mover.EntityId] = previous; } else { _blind.Remove(mover.EntityId); }
                return BehaviorResult.Allow();
            }

            await ScheduleAsync(context, BlindTimerId(mover.EntityId), until, Payloads.Map(("living", mover.EntityId)));
            return BehaviorResult.Allow().WithEmitted(
                ModuleEvents.Message(mover.EntityId, "Stinging sand scours your face.", ModuleEvents.Combat),
                ModuleEvents.SetComponent(mover.EntityId, SandBlindComponent, Payloads.Map(("until", until), ("storm_id", storm.Id))));
        }

        // ================= helpers =================

        private static string StormEndTimerId(string stormId) { return "storm-" + stormId + "-end"; }

        private static string BlindTimerId(string livingId) { return "blind-" + livingId; }

        private static Struct StormComponentData(Storm storm)
        {
            return Payloads.Map(("storm_id", storm.Id), ("step_at", storm.StepAt), ("intensity", storm.Intensity));
        }

        // The component and the Feature that show a storm in its room (D13, D72).
        private static List<EventRecord> StormVisibleEvents(Storm storm)
        {
            return new List<EventRecord>
            {
                ModuleEvents.SetComponent(storm.Room, StormComponent, StormComponentData(storm)),
                ModuleEvents.AddFeature(storm.Room, StormFeatureText,
                    longText: "A wall of swirling sand fills the air, stinging and blinding.",
                    roomLine: "A wall of swirling sand roars through here.",
                    detail: "The sand is so thick you can barely see your own hands.",
                    handles: new[] { "sand", "storm", "sandstorm", "wall" })
            };
        }

        private static Task RemoveStormVisibleAsync(ModuleContext context, Storm storm)
        {
            return context.EmitAsync(ModuleEvents.RemoveComponent(storm.Room, StormComponent), ModuleEvents.RemoveFeature(storm.Room, StormFeatureText));
        }

        private static async Task ScheduleAsync(ModuleContext context, string timerId, long atUnixMilliseconds, Struct payload)
        {
            try { await context.ScheduleTimerAtAsync(timerId, DateTimeOffset.FromUnixTimeMilliseconds(atUnixMilliseconds), payload); }
            catch (ModuleCallException ex) { context.Log("Timer '" + timerId + "' was not scheduled: " + ex.Message); }
        }

        private async Task<bool> SaveStormsAsync(ModuleContext context)
        {
            var document = new Struct();
            foreach (Storm storm in _storms.Values)
            {
                document.Fields[storm.Id] = Value.ForStruct(Payloads.Map(("room", storm.Room), ("ends_at", storm.EndsAt), ("step_at", storm.StepAt), ("steps", storm.Steps), ("intensity", storm.Intensity)));
            }
            return await SaveAsync(context, StormsKey, document);
        }

        private async Task<bool> SaveBlindAsync(ModuleContext context)
        {
            var document = new Struct();
            foreach (KeyValuePair<string, long> entry in _blind) { document.Fields[entry.Key] = Value.ForNumber(entry.Value); }
            return await SaveAsync(context, BlindKey, document);
        }

        private async Task<bool> SaveSpawnerAsync(ModuleContext context)
        {
            return await SaveAsync(context, SpawnerKey, Payloads.Map(("next_spawn_at", _nextSpawnAt)));
        }

        private static async Task<bool> SaveAsync(ModuleContext context, string key, Struct document)
        {
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
