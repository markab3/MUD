using System.Linq;
using MUD.Core.Components;
using MUD.Core.GameObjects;

namespace MUD.Core.Commands
{
    public class ApplyStatusCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "apply" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Creator; }

        public string HelpText { get => "Apply a given status to a living object. Syntax: apply <status name> to <living>"; }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) }; ;
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            const string usage = "Syntax: apply <status name> to <living>. Statuses: bleeding.";

            if (!commandIssuer.Roles.Creator)
            {
                commandIssuer.ReceiveMessage("This is a creator only command. How did you get this?!");
                return;
            }

            string args = ((commandArgs != null && commandArgs.Length > 0 ? commandArgs[0] as string : null) ?? "").Trim();
            int separator = args.IndexOf(" to ", System.StringComparison.OrdinalIgnoreCase);
            if (separator <= 0) { commandIssuer.ReceiveMessage(usage); return; }

            string status = args.Substring(0, separator).Trim().ToLowerInvariant();
            string targetText = args.Substring(separator + 4).Trim();
            if (status != "bleeding" || targetText.Length == 0) { commandIssuer.ReceiveMessage(usage); return; }

            Living target = targetText.ToLowerInvariant() == "me"
                ? commandIssuer
                : commandIssuer.MatchObjects(targetText, false, false, true, false).OfType<Living>().FirstOrDefault();
            if (target == null)
            {
                commandIssuer.ReceiveMessage("You cannot find '" + targetText + "'.");
                return;
            }

            if (!BleedingStatus.Apply(target))
            {
                commandIssuer.ReceiveMessage("That is already bleeding.");
                return;
            }
            if (target is Player player) { player.Save(true); }
            commandIssuer.ReceiveMessage("Applied bleeding.");
        }
    }
}