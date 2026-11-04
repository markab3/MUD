using System.Collections.Generic;

namespace MUD.Core.Events
{
    public enum VerdictDecision
    {
        Allow,
        Deny
    }

    // An event a handler asks core to raise as part of its reply (owner decisions D6, D8). It is queued whether or not
    // the action is denied; there is no transactional discard. A name that core can execute as an action (for example
    // entity.move, see EventDispatcher.RegisterAction) is a request: it runs its own Before chain (D6, a redirect is a
    // Deny plus a new move). Any other name is an After-phase event delivered to the hooks of its parties.
    public sealed class EmittedEvent
    {
        public string Name { get; }

        public IReadOnlyList<EventParty> Parties { get; }

        public IReadOnlyDictionary<string, object> Payload { get; }

        public EmittedEvent(string name, IReadOnlyList<EventParty> parties, IReadOnlyDictionary<string, object> payload = null)
        {
            Name = name;
            Parties = parties;
            Payload = payload;
        }
    }

    // What a Before handler answers (owner decision D6): Allow or Deny(message), each with optional emitted events.
    // There is no Modify; a redirect is a Deny plus an emitted entity.move request.
    //
    // IsHandled is meaningful for the command.entered hook only (owner decision D63): the behavior took the typed line,
    // so core stops asking further behaviors and does not run a built-in command. It is ignored for every other hook.
    public sealed class Verdict
    {
        private static readonly Verdict AllowWithoutEvents = new Verdict(VerdictDecision.Allow, null, false, null);

        public VerdictDecision Decision { get; }

        // Shown to the player on Deny, and on a handled command line when it is not null.
        public string Message { get; }

        public bool IsHandled { get; }

        public IReadOnlyList<EmittedEvent> Emitted { get; }

        private Verdict(VerdictDecision decision, string message, bool handled, IReadOnlyList<EmittedEvent> emitted)
        {
            Decision = decision;
            Message = message;
            IsHandled = handled;
            Emitted = emitted;
        }

        public static Verdict Allow(IReadOnlyList<EmittedEvent> emitted = null)
        {
            return emitted == null ? AllowWithoutEvents : new Verdict(VerdictDecision.Allow, null, false, emitted);
        }

        public static Verdict Deny(string message, IReadOnlyList<EmittedEvent> emitted = null)
        {
            return new Verdict(VerdictDecision.Deny, message, false, emitted);
        }

        // command.entered only: this behavior took the line. The decision is Allow (a handled line is not a veto).
        public static Verdict Handled(string message = null, IReadOnlyList<EmittedEvent> emitted = null)
        {
            return new Verdict(VerdictDecision.Allow, message, true, emitted);
        }
    }

    // The core's reply to a verdict (owner decision D61): the verdict itself always counts when it arrived in time; each
    // emitted event that core refused is listed and logged, and the rest were applied.
    public sealed class VerdictAcknowledgement
    {
        public bool Counted { get; }

        // Why the verdict was not counted (late, cancelled or already answered); null when it counted.
        public string NotCountedReason { get; }

        public IReadOnlyList<string> RefusedEvents { get; }

        public int AppliedEventCount { get; }

        public VerdictAcknowledgement(bool counted, string notCountedReason, IReadOnlyList<string> refusedEvents, int appliedEventCount)
        {
            Counted = counted;
            NotCountedReason = notCountedReason;
            RefusedEvents = refusedEvents ?? new string[0];
            AppliedEventCount = appliedEventCount;
        }
    }

    public enum BeforeResult
    {
        // No handler denied (every miss failed open). The action may go on to re-validation and commit.
        Allowed,
        Denied,
        // The wait was cancelled (stop, quit); the action must not run.
        Cancelled,
        // Core refused to start the chain (cascade depth or re-entrancy guard); the action must not run.
        Refused
    }

    // The result of one finished Before chain.
    public sealed class BeforeOutcome
    {
        // The outcome of a chain that had no handlers at all (the fast path).
        public static readonly BeforeOutcome AllowedWithoutHandlers = new BeforeOutcome(BeforeResult.Allowed, null, false, 0, null);

        public BeforeResult Result { get; }

        // The first Deny message to arrive (D7), or the core message for a fail-closed miss; null when none was given.
        public string Message { get; }

        // command.entered only: a behavior handled the line (D63), and the message it gave, if any.
        public bool Handled { get; }

        // Handlers that did not answer before the deadline.
        public int TimedOut { get; }

        // The Before event the chain ran for; null on the fast path.
        public GameEvent Event { get; }

        public BeforeOutcome(BeforeResult result, string message, bool handled, int timedOut, GameEvent gameEvent)
        {
            Result = result;
            Message = message;
            Handled = handled;
            TimedOut = timedOut;
            Event = gameEvent;
        }
    }
}
