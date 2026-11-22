using System;
using System.Collections.Generic;
using MUD.Core.Modules;

namespace MUD.Core.Administration
{
    /// <summary>
    /// What the Admin service needs from the module supervisor. The supervisor is the only owner of module state and logic; this is a
    /// seam so tests can supply a fake and so the Admin service can be built before the supervisor exists.
    /// </summary>
    public interface IModuleControl
    {
        /// <summary>The state of every module core knows, or null when the supervisor is not running.</summary>
        IReadOnlyList<ModuleStatus> GetStatuses();

        /// <summary>Queues a start on the supervisor thread (it clears Failed, Refused and the crash history). Null when the supervisor is not running.</summary>
        string RequestStart(string name);

        /// <summary>Queues a stop on the supervisor thread. Null when the supervisor is not running.</summary>
        string RequestStop(string name);
    }

    /// <summary>The real thing: the World's supervisor, looked up on each call because it is attached after the Admin service is built.</summary>
    public sealed class SupervisorModuleControl : IModuleControl
    {
        private readonly Func<ModuleSupervisor> _supervisor;

        public SupervisorModuleControl(Func<ModuleSupervisor> supervisor)
        {
            _supervisor = supervisor ?? throw new ArgumentNullException(nameof(supervisor));
        }

        public IReadOnlyList<ModuleStatus> GetStatuses() { return _supervisor()?.GetStatuses(); }

        public string RequestStart(string name) { return _supervisor()?.RequestStart(name); }

        public string RequestStop(string name) { return _supervisor()?.RequestStop(name); }
    }
}
