using System.Linq;
using MUD.Core;
using MUD.Telnet;
using Xunit;

namespace MUD.Tests
{
    /// <summary>The project layout of phase 1: core and the telnet front end only meet through MUD.CoreApi.</summary>
    public class ProjectBoundaryTests
    {
        private static string[] ReferencedAssemblyNames(System.Reflection.Assembly assembly)
        {
            return assembly.GetReferencedAssemblies().Select(name => name.Name).ToArray();
        }

        [Fact]
        public void CoreAssemblyHasNoReferenceToTheTelnetProject()
        {
            Assert.DoesNotContain("MUD.Telnet", ReferencedAssemblyNames(typeof(World).Assembly));
        }

        [Fact]
        public void TelnetAssemblyHasNoReferenceToCoreOrData()
        {
            string[] references = ReferencedAssemblyNames(typeof(Server).Assembly);
            Assert.DoesNotContain("MUD.Core", references);
            Assert.DoesNotContain("MUD.Data", references);
        }
    }
}
