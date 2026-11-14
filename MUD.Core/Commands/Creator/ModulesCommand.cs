using System;
using System.Linq;
using MUD.Core.GameObjects;
using MUD.Core.Modules;
using MUD.CoreApi;

namespace MUD.Core.Commands
{
    /// <summary>
    /// The `modules` admin command (owner decisions D67, D69): list, rescan, start, stop, restart, and `grants reload` for the
    /// grants file. There is no `modules token` and no `modules reload` (D69, D67). It is registered in the Creator list because
    /// Living.ResolveCommand never consults the Admin list (pinned by RoleResolutionTests); the Admin role is checked here, like
    /// the other admin-only commands do. Every sub-command only queues work for the supervisor thread and returns at once.
    /// </summary>
    public class ModulesCommand : ICommand
    {
        public const string Syntax = "Syntax: modules list | modules rescan | modules start <name> | modules stop <name> | modules restart <name> | modules grants reload";

        private readonly World _world;

        public ModulesCommand(World world)
        {
            _world = world;
        }

        public string[] CommandKeywords { get => new string[] { "modules" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Creator; }

        public string HelpText { get => "Admin only. Show and control the out-of-process modules. `list` shows every module and its state; `rescan` looks in the modules directory for new or changed modules (it never restarts a running one); `start`, `stop` and `restart` act on one module; `grants reload` re-reads the grants file.\r\n\r\n" + Syntax; }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) ?? "" };
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            if (!commandIssuer.Roles.Admin)
            {
                commandIssuer.ReceiveMessage("This is an admin only command.", OutputChannel.System);
                return;
            }
            ModuleSupervisor supervisor = _world.ModuleSupervisor;
            if (supervisor == null)
            {
                commandIssuer.ReceiveMessage("The module supervisor is not running.", OutputChannel.System);
                return;
            }

            string[] words = ((commandArgs != null && commandArgs.Length > 0 ? commandArgs[0] as string : null) ?? "")
                .Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            string verb = words.Length > 0 ? words[0].ToLowerInvariant() : "";
            string name = words.Length > 1 ? words[1] : null;

            switch (verb)
            {
                case "list" when words.Length == 1:
                    foreach (string line in Describe(supervisor)) { commandIssuer.ReceiveMessage(line, OutputChannel.System); }
                    return;
                case "rescan" when words.Length == 1:
                    commandIssuer.ReceiveMessage(supervisor.RequestRescan(), OutputChannel.System);
                    return;
                case "start" when words.Length == 2:
                    commandIssuer.ReceiveMessage(supervisor.RequestStart(name), OutputChannel.System);
                    return;
                case "stop" when words.Length == 2:
                    commandIssuer.ReceiveMessage(supervisor.RequestStop(name), OutputChannel.System);
                    return;
                case "restart" when words.Length == 2:
                    commandIssuer.ReceiveMessage(supervisor.RequestRestart(name), OutputChannel.System);
                    return;
                case "grants" when words.Length == 2 && string.Equals(words[1], "reload", StringComparison.OrdinalIgnoreCase):
                    commandIssuer.ReceiveMessage(supervisor.Registry.Grants.Reload()
                        ? "Grants reloaded from " + supervisor.Registry.Grants.FilePath + ". They apply to the next module start."
                        : "The grants file could not be read; the earlier grants stay in force. See the core log.", OutputChannel.System);
                    return;
                default:
                    commandIssuer.ReceiveMessage(Syntax, OutputChannel.System);
                    return;
            }
        }

        /// <summary>The lines of `modules list`.</summary>
        public static string[] Describe(ModuleSupervisor supervisor)
        {
            var statuses = supervisor.GetStatuses();
            if (statuses.Count == 0) { return new[] { "No modules found in " + (supervisor.Options.ModulesDirectory ?? "(no modules directory set)") + "." }; }
            var lines = statuses.Select(s =>
            {
                string text = s.Name + ": " + s.State
                    + (s.Required ? " (required)" : "")
                    + (string.IsNullOrEmpty(s.Version) ? "" : " version " + s.Version)
                    + (s.ProcessId > 0 ? " process " + s.ProcessId : "")
                    + (s.CrashesInWindow > 0 ? ", " + s.CrashesInWindow + " recent crash(es)" : "")
                    + (s.NextStartUtc.HasValue ? ", restart at " + s.NextStartUtc.Value.ToString("HH:mm:ss") + " UTC" : "")
                    + (s.FolderMissing ? ", folder gone" : "");
                if (!string.IsNullOrEmpty(s.Problem)) { text += ". Problem: " + s.Problem; }
                else if (!string.IsNullOrEmpty(s.Detail)) { text += ". Last: " + s.Detail; }
                return text;
            });
            return lines.ToArray();
        }
    }
}
