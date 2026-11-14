using System;
using System.Threading.Tasks;
using Google.Protobuf.WellKnownTypes;
using MUD.Contracts;
using MUD.Modules.Sdk;

namespace MUD.Modules.Hello
{
    /// <summary>
    /// The trivial module (card P4-5): it shows every part of the SDK and has no game meaning beyond that.
    ///
    ///   Behavior type  HelloGreeter, hook living.enter, attached to a room (or a player) by a builder.
    ///   Before phase   always Allow (a greeter never stops a move).
    ///   After phase    greets the mover by name with a message and counts the move.
    ///   State          the count is kept in the module state document "moves" ({ "count": n }) and read again in OnStart,
    ///                  so it survives a restart of the module (D12, D13).
    ///   Timer          a repeating "hello-tick" timer re-reports the count to the module log every minute (D74: recreated on every start).
    ///
    /// The card suggested a greeting on login. Core fires no event when a player logs in (World.AddPlayer enters the room directly,
    /// without an entity.move), and adding one is not part of this card, so the greeting goes with the move into a room that has the entry.
    ///
    /// Capabilities requested (the owner grants them in module-grants.yaml): emit:message.send, state, timer.schedule.
    /// The module still works with a partial grant: a refused message or state write is logged and skipped.
    /// </summary>
    public sealed class HelloModule
    {
        public const string ModuleName = "hello";
        public const string BehaviorType = "HelloGreeter";
        public const string StateKey = "moves";
        public const string TimerId = "hello-tick";

        private readonly TimeSpan _reportInterval;
        private long _moves;

        public HelloModule(TimeSpan? reportInterval = null)
        {
            _reportInterval = reportInterval ?? TimeSpan.FromMinutes(1);
        }

        /// <summary>The moves counted since the state document began (restored in OnStart).</summary>
        public long Moves { get { return System.Threading.Interlocked.Read(ref _moves); } }

        public static ModuleDefinition Define(HelloModule instance = null)
        {
            HelloModule module = instance ?? new HelloModule();
            return ModuleDefinition.Create(ModuleName, "1.0.0")
                .Request("emit:message.send", "state", "timer.schedule")
                .Behavior(BehaviorType, new[] { "living.enter" }, module.OnEnterAsync)
                .Start(module.OnStartAsync)
                .Timer(module.OnTimerAsync);
        }

        private async Task OnStartAsync(ModuleContext context)
        {
            if (context.IsGranted("state"))
            {
                Struct document = await context.GetStateAsync(StateKey);
                _moves = (long)Payloads.GetNumber(document, "count", 0);
                context.Log("Restored " + _moves + " counted moves.");
            }
            if (context.IsGranted("timer.schedule"))
            {
                await context.ScheduleTimerAsync(TimerId, _reportInterval);
            }
        }

        private async Task<BehaviorResult> OnEnterAsync(BehaviorCall call, ModuleContext context)
        {
            if (call.IsBefore) { return BehaviorResult.Allow(); }

            PartySnapshot mover = call.Party("mover");
            if (mover == null) { return BehaviorResult.Allow(); }

            long count = System.Threading.Interlocked.Increment(ref _moves);

            string greeting = Payloads.GetText(call.Config, "greeting") ?? "Hello";
            string name = Payloads.GetText(mover.Components, "shortDescription") ?? "traveller";
            if (context.IsGranted("emit:message.send"))
            {
                await context.SendMessageAsync(mover.EntityId, greeting + ", " + name + ". That is move " + count + " I have seen.", ModuleEvents.System);
            }
            if (context.IsGranted("state"))
            {
                try { await context.SetStateAsync(StateKey, Payloads.Map(("count", count))); }
                catch (ModuleCallException ex) { context.Log("The move count was not saved: " + ex.Message); }
            }
            return BehaviorResult.Allow();
        }

        private async Task OnTimerAsync(TimerFired fired, ModuleContext context)
        {
            context.Log("Hello has counted " + Moves + " moves.");
            if (context.IsGranted("timer.schedule")) { await context.ScheduleTimerAsync(TimerId, _reportInterval); }
        }
    }
}
