using System.Collections.Generic;
using System.Linq;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using MUD.Core.Properties.PlayerProperties;
using Xunit;

namespace MUD.Tests
{
    public class TransCommandTests
    {
        // Records everything it is told instead of writing to a socket.
        private class RecordingPlayer : Player
        {
            public List<string> Messages = new List<string>();

            public RecordingPlayer(World world, string name) : base(world, null, new FakePlayerRepo())
            {
                PlayerName = name;
                NameKey = name.ToLowerInvariant();
                ShortDescription = name;
            }

            public override void ReceiveMessage(string message) { Messages.Add(message); }
        }

        public enum Role { Player, Creator, Admin }

        private class Fixture
        {
            // World's constructor registers global Mongo class maps and races with other tests, so skip it: only Players is needed.
            public World World = (World)System.Runtime.CompilerServices.RuntimeHelpers.GetUninitializedObject(typeof(World));
            public Dictionary<string, Room> Rooms = new Dictionary<string, Room>();
            public TransCommand Command;

            public Fixture()
            {
                World.Players = new List<Player>();
                Command = new TransCommand(World) { RoomLookup = key => Rooms.TryGetValue(key, out var r) ? r : null };
            }

            public Room AddRoom(string id)
            {
                var room = new Room(new FakeRoomRepo()) { Id = id, ShortDescription = id, LongDescription = "A room." };
                Rooms[id] = room;
                return room;
            }

            public RecordingPlayer AddPlayer(string name, Role role, Room room, bool online = true)
            {
                var p = new RecordingPlayer(World, name);
                if (role >= Role.Creator) { p.ExtendedProperties.Add(new CreatorProperty()); }
                if (role == Role.Admin) { p.ExtendedProperties.Add(new AdminProperty()); }
                if (room != null)
                {
                    p.CurrentLocation = room;
                    p.CurrentLocationId = room.Id;
                    room.Occupants.Add(p);
                }
                if (online) { World.Players.Add(p); }
                return p;
            }

            public void Run(Living issuer, string input)
            {
                Command.DoCommand(issuer, Command.ParseCommand(issuer, input));
            }
        }

        [Fact]
        public void IsRegisteredAsCreatorCommandByTheNormalScan()
        {
            // The command is a concrete ICommand with a World constructor, which is what World's DI assembly scan registers.
            var type = typeof(TransCommand);
            Assert.True(typeof(ICommand).IsAssignableFrom(type) && !type.IsAbstract);
            Assert.NotNull(type.GetConstructor(new[] { typeof(World) }));
            Assert.Equal(CommandCategories.Creator, new Fixture().Command.CommandCategory);
            Assert.Equal(new[] { "trans" }, new Fixture().Command.CommandKeywords);
        }

        [Fact]
        public void HelpTextStatesSyntaxAndRules()
        {
            var f = new Fixture();
            Assert.Contains("trans <player name> [<room key>]", f.Command.HelpText);
            Assert.Contains("lower role", f.Command.HelpText);
        }

        [Fact]
        public void PlayerNameOnlyBringsThemToTheIssuersRoom()
        {
            var f = new Fixture();
            Room here = f.AddRoom("t.here"), there = f.AddRoom("t.there");
            var issuer = f.AddPlayer("Boss", Role.Creator, here);
            var target = f.AddPlayer("Bob", Role.Player, there);

            f.Run(issuer, "trans bob");

            Assert.Same(here, target.CurrentLocation);
            Assert.Equal("t.here", target.CurrentLocationId);
            Assert.Contains(target, here.Occupants);
            Assert.DoesNotContain(target, there.Occupants);
        }

        [Fact]
        public void PlayerNameAndRoomKeySendsThemThere()
        {
            var f = new Fixture();
            Room here = f.AddRoom("t.here"), there = f.AddRoom("t.there"), dest = f.AddRoom("t.dest");
            var issuer = f.AddPlayer("Boss", Role.Creator, here);
            var target = f.AddPlayer("Bob", Role.Player, there);

            f.Run(issuer, "trans BOB t.dest");

            Assert.Same(dest, target.CurrentLocation);
            Assert.Equal("t.dest", target.CurrentLocationId);
            Assert.Contains(target, dest.Occupants);
            Assert.DoesNotContain(target, there.Occupants);
            Assert.Same(here, issuer.CurrentLocation);
        }

        [Fact]
        public void OfflineTargetIsRefusedAndNobodyMoves()
        {
            var f = new Fixture();
            Room here = f.AddRoom("t.here"), there = f.AddRoom("t.there");
            var issuer = f.AddPlayer("Boss", Role.Creator, here);
            var target = f.AddPlayer("Bob", Role.Player, there, online: false);

            f.Run(issuer, "trans bob");

            Assert.Same(there, target.CurrentLocation);
            Assert.Contains(issuer.Messages, m => m.Contains("not online"));
        }

        [Fact]
        public void NameMatchIsExactNotSubstring()
        {
            var f = new Fixture();
            Room here = f.AddRoom("t.here"), there = f.AddRoom("t.there");
            var issuer = f.AddPlayer("Boss", Role.Creator, here);
            var target = f.AddPlayer("Bobby", Role.Player, there);

            f.Run(issuer, "trans bob");

            Assert.Same(there, target.CurrentLocation);
            Assert.Contains(issuer.Messages, m => m.Contains("not online"));
        }

        [Fact]
        public void UnknownRoomMovesNobodyAndSaysWhy()
        {
            var f = new Fixture();
            Room here = f.AddRoom("t.here"), there = f.AddRoom("t.there");
            var issuer = f.AddPlayer("Boss", Role.Creator, here);
            var target = f.AddPlayer("Bob", Role.Player, there);
            var bystander = f.AddPlayer("Cat", Role.Player, there);
            int issuerBefore = issuer.Messages.Count;

            f.Run(issuer, "trans bob t.nowhere");

            Assert.Same(there, target.CurrentLocation);
            Assert.Same(here, issuer.CurrentLocation);
            Assert.Empty(target.Messages);
            Assert.Empty(bystander.Messages);
            Assert.Contains(issuer.Messages.Skip(issuerBefore), m => m.Contains("t.nowhere") && m.Contains("Nobody was moved"));
        }

        [Theory]
        [InlineData(Role.Creator, Role.Player, true)]
        [InlineData(Role.Creator, Role.Creator, false)]
        [InlineData(Role.Creator, Role.Admin, false)]
        [InlineData(Role.Admin, Role.Player, true)]
        [InlineData(Role.Admin, Role.Creator, true)]
        [InlineData(Role.Admin, Role.Admin, false)]
        public void OnlyStrictlyLowerRolesCanBeMoved(Role issuerRole, Role targetRole, bool allowed)
        {
            var f = new Fixture();
            Room here = f.AddRoom("t.here"), there = f.AddRoom("t.there");
            var issuer = f.AddPlayer("Boss", issuerRole, here);
            var target = f.AddPlayer("Bob", targetRole, there);

            f.Run(issuer, "trans bob");

            Assert.Same(allowed ? here : there, target.CurrentLocation);
            if (!allowed)
            {
                Assert.Contains(issuer.Messages, m => m.Contains("lower role"));
                Assert.Empty(target.Messages);
            }
        }

        [Fact]
        public void PlainPlayerCannotUseIt()
        {
            var f = new Fixture();
            Room here = f.AddRoom("t.here"), there = f.AddRoom("t.there");
            var issuer = f.AddPlayer("Boss", Role.Player, here);
            var target = f.AddPlayer("Bob", Role.Player, there);

            f.Run(issuer, "trans bob");

            Assert.Same(there, target.CurrentLocation);
        }

        [Theory]
        [InlineData(Role.Creator)]
        [InlineData(Role.Admin)]
        public void TargetingYourselfIsRejectedWithANavigateHint(Role role)
        {
            var f = new Fixture();
            Room here = f.AddRoom("t.here"), dest = f.AddRoom("t.dest");
            var issuer = f.AddPlayer("Boss", role, here);

            f.Run(issuer, "trans boss t.dest");

            Assert.Same(here, issuer.CurrentLocation);
            Assert.Contains(issuer.Messages, m => m.Contains("navigate"));
        }

        [Fact]
        public void MessagesGoToTheRightPlaces()
        {
            var f = new Fixture();
            Room here = f.AddRoom("t.here"), there = f.AddRoom("t.there"), dest = f.AddRoom("t.dest");
            var issuer = f.AddPlayer("Boss", Role.Admin, here);
            var target = f.AddPlayer("Bob", Role.Player, there);
            var originWatcher = f.AddPlayer("Origin", Role.Player, there);
            var destWatcher = f.AddPlayer("Dest", Role.Player, dest);
            var hereWatcher = f.AddPlayer("Here", Role.Player, here);

            f.Run(issuer, "trans bob t.dest");

            Assert.Contains("Bob is whisked away by a strange force.", originWatcher.Messages);
            Assert.DoesNotContain(originWatcher.Messages, m => m.Contains("appears in a flash"));
            Assert.Contains("Bob appears in a flash of light.", destWatcher.Messages);
            Assert.DoesNotContain(destWatcher.Messages, m => m.Contains("whisked"));
            Assert.Empty(hereWatcher.Messages);

            Assert.Contains("You have been moved by Boss.", target.Messages);
            Assert.Contains(target.Messages, m => m.Contains("A room."));
            Assert.DoesNotContain(target.Messages, m => m.Contains("whisked") || m.Contains("appears in a flash"));
        }

        [Fact]
        public void MissingArgumentsShowSyntax()
        {
            var f = new Fixture();
            var issuer = f.AddPlayer("Boss", Role.Creator, f.AddRoom("t.here"));

            f.Run(issuer, "trans");
            f.Run(issuer, "trans a b c");

            Assert.Equal(2, issuer.Messages.Count(m => m.Contains("Syntax: trans")));
        }
    }
}
