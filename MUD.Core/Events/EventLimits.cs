namespace MUD.Core.Events
{
    // The numbers that keep emitted events from running away (owner decision D9). The owner has not set them
    // (CORE_API 2.8); the defaults below were chosen by card P3-1 and are flagged in its pull request for the owner
    // to confirm or change. They are plain settings so a later card can read them from configuration.
    public class EventLimits
    {
        // Longest chain of emitted events: a root event is depth 0, an event it emitted is depth 1, and so on.
        // An event deeper than this is refused, counted and logged.
        public int MaxCascadeDepth { get; set; } = 8;

        // Events processed per pass of the game loop, whatever else is queued. A pass handles up to
        // GameLoop.MaxItemsPerPass inbound items; this is the share reserved for events on top of that, so a burst of
        // events cannot starve input and a flood of input cannot starve events. Events over the budget stay queued and
        // are processed on the next pass.
        public int MaxEventsPerPass { get; set; } = 50;
    }
}
