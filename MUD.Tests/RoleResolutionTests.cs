using System.Collections.Generic;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using Xunit;

namespace MUD.Tests
{
    // Pins how Living.ResolveCommand uses roles, including the Admin quirk (ARCHITECTURE_V2 section 11 item 1): the
    // Admin branch consults the creator commands, and the admin commands are never consulted by ResolveCommand.
    // The owner decides separately whether that is right; these tests pin today's behavior.
    public class RoleResolutionTests
    {
        private class StubCommand : ICommand
        {
            public StubCommand(string keyword, CommandCategories category)
            {
                CommandKeywords = new[] { keyword };
                CommandCategory = category;
            }

            public string[] CommandKeywords { get; }
            public CommandCategories CommandCategory { get; }
            public string HelpText { get { return string.Empty; } }
            public object[] ParseCommand(Living commandIssuer, string input) { return new object[0]; }
            public void DoCommand(Living commandIssuer, object[] commandArgs) { }
        }

        private static readonly StubCommand DefaultOnly = new StubCommand("wave", CommandCategories.Default);
        private static readonly StubCommand CreatorOnly = new StubCommand("dig", CommandCategories.Creator);
        private static readonly StubCommand AdminOnly = new StubCommand("shutdownworld", CommandCategories.Admin);
        // The same keyword in the creator list and the default list: the creator list is consulted first.
        private static readonly StubCommand CreatorShadow = new StubCommand("look", CommandCategories.Creator);
        private static readonly StubCommand DefaultShadowed = new StubCommand("look", CommandCategories.Default);

        private static Living MakeLiving(bool creator, bool admin)
        {
            var world = TestWorld.Create();
            world.DefaultCommands = new CommandSource(new List<ICommand> { DefaultOnly, DefaultShadowed });
            world.CreatorCommands = new CommandSource(new List<ICommand> { CreatorOnly, CreatorShadow });
            world.AdminCommands = new CommandSource(new List<ICommand> { AdminOnly });
            world.AllCommands = new CommandSource();
            var living = new RecordingLiving(world);
            if (creator) { living.Roles.Creator = true; }
            if (admin) { living.Roles.Admin = true; }
            return living;
        }

        [Fact]
        public void PlainLiving_ResolvesDefaultCommandsOnly()
        {
            var living = MakeLiving(false, false);
            Assert.Same(DefaultOnly, living.ResolveCommand("wave"));
            Assert.Null(living.ResolveCommand("dig"));
            Assert.Null(living.ResolveCommand("shutdownworld"));
            Assert.Same(DefaultShadowed, living.ResolveCommand("look"));
        }

        [Fact]
        public void Creator_ResolvesCreatorCommandsBeforeDefaultOnes()
        {
            var living = MakeLiving(true, false);
            Assert.Same(CreatorOnly, living.ResolveCommand("dig"));
            Assert.Same(CreatorShadow, living.ResolveCommand("look"));
            Assert.Same(DefaultOnly, living.ResolveCommand("wave"));
            Assert.Null(living.ResolveCommand("shutdownworld"));
        }

        [Fact]
        public void Admin_ResolvesTheCreatorCommands_AndNeverTheAdminCommands()
        {
            foreach (var living in new[] { MakeLiving(false, true), MakeLiving(true, true) })
            {
                Assert.Same(CreatorOnly, living.ResolveCommand("dig"));
                Assert.Same(CreatorShadow, living.ResolveCommand("look"));
                Assert.Same(DefaultOnly, living.ResolveCommand("wave"));
                Assert.Null(living.ResolveCommand("shutdownworld"));
            }
        }
    }
}
