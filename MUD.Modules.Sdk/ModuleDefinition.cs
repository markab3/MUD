using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using MUD.Contracts;

namespace MUD.Modules.Sdk
{
    /// <summary>What a module declares about itself: name, version, capabilities, the behavior types it provides, and its handlers.</summary>
    public sealed class ModuleDefinition
    {
        private readonly List<string> _capabilities = new List<string>();
        private readonly List<string> _requiredCapabilities = new List<string>();
        private readonly Dictionary<string, BehaviorRegistration> _behaviors = new Dictionary<string, BehaviorRegistration>(StringComparer.Ordinal);

        private ModuleDefinition(string name, string version)
        {
            if (string.IsNullOrWhiteSpace(name)) { throw new ArgumentException("A module needs a name.", nameof(name)); }
            Name = name;
            Version = version ?? string.Empty;
        }

        /// <summary>The module name. It must equal the name of the module's folder in the modules directory (D67).</summary>
        public string Name { get; }

        public string Version { get; }

        /// <summary>Whether the front end waits for this module to be Ready (reported by --describe, D67 working default).</summary>
        public bool Required { get; private set; }

        /// <summary>Capabilities requested in the manifest, in the vocabulary of D65: "emit:&lt;event name&gt;", "timer.schedule", "state", "read".</summary>
        public IReadOnlyList<string> Capabilities { get { return _capabilities; } }

        /// <summary>The subset of capabilities whose absence makes core refuse the start (D18).</summary>
        public IReadOnlyList<string> RequiredCapabilities { get { return _requiredCapabilities; } }

        public IReadOnlyDictionary<string, BehaviorRegistration> Behaviors { get { return _behaviors; } }

        /// <summary>Runs once after the connection is accepted and before Ready: read the state document, reconcile, schedule timers (idempotent, D13).</summary>
        public Func<ModuleContext, Task> OnStart { get; private set; }

        /// <summary>Runs for every timer.fired delivery.</summary>
        public Func<TimerFired, ModuleContext, Task> OnTimer { get; private set; }

        public static ModuleDefinition Create(string name, string version)
        {
            return new ModuleDefinition(name, version);
        }

        public ModuleDefinition MarkRequired()
        {
            Required = true;
            return this;
        }

        /// <summary>Requests capabilities. They are only requests: the grants file decides (D18).</summary>
        public ModuleDefinition Request(params string[] capabilities)
        {
            foreach (string capability in capabilities) { AddCapability(_capabilities, capability); }
            return this;
        }

        /// <summary>Requests capabilities that core must grant or refuse the start (D18).</summary>
        public ModuleDefinition RequireCapabilities(params string[] capabilities)
        {
            foreach (string capability in capabilities)
            {
                AddCapability(_capabilities, capability);
                AddCapability(_requiredCapabilities, capability);
            }
            return this;
        }

        /// <summary>Provides one behavior type, run for the given hooks. The handler runs one delivery at a time (D62).</summary>
        public ModuleDefinition Behavior(string type, string[] hooks, Func<BehaviorCall, ModuleContext, Task<BehaviorResult>> handler)
        {
            if (string.IsNullOrWhiteSpace(type)) { throw new ArgumentException("A behavior needs a type name.", nameof(type)); }
            if (handler == null) { throw new ArgumentNullException(nameof(handler)); }
            if (_behaviors.ContainsKey(type)) { throw new InvalidOperationException("The behavior type '" + type + "' is already registered."); }
            _behaviors[type] = new BehaviorRegistration(type, hooks ?? new string[0], handler);
            return this;
        }

        public ModuleDefinition Start(Func<ModuleContext, Task> onStart)
        {
            OnStart = onStart;
            return this;
        }

        public ModuleDefinition Timer(Func<TimerFired, ModuleContext, Task> onTimer)
        {
            OnTimer = onTimer;
            return this;
        }

        /// <summary>The manifest sent to core in Hello (D18). It is built from this definition, never compiled into core.</summary>
        public Manifest BuildManifest()
        {
            var manifest = new Manifest { Name = Name };
            foreach (BehaviorRegistration behavior in _behaviors.Values)
            {
                var declaration = new BehaviorDecl { Type = behavior.Type };
                declaration.Hooks.AddRange(behavior.Hooks);
                manifest.Behaviors.Add(declaration);
            }
            manifest.Capabilities.AddRange(_capabilities);
            manifest.RequiredCapabilities.AddRange(_requiredCapabilities);
            return manifest;
        }

        private static void AddCapability(List<string> list, string capability)
        {
            if (string.IsNullOrWhiteSpace(capability)) { throw new ArgumentException("A capability cannot be empty."); }
            if (!list.Contains(capability)) { list.Add(capability); }
        }
    }

    public sealed class BehaviorRegistration
    {
        internal BehaviorRegistration(string type, IReadOnlyList<string> hooks, Func<BehaviorCall, ModuleContext, Task<BehaviorResult>> handler)
        {
            Type = type;
            Hooks = hooks;
            Handler = handler;
        }

        public string Type { get; }
        public IReadOnlyList<string> Hooks { get; }
        public Func<BehaviorCall, ModuleContext, Task<BehaviorResult>> Handler { get; }
    }

    /// <summary>One delivery to a behavior: a read-only view of a CoreEvent.</summary>
    public sealed class BehaviorCall
    {
        public BehaviorCall(CoreEvent delivery)
        {
            Delivery = delivery ?? throw new ArgumentNullException(nameof(delivery));
        }

        public CoreEvent Delivery { get; }
        public string EventId { get { return Delivery.EventId; } }
        public string EventName { get { return Delivery.Name; } }
        public Phase Phase { get { return Delivery.Phase; } }
        public bool IsBefore { get { return Delivery.Phase == Phase.Before; } }
        public bool IsAfter { get { return Delivery.Phase == Phase.After; } }
        public string Hook { get { return Delivery.Hook; } }
        public string BehaviorType { get { return Delivery.Behavior == null ? string.Empty : Delivery.Behavior.Type; } }

        /// <summary>The behavior entry's own Config (D57), or an empty map.</summary>
        public Struct Config { get { return Delivery.Behavior == null || Delivery.Behavior.Config == null ? new Struct() : Delivery.Behavior.Config; } }

        public Struct Payload { get { return Delivery.Payload ?? new Struct(); } }

        public IReadOnlyList<PartySnapshot> Parties { get { return Delivery.Parties; } }

        /// <summary>The first party with the role (for example mover, from, to, actor, room, item), or null.</summary>
        public PartySnapshot Party(string role)
        {
            return Delivery.Parties.FirstOrDefault(party => party.Role == role);
        }
    }

    /// <summary>A timer.fired delivery for a timer this module scheduled (D59, D74).</summary>
    public sealed class TimerFired
    {
        public TimerFired(string timerId, Struct payload)
        {
            TimerId = timerId;
            Payload = payload ?? new Struct();
        }

        public string TimerId { get; }
        public Struct Payload { get; }
    }

    /// <summary>
    /// What a Before handler answers: Allow or Deny only (D6), optionally with events that apply whether or not the action is denied (D8),
    /// and the handled flag, meaningful only for command.entered (D63).
    /// </summary>
    public sealed class BehaviorResult
    {
        private readonly List<EventRecord> _emitted = new List<EventRecord>();

        private BehaviorResult(Decision decision, string message, bool handled)
        {
            Decision = decision;
            Message = message ?? string.Empty;
            Handled = handled;
        }

        public Decision Decision { get; }
        public string Message { get; }
        public bool Handled { get; }
        public IReadOnlyList<EventRecord> Emitted { get { return _emitted; } }

        public static BehaviorResult Allow() { return new BehaviorResult(Decision.Allow, null, false); }

        /// <summary>Veto. The message is shown to the player.</summary>
        public static BehaviorResult Deny(string message) { return new BehaviorResult(Decision.Deny, message, false); }

        /// <summary>For command.entered: this behavior took the line, stop looking (D63).</summary>
        public static BehaviorResult AllowAndHandled() { return new BehaviorResult(Decision.Allow, null, true); }

        /// <summary>Adds events built with ModuleEvents. Core applies the valid ones and reports the refused ones (D61).</summary>
        public BehaviorResult WithEmitted(params EventRecord[] events)
        {
            _emitted.AddRange(events);
            return this;
        }
    }
}
