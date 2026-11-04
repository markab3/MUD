using System;
using System.Collections.Generic;
using MUD.Core.Components;

namespace MUD.Core.Events
{
    // Code that can answer a Before event (owner decision D6). A handler object that implements IEventHandler (After
    // notifications) may also implement this; a registered handler that does not is a miss for a Before event and
    // follows the fail policy.
    //
    // Begin must not block. It may call delivery.Reply at once (an in-process handler usually does), or keep the delivery
    // and reply later from the game thread, for example when the inbound item carrying an out-of-process module's verdict
    // is processed. A reply after the chain deadline or after a cancel is dropped and counted.
    public interface IBeforeHandler
    {
        void Begin(BeforeDelivery delivery);
    }

    // One call of one handler in a Before chain: one delivery id per handler call (owner decision D57), the event, the
    // immutable snapshots of every party (core components only, D57), the party and entry that matched (the entry's own
    // Config travels with the delivery, D57) and the hook it matched under.
    public sealed class BeforeDelivery
    {
        private readonly EventDispatcher _dispatcher;

        public long DeliveryId { get; }

        public GameEvent Event { get; }

        public EventParty Party { get; }

        public BehaviorRef Entry { get; }

        public string Hook { get; }

        public IReadOnlyList<EntitySnapshot> Snapshots { get; }

        internal BeforeDelivery(EventDispatcher dispatcher, long deliveryId, GameEvent gameEvent, EventParty party, BehaviorRef entry, string hook, IReadOnlyList<EntitySnapshot> snapshots)
        {
            _dispatcher = dispatcher;
            DeliveryId = deliveryId;
            Event = gameEvent;
            Party = party;
            Entry = entry;
            Hook = hook;
            Snapshots = snapshots;
        }

        // Game thread only. The same as dispatcher.SubmitVerdict(DeliveryId, verdict).
        public VerdictAcknowledgement Reply(Verdict verdict)
        {
            return _dispatcher.SubmitVerdict(DeliveryId, verdict);
        }
    }

    // One behavior entry that a Before chain will ask, with the party that carries it and the hook it matched under.
    internal sealed class BeforeCandidate
    {
        public EventParty Party;
        public BehaviorRef Entry;
        public string Hook;
        public IBeforeHandler Handler;
        public BeforeDelivery Delivery;
        public bool Answered;
    }

    // One Before chain: the handlers consulted for one Before event, and the verdicts collected so far. Game thread only.
    //
    // Two shapes:
    //  - all-handlers-wait (D15): every matching entry of every party is asked at once; the chain ends when all have
    //    answered or the single chain deadline passes. Any Deny vetoes (D6); the message shown is the first Deny to
    //    arrive (D7). Used for living.exit / living.enter and item.drop / item.take.
    //  - sequential (D63): used for command.entered only. Entries are asked one at a time in the order given; the first
    //    one that answers "handled" ends the chain and nothing after it is asked. Each hop has its own deadline.
    //
    // A handler that cannot answer in time, is unresolved, or throws is a miss and follows the fail policy (D60): fail
    // open by default (counts as Allow, or "not handled"), fail closed only for an entry the policy names.
    public sealed class BeforeChain
    {
        private readonly EventDispatcher _dispatcher;
        private readonly Action<BeforeOutcome> _onComplete;
        private readonly bool _sequential;
        private List<BeforeCandidate> _candidates;
        private IReadOnlyList<EntitySnapshot> _snapshots;
        private int _pending;
        private int _nextHop;
        private bool _advancing;
        private bool _denied;
        private string _message;
        private bool _handled;
        private int _timedOut;

        public GameEvent Event { get; }

        public bool IsComplete { get; private set; }

        // The time after which the chain (or, for a sequential chain, the hop being waited on) counts as missed.
        public DateTime Deadline { get; private set; }

        // What was running when the chain began (a handler of another chain, or nothing). The completion callback runs in
        // that context, not in the context of whichever handler happened to reply last, so the events the action
        // raises when it commits are caused by whatever caused the action.
        private readonly GameEvent _startedUnderEvent;
        private readonly EventOrigin _startedUnderOrigin;

        internal BeforeChain(EventDispatcher dispatcher, GameEvent gameEvent, bool sequential, Action<BeforeOutcome> onComplete, GameEvent startedUnderEvent, EventOrigin startedUnderOrigin)
        {
            _dispatcher = dispatcher;
            Event = gameEvent;
            _sequential = sequential;
            _onComplete = onComplete;
            _startedUnderEvent = startedUnderEvent;
            _startedUnderOrigin = startedUnderOrigin;
        }

        // Starts asking. Returns with the chain either complete (everything answered inline) or waiting.
        internal void Start(List<BeforeCandidate> candidates)
        {
            _candidates = candidates ?? new List<BeforeCandidate>();
            if (_candidates.Count > 0) { _snapshots = BuildSnapshots(Event.Parties); }

            if (_sequential)
            {
                Advance();
                return;
            }

            // All-handlers-wait. Every candidate counts as pending first, so an inline reply cannot end the chain early.
            Deadline = _dispatcher.Clock.Now + _dispatcher.Limits.BeforeChainDeadline;
            _pending = _candidates.Count;
            for (int index = 0; index < _candidates.Count && !IsComplete; index++)
            {
                BeforeCandidate candidate = _candidates[index];
                if (!Ask(candidate) && !candidate.Answered)
                {
                    candidate.Answered = true;
                    ApplyMiss(candidate, "unresolved or not a Before handler");
                    _pending--;
                }
            }
            if (!IsComplete && _pending == 0) { Complete(BeforeResult.Allowed); }
        }

        private static IReadOnlyList<EntitySnapshot> BuildSnapshots(IReadOnlyList<EventParty> parties)
        {
            var snapshots = new EntitySnapshot[parties.Count];
            for (int index = 0; index < snapshots.Length; index++) { snapshots[index] = EntitySnapshot.From(parties[index]); }
            return snapshots;
        }

        // Hands one candidate its delivery. False when it has no Before handler. A handler that throws is a miss.
        private bool Ask(BeforeCandidate candidate)
        {
            IEventHandler resolved;
            if (!_dispatcher.TryResolveHandler(candidate.Entry, out resolved)) { return false; }
            candidate.Handler = resolved as IBeforeHandler;
            if (candidate.Handler == null) { return false; }

            candidate.Delivery = _dispatcher.RegisterDelivery(this, candidate, _snapshots);
            try
            {
                _dispatcher.RunAsHandler(Event, candidate.Party, candidate.Entry, () => candidate.Handler.Begin(candidate.Delivery));
            }
            catch (Exception ex)
            {
                _dispatcher.HandlerFailed(candidate.Entry, Event, ex);
                if (!candidate.Answered)
                {
                    _dispatcher.ForgetDelivery(candidate.Delivery.DeliveryId);
                    candidate.Answered = true;
                    ApplyMiss(candidate, "threw " + ex.GetType().Name);
                    if (!_sequential) { _pending--; }
                }
            }
            return true;
        }

        // Called by the dispatcher when a verdict for one of this chain's deliveries arrives in time.
        internal VerdictAcknowledgement Record(long deliveryId, Verdict verdict)
        {
            BeforeCandidate candidate = null;
            for (int index = 0; index < _candidates.Count; index++)
            {
                if (_candidates[index].Delivery != null && _candidates[index].Delivery.DeliveryId == deliveryId) { candidate = _candidates[index]; break; }
            }
            if (candidate == null || candidate.Answered) { return new VerdictAcknowledgement(false, "already answered", null, 0); }
            candidate.Answered = true;
            _dispatcher.ForgetDelivery(deliveryId);

            // Events a handler emits apply whether or not the action is denied (D8); a refused one is skipped (D61).
            List<string> refused;
            int applied = _dispatcher.ApplyEmitted(Event, candidate.Party, candidate.Entry, verdict.Emitted, out refused);

            if (verdict.Decision == VerdictDecision.Deny) { RecordDeny(verdict.Message); }
            else if (_sequential && verdict.IsHandled)
            {
                _handled = true;
                _message = verdict.Message;
            }

            if (_sequential)
            {
                if (!_advancing && !IsComplete) { Advance(); }
            }
            else
            {
                _pending--;
                if (_pending == 0 && !IsComplete) { Complete(BeforeResult.Allowed); }
            }
            return new VerdictAcknowledgement(true, null, refused, applied);
        }

        // The first Deny to arrive gives the message (D7).
        private void RecordDeny(string message)
        {
            if (_denied) { return; }
            _denied = true;
            _message = message;
        }

        private void ApplyMiss(BeforeCandidate candidate, string why)
        {
            bool closed = _dispatcher.FailPolicy.IsClosed(candidate.Entry.Module, candidate.Entry.Type);
            _dispatcher.CountMiss(candidate.Entry, candidate.Hook, why, closed);
            if (closed) { RecordDeny(FailPolicy.ClosedMessage); }
        }

        // Sequential chains: ask entries one after another until one handles the line, one denies, or none are left.
        private void Advance()
        {
            _advancing = true;
            try
            {
                while (!IsComplete)
                {
                    if (_denied || _handled || _nextHop >= _candidates.Count)
                    {
                        Complete(BeforeResult.Allowed);
                        return;
                    }
                    BeforeCandidate hop = _candidates[_nextHop++];
                    Deadline = _dispatcher.Clock.Now + _dispatcher.Limits.BeforeChainDeadline;
                    if (!Ask(hop) && !hop.Answered)
                    {
                        hop.Answered = true;
                        ApplyMiss(hop, "unresolved or not a Before handler");
                    }
                    if (!hop.Answered) { return; } // waiting for this hop's verdict
                }
            }
            finally
            {
                _advancing = false;
            }
        }

        // Called by the dispatcher on the game thread when the clock has reached Deadline.
        internal void Expire()
        {
            if (IsComplete) { return; }
            if (_sequential)
            {
                // Only the hop being waited on can be late: it is a miss, and the chain moves on to the next entry.
                BeforeCandidate late = _nextHop > 0 ? _candidates[_nextHop - 1] : null;
                if (late != null && !late.Answered)
                {
                    late.Answered = true;
                    _timedOut++;
                    if (late.Delivery != null) { _dispatcher.ForgetDelivery(late.Delivery.DeliveryId); }
                    ApplyMiss(late, "missed the deadline");
                    if (!_advancing) { Advance(); }
                }
                return;
            }

            for (int index = 0; index < _candidates.Count; index++)
            {
                BeforeCandidate candidate = _candidates[index];
                if (candidate.Answered) { continue; }
                candidate.Answered = true;
                _timedOut++;
                if (candidate.Delivery != null) { _dispatcher.ForgetDelivery(candidate.Delivery.DeliveryId); }
                ApplyMiss(candidate, "missed the deadline");
                _pending--;
            }
            Complete(BeforeResult.Allowed);
        }

        // Ends the chain without a decision (stop, quit). Verdicts that arrive later are dropped and counted.
        public void Cancel()
        {
            Complete(BeforeResult.Cancelled);
        }

        internal void Refuse()
        {
            Complete(BeforeResult.Refused);
        }

        private void Complete(BeforeResult result)
        {
            if (IsComplete) { return; }
            IsComplete = true;
            if (_candidates != null)
            {
                for (int index = 0; index < _candidates.Count; index++)
                {
                    if (_candidates[index].Delivery != null) { _dispatcher.ForgetDelivery(_candidates[index].Delivery.DeliveryId); }
                }
            }
            _dispatcher.ChainFinished(this);

            BeforeResult final = result;
            if (result == BeforeResult.Allowed && _denied) { final = BeforeResult.Denied; }
            var outcome = new BeforeOutcome(final, _message, _handled, _timedOut, Event);
            try
            {
                if (_onComplete != null) { _dispatcher.RunInContext(_startedUnderEvent, _startedUnderOrigin, () => _onComplete(outcome)); }
            }
            catch (Exception ex)
            {
                _dispatcher.HandlerFailed(null, Event, ex);
            }
        }
    }
}
