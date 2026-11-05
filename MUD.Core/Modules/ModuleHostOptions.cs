using System;

namespace MUD.Core.Modules
{
    /// <summary>
    /// The numbers of the module host. The owner has not set the queue size, the per-pass share or the call timeout
    /// (CORE_API 1 says "not set"; D70 covers only the start-up timeout and the systemd limit). The defaults below were
    /// chosen by card P4-2 and are flagged in its pull request for the owner to confirm or change. They are read from the
    /// ModuleHost section of appsettings.json.
    /// </summary>
    public class ModuleHostOptions
    {
        /// <summary>Most module calls waiting for the game thread, across all modules. A call arriving when the queue is full is refused, never blocked.</summary>
        public int InboundQueueCapacity { get; set; } = GameLoop.DefaultModuleCallCapacity;

        /// <summary>
        /// Most queued module calls run in one pass of the game loop. A pass already handles up to GameLoop.MaxItemsPerPass (100)
        /// player and persistence items; module calls take this smaller share on top, so a chatty module cannot starve player input.
        /// </summary>
        public int InboundPerPassShare { get; set; } = GameLoop.DefaultModuleCallsPerPass;

        /// <summary>Most deliveries waiting on one module's server stream. A delivery that does not fit is refused and the caller applies the fail policy.</summary>
        public int OutboundQueueCapacity { get; set; } = 1000;

        /// <summary>How long a module call waits for the game thread to run it before the call fails with a deadline error.</summary>
        public TimeSpan GameThreadCallTimeout { get; set; } = TimeSpan.FromSeconds(5);

        /// <summary>How long stopping the host waits for open calls and streams to finish.</summary>
        public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(3);

        // ---- Card P4-4. The owner has not set these numbers (CORE_API 2.8: "not set"; D73 leaves the state size limit to the card).
        // The defaults below were chosen by card P4-4 and are flagged in its pull request for the owner to confirm or change. ----

        /// <summary>Largest module-state document, in BSON bytes (D12, D73). BehaviorRef.MaxConfigBytes is 4096; a state document may hold a module's whole schedule, so it is larger.</summary>
        public int StateMaxDocumentBytes { get; set; } = 65536;

        /// <summary>Sustained SetState writes per second, per module (token bucket refill).</summary>
        public double StateWritesPerSecond { get; set; } = 10;

        /// <summary>Most SetState writes a module may make at once before the rate limit applies (token bucket size).</summary>
        public double StateWriteBurst { get; set; } = 20;

        /// <summary>Sustained emitted events per second, per module, through the Emit call (the per-module emission quota of D9).</summary>
        public double EmitPerSecond { get; set; } = 50;

        /// <summary>Most events a module may emit at once through the Emit call before the quota applies.</summary>
        public double EmitBurst { get; set; } = 100;

        /// <summary>Most timers one module connection may hold at once (CORE_API 3: "minimum interval and count not set").</summary>
        public int TimersPerModule { get; set; } = 100;

        /// <summary>Earliest a timer may fire after it is scheduled: a timer set for the past or sooner than this fires after this delay. Timers are checked once per game tick (100 ms).</summary>
        public TimeSpan TimerMinimumDelay { get; set; } = TimeSpan.FromMilliseconds(100);
    }
}
