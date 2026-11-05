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
    }
}
