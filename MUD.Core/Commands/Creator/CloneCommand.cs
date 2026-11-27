using System;
using MongoDB.Driver;
using MUD.Core.Components;
using MUD.Core.GameObjects;
using MUD.Core.ItemTemplates;

namespace MUD.Core.Commands
{
    // 'clone <area>.<local>': puts a copy of an item template (specs/ITEM_TEMPLATES.md) into the creator's inventory. Not a new command
    // class: this is the existing 'clone' stub, now with a body.
    public class CloneCommand : ICommand
    {
        public const string NoSuchTemplate = "No such item template.";

        private readonly Func<string, ItemTemplateEntry> _lookup;

        // World's DI scan builds the command with the host's database.
        public CloneCommand(IMongoDatabase database) : this(Lookup(database)) { }

        // Test seam, as for regen and update: the template lookup is supplied instead of built from a database. Internal, so World's DI scan
        // (which only considers public constructors) never sees it.
        internal CloneCommand(Func<string, ItemTemplateEntry> lookup)
        {
            _lookup = lookup;
        }

        private static Func<string, ItemTemplateEntry> Lookup(IMongoDatabase database)
        {
            var store = new MongoItemTemplateStore(database);
            return id => store.Get(id);
        }

        public string[] CommandKeywords { get => new string[] { "clone" }; }

        public CommandCategories CommandCategory { get => CommandCategories.Creator; }

        public string HelpText { get => "Put a copy of an item template from the item template library into your inventory. The copy is a new item: its descriptions, words, components and behaviors are copied from the template as it is now, and later changes to the template do not change it. Syntax: clone <area>.<local key>   (for example: clone temple.brass_lamp)"; }

        public object[] ParseCommand(Living commandIssuer, string input)
        {
            return new object[] { this.StripKeyword(input) };
        }

        public void DoCommand(Living commandIssuer, object[] commandArgs)
        {
            Roles creatorStatusObj = commandIssuer.Roles.Creator ? commandIssuer.Roles : null;
            if (creatorStatusObj == null)
            {
                commandIssuer.ReceiveMessage("This is a creator only command. How did you get this?!");
                return;
            }

            string id = ((commandArgs != null && commandArgs.Length > 0 ? commandArgs[0] as string : null) ?? string.Empty).Trim();
            if (id.Length == 0)
            {
                commandIssuer.ReceiveMessage("Syntax: clone <area>.<local key>");
                return;
            }

            ItemTemplateEntry template;
            try { template = _lookup(id); }
            catch (Exception ex) when (!(ex is OutOfMemoryException))
            {
                commandIssuer.ReceiveMessage("The item template library could not be read (" + ex.GetType().Name + "). See the server log.");
                return;
            }
            if (template == null)
            {
                commandIssuer.ReceiveMessage(NoSuchTemplate);
                return;
            }

            InventoryItem item = ItemTemplateCopy.ToItem(template);
            commandIssuer.Inventory.Add(item);
            // Write-through like 'get': the player is saved so the item survives a restart.
            (commandIssuer as Player)?.Save(true);
            commandIssuer.ReceiveMessage("You clone " + item.ShortDescription + " (" + template.Id + ", version " + template.Version + ").");
        }
    }
}
