using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;
using MUD.Contracts;

namespace MUD.Modules.Sdk
{
    /// <summary>A call to core was answered with a refusal (a missing capability, a limit, a bad key). The message is core's own words.</summary>
    public sealed class ModuleCallException : Exception
    {
        public ModuleCallException(string message) : base(message) { }
    }

    /// <summary>
    /// What a behavior uses to call core (owner decision D40): named helper methods over the dedicated wire calls (state, timers, reads)
    /// and the one generic Emit. Every helper that emits needs the matching emit:&lt;event name&gt; capability in the grants file (D65);
    /// SetState and GetState need "state"; ScheduleTimer and CancelTimer need "timer.schedule"; the reads need "read". A refused call
    /// throws ModuleCallException with core's message, except Emit helpers, which return the refusals (D61: valid events still apply).
    /// </summary>
    public sealed class ModuleContext
    {
        private readonly ModuleHost.ModuleHostClient _client;
        private readonly Action<string> _log;

        public ModuleContext(ModuleHost.ModuleHostClient client, string moduleName, string connectionId, IReadOnlyList<string> granted, Action<string> log = null)
        {
            _client = client ?? throw new ArgumentNullException(nameof(client));
            ModuleName = moduleName;
            ConnectionId = connectionId;
            Granted = granted ?? new string[0];
            _log = log ?? Console.WriteLine;
        }

        internal ModuleHost.ModuleHostClient Client { get { return _client; } }

        public string ModuleName { get; }
        public string ConnectionId { get; }

        /// <summary>The capabilities core granted at Hello (the manifest's request minus what the grants file did not allow).</summary>
        public IReadOnlyList<string> Granted { get; }

        /// <summary>True when core granted the capability (exact string, for example "state" or "emit:message.send").</summary>
        public bool IsGranted(string capability)
        {
            for (int index = 0; index < Granted.Count; index++)
            {
                if (string.Equals(Granted[index], capability, StringComparison.Ordinal)) { return true; }
            }
            return false;
        }

        /// <summary>Writes one line to the module's standard output. Core re-logs it with a [module:name] prefix (P4-3).</summary>
        public void Log(string text) { _log(text); }

        // ---- emit ----

        /// <summary>Sends events on their own, outside a verdict. The cause is the delivery being handled, when there is one.</summary>
        public async Task<IReadOnlyList<EmitRefusal>> EmitAsync(IEnumerable<EventRecord> events, string causeId = null, CancellationToken cancel = default(CancellationToken))
        {
            var request = new EmitRequest { ConnectionId = ConnectionId, CauseId = causeId ?? string.Empty };
            request.Events.AddRange(events);
            if (request.Events.Count == 0) { return new EmitRefusal[0]; }
            EmitReply reply = await _client.EmitAsync(request, cancellationToken: cancel);
            foreach (EmitRefusal refusal in reply.Refusals)
            {
                Log("core refused emitted event " + refusal.Index + " '" + refusal.EventName + "': " + refusal.Reason);
            }
            return reply.Refusals.ToList();
        }

        public Task<IReadOnlyList<EmitRefusal>> EmitAsync(params EventRecord[] events) { return EmitAsync((IEnumerable<EventRecord>)events); }

        /// <summary>message.send: text in %^Color%^ markup to a living or a room, on a channel (say, tell, room, combat, system, chat; null is system, D44).</summary>
        public Task<IReadOnlyList<EmitRefusal>> SendMessageAsync(string targetId, string text, string channel = null)
        {
            return EmitAsync(ModuleEvents.Message(targetId, text, channel));
        }

        /// <summary>component.set: writes a component whose name starts with this module's name (D50).</summary>
        public Task<IReadOnlyList<EmitRefusal>> SetComponentAsync(string entityId, string component, Struct data)
        {
            return EmitAsync(ModuleEvents.SetComponent(entityId, component, data));
        }

        public Task<IReadOnlyList<EmitRefusal>> RemoveComponentAsync(string entityId, string component)
        {
            return EmitAsync(ModuleEvents.RemoveComponent(entityId, component));
        }

        /// <summary>feature.add: shows a temporary condition in `look` by adding a Feature to a room (D71, D72).</summary>
        public Task<IReadOnlyList<EmitRefusal>> AddFeatureAsync(string roomId, string shortText, string longText = null, string roomLine = null, string detail = null, IEnumerable<string> handles = null)
        {
            return EmitAsync(ModuleEvents.AddFeature(roomId, shortText, longText, roomLine, detail, handles));
        }

        public Task<IReadOnlyList<EmitRefusal>> RemoveFeatureAsync(string roomId, string shortText)
        {
            return EmitAsync(ModuleEvents.RemoveFeature(roomId, shortText));
        }

        public Task<IReadOnlyList<EmitRefusal>> MoveEntityAsync(string moverId, string destinationRoomId, string reason = null)
        {
            return EmitAsync(ModuleEvents.MoveEntity(moverId, destinationRoomId, reason));
        }

        public Task<IReadOnlyList<EmitRefusal>> AddBehaviorAsync(string entityId, string hook, string module, string type, int priority = 0, Struct config = null)
        {
            return EmitAsync(ModuleEvents.AddBehavior(entityId, hook, module, type, priority, config));
        }

        public Task<IReadOnlyList<EmitRefusal>> RemoveBehaviorAsync(string entityId, string hook, string module, string type)
        {
            return EmitAsync(ModuleEvents.RemoveBehavior(entityId, hook, module, type));
        }

        // ---- state (D12, D73) ----

        /// <summary>Replaces the whole document under the key (last write wins). Limits on size and rate are core's settings.</summary>
        public async Task SetStateAsync(string key, Struct document, CancellationToken cancel = default(CancellationToken))
        {
            Ack ack = await _client.SetStateAsync(new SetStateRequest { ConnectionId = ConnectionId, Key = key, Doc = document ?? new Struct() }, cancellationToken: cancel);
            Check(ack);
        }

        /// <summary>The document under the key, or null when there is none.</summary>
        public async Task<Struct> GetStateAsync(string key, CancellationToken cancel = default(CancellationToken))
        {
            GetStateReply reply = await _client.GetStateAsync(new GetStateRequest { ConnectionId = ConnectionId, Key = key }, cancellationToken: cancel);
            return reply.Found ? reply.Doc ?? new Struct() : null;
        }

        // ---- timers (D74: held in memory in core, gone after a reconnect; recreate them in OnStart) ----

        public Task ScheduleTimerAsync(string timerId, TimeSpan delay, Struct payload = null, CancellationToken cancel = default(CancellationToken))
        {
            return ScheduleTimerAtAsync(timerId, DateTimeOffset.UtcNow + delay, payload, cancel);
        }

        public async Task ScheduleTimerAtAsync(string timerId, DateTimeOffset at, Struct payload = null, CancellationToken cancel = default(CancellationToken))
        {
            Ack ack = await _client.ScheduleTimerAsync(new TimerRequest { ConnectionId = ConnectionId, TimerId = timerId, AtUnixMs = at.ToUnixTimeMilliseconds(), Payload = payload ?? new Struct() }, cancellationToken: cancel);
            Check(ack);
        }

        public async Task CancelTimerAsync(string timerId, CancellationToken cancel = default(CancellationToken))
        {
            Ack ack = await _client.CancelTimerAsync(new CancelTimerRequest { ConnectionId = ConnectionId, TimerId = timerId }, cancellationToken: cancel);
            Check(ack);
        }

        // ---- reads (D58). Core does not answer these yet (P4-4 left them out); they throw RpcException Unimplemented until it does. ----

        public async Task<EntityView> GetEntityAsync(string entityId, CancellationToken cancel = default(CancellationToken))
        {
            GetEntityReply reply = await _client.GetEntityAsync(new GetEntityRequest { ConnectionId = ConnectionId, EntityId = entityId }, cancellationToken: cancel);
            return reply.Found ? reply.Entity : null;
        }

        public Task<QueryEntitiesReply> QueryEntitiesAsync(string area = null, string component = null, uint limit = 0, string pageToken = null, CancellationToken cancel = default(CancellationToken))
        {
            return _client.QueryEntitiesAsync(new QueryEntitiesRequest { ConnectionId = ConnectionId, Area = area ?? string.Empty, Component = component ?? string.Empty, Limit = limit, PageToken = pageToken ?? string.Empty }, cancellationToken: cancel).ResponseAsync;
        }

        private static void Check(Ack ack)
        {
            if (!ack.Ok) { throw new ModuleCallException(string.IsNullOrEmpty(ack.Error) ? "core refused the call" : ack.Error); }
        }
    }
}
