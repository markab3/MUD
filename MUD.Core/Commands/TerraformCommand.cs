using System;
using System.Linq;
using MUD.Core.Formatting;

namespace MUD.Core.Commands
{
    public class TerraformCommand : ICommand
    {
        public string[] CommandKeywords { get => new string[] { "terraform" }; }

        public bool IsDefault { get => true; }

        public string HelpText { get => String.Format("Change the room you are in or create a new one. Valid options are: {0}", Enum.GetNames(typeof(TerraformOption)).GetListText()); }

        private enum TerraformOption
        {
            CREATE,
            LINK,
            UNLINK,
            SETSHORT,
            SETLONG,
        }

        private TerraformOption? _terraformOption = null;

        private World _world;

        public TerraformCommand(World world)
        {
            _world = world;
        }

        public object[] ParseCommand(Player commandIssuer, string input)
        {
            input = this.StripKeyword(input);

            string[] inputArgs = input.Split(" ");

            if (inputArgs != null && inputArgs.Length > 0)
            {
                TerraformOption matchedOption;
                if (Enum.TryParse(((string)inputArgs[0]).ToUpper(), out matchedOption))
                {
                    _terraformOption = matchedOption;
                    inputArgs = inputArgs.Skip(1).ToArray();
                }
            }
            return inputArgs;
        }

        public void DoCommand(Player commandIssuer, object[] commandArgs)
        {
            if (_terraformOption == null)
            {
                // No bueno.
                string options = Enum.GetNames(typeof(TerraformOption)).GetListText();
                commandIssuer.ReceiveMessage(String.Format("Valid terraform option not provided. Available options are: {0}", options));
                return;
            }

            Room currentLocation = commandIssuer.CurrentLocation;
            if (currentLocation == null)
            {
                commandIssuer.ReceiveMessage("You are not in a room currently, so what are you editing?");
                return;
            }

            switch (_terraformOption)
            {
                case TerraformOption.CREATE:
                    break;
                case TerraformOption.LINK:
                    // Link this with another room.
                    if (commandArgs == null || commandArgs.Length < 2)
                    {
                        commandIssuer.ReceiveMessage("You must provide a room id and an exit name to make a new link. You can optionally specify a return exit as well.\r\nFormat: terraform link <room id> <exit> <return exit>");
                        return;
                    }

                    string newDestination = (string)commandArgs[0];
                    string newName = (string)commandArgs[1];
                    string returnName = null;
                    if (commandArgs.Length > 2) { returnName = (string)commandArgs[2]; }

                    var roomToLink = _world.GetRoom((string)commandArgs[0]);
                    if (roomToLink == null)
                    {
                        commandIssuer.ReceiveMessage(string.Format("Room with id of {0} was not found.", (string)commandArgs[0]));
                        return;
                    }

                    if (currentLocation.Exits != null && currentLocation.Exits.FirstOrDefault(x => x.DestinationId == newDestination) != null)
                    {
                        commandIssuer.ReceiveMessage(string.Format("The room with id of {0} is already linked here.", newDestination));
                    }
                    else if (currentLocation.Exits != null && currentLocation.Exits.FirstOrDefault(x => x.Name == newName) != null)
                    {
                        commandIssuer.ReceiveMessage(string.Format("This room already has {0} {1} exit.", newName.GetArticle(), newName));
                    }
                    else
                    {
                        currentLocation.Exits.Add(new Exit() { DestinationId = newDestination, Name = newName });
                        currentLocation.RebuildExitCommands();
                        if (!commandIssuer.CurrentLocation.Save())
                        {
                            commandIssuer.ReceiveMessage("This room could not be saved.");
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(returnName))
                    {
                        if (roomToLink.Exits != null && roomToLink.Exits.FirstOrDefault(x => x.DestinationId == currentLocation.Id) != null)
                        {
                            commandIssuer.ReceiveMessage(string.Format("The room with id of {0} already has an exit that leads back here.", newDestination));
                        }
                        else if (roomToLink.Exits != null && roomToLink.Exits.FirstOrDefault(x => x.Name == newName) != null)
                        {
                            commandIssuer.ReceiveMessage(string.Format("The room with id {0} already has {1} {2} exit.", roomToLink.Id, newName.GetArticle(), newName));
                        }
                        else
                        {
                            roomToLink.Exits.Add(new Exit() { DestinationId = currentLocation.Id, Name = returnName });
                            roomToLink.RebuildExitCommands();
                            if (!roomToLink.Save())
                            {
                                commandIssuer.ReceiveMessage("Return exit could not be saved.");
                            }
                        }
                    }
                    commandIssuer.DataReceivedHandler(commandIssuer, "look");
                    break;
                case TerraformOption.UNLINK:
                    // Remove an exit.
                    if (commandArgs == null || commandArgs.Length < 1)
                    {
                        commandIssuer.ReceiveMessage("You must provide a room id or exit name to unlink it.\r\nFormat: terraform unlink <room id or exit>");
                        return;
                    }


                    string exitArg = (string)commandArgs[0];
                    Exit exitToRemove = currentLocation.Exits.FirstOrDefault(e => e.Name.ToLower() == exitArg.ToLower());
                    if (exitToRemove == null)
                    {
                        exitToRemove = currentLocation.Exits.FirstOrDefault(e => e.DestinationId.ToLower() == exitArg.ToLower());
                    }
                    if (exitToRemove == null)
                    {
                        commandIssuer.ReceiveMessage(string.Format("Did not find an exit here for {0}.", exitArg));
                        return;
                    }
                    currentLocation.Exits.Remove(exitToRemove);
                    currentLocation.RebuildExitCommands();
                    commandIssuer.DataReceivedHandler(commandIssuer, "look");

                    break;
                case TerraformOption.SETSHORT:
                    // Set the short description for this room.
                    break;
                case TerraformOption.SETLONG:
                    // Set the long description for this room.
                    break;
            }
        }
    }
}