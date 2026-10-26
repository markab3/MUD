using System.Linq;
using MUD.Core.GameObjects;
using MUD.Core.Properties;
using MUD.Core.Properties.Interfaces;

namespace MUD.Core.Commands
{
    public class SearchCommand : ICommand
    {
        /// <summary>How long a search keeps the searcher busy before the result is found.</summary>
        public static readonly System.TimeSpan SearchDuration = System.TimeSpan.FromSeconds(3);

        private readonly CommandQueue _commandQueue;

        public SearchCommand(CommandQueue commandQueue)
        {
            _commandQueue = commandQueue;
        }

        public string[] CommandKeywords { get => new string[] { "search" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Default; }

        public string HelpText { get => "Search for things in the specified spot. If no additional arguments are provided, search the room in general.\r\n \r\nSyntax: search <optional item>"; }


        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) };
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            // Room currentRoom = commandIssuer.CurrentLocation;
            // SearchProperty searchProp = (SearchProperty)currentRoom.ExtendedProperties.FirstOrDefault(p => p.GetType() == typeof(SearchProperty));
            // if (searchProp != null)
            // {
            //     searchProp.DoSearch(commandIssuer, target);
            // }
            // else
            // {
            //     commandIssuer.ReceiveMessage(string.Format("You search the {0} but find nothing of any value.", (string)commandArgs[0]));
            // }
            commandIssuer.ReceiveMessage("You begin your search.");
            // The game thread must never sleep: the searcher is busy for SearchDuration (NextActionAt) and the
            // search itself runs as their next command ("stop" cancels it).
            string target = (string)commandArgs[0];
            _commandQueue.DelayThen(commandIssuer, SearchDuration, () => FinishSearch(commandIssuer, target));
        }

        private void FinishSearch(Living commandIssuer, string target)
        {

            bool success = false;

            var searchProp = commandIssuer.CurrentLocation.ExtendedProperties.FirstOrDefault(ep => ep is ISearchable);
            if (searchProp != null)
            {
                success = ((ISearchable)searchProp).DoSearch(commandIssuer, target);
            }

            if (!success)
            {
                commandIssuer.ReceiveMessage("You finish your search.");
            }

            commandIssuer.CurrentLocation.TellRoom(string.Format("{0} searches around a bit.", commandIssuer.ShortDescription), new Living[] { commandIssuer });
        }
    }
}