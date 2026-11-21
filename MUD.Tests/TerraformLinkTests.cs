using System;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using MUD.Core;
using MUD.Core.Commands;
using MUD.Core.GameObjects;
using MUD.Core.Repositories.Interfaces;
using MUD.Data;
using Xunit;

namespace MUD.Tests
{
    public class TerraformLinkTests
    {
        private static (RecordingPlayer player, Room here, Room other, World world) Setup()
        {
            var repo = new LookupRoomRepo();
            var world = TestWorld.Create();
            var services = new ServiceCollection().AddSingleton<IRoomRepository>(repo).BuildServiceProvider();
            typeof(World).GetField("_serviceProvider", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(world, services);

            var here = new Room(repo) { Id = "t.here" };
            var other = new Room(repo) { Id = "t.other" };
            repo.Target = other;
            var player = new RecordingPlayer(world, "creator") { CurrentLocation = here };
            return (player, here, other, world);
        }

        private static void Link(RecordingPlayer player, World world, string line)
        {
            var cmd = new TerraformCommand(world);
            var args = cmd.ParseCommand(player, line);
            try { cmd.DoCommand(player, args); }
            catch (NullReferenceException) { /* the trailing "look" needs a live command queue; the link work is already done */ }
        }

        [Fact]
        public void ReturnExitIsCheckedByReturnNameNotForwardName()
        {
            var (player, here, other, world) = Setup();
            other.Exits.Add(new Exit { DestinationId = "t.elsewhere", Name = "east" });

            Link(player, world, "terraform link t.other east west");

            Assert.Contains(here.Exits, x => x.Name == "east" && x.DestinationId == "t.other");
            Assert.Contains(other.Exits, x => x.Name == "west" && x.DestinationId == "t.here");
            Assert.DoesNotContain(player.Messages, m => m.Contains("already has"));
        }

        [Fact]
        public void ReturnExitRefusedWhenReturnNameTakenAndMessageNamesReturnExit()
        {
            var (player, here, other, world) = Setup();
            other.Exits.Add(new Exit { DestinationId = "t.elsewhere", Name = "west" });

            Link(player, world, "terraform link t.other east west");

            Assert.Single(other.Exits);
            Assert.Contains(player.Messages, m => m.Contains("already has a west exit"));
        }
    }

    // Returns a fixed room for Get so the command mutates the test's instance.
    public class LookupRoomRepo : FakeRepo<Room>, IRoomRepository
    {
        public Room Target;
        public new Room Get(string id) { return Target != null && id == Target.Id ? Target : null; }
    }
}
