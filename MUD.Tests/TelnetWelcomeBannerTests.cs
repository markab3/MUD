using System;
using Xunit;

namespace MUD.Tests
{
    /// <summary>The telnet login screen shows the ASCII art banner above the help text and fits an 80 column terminal.</summary>
    public class TelnetWelcomeBannerTests
    {
        private static readonly string[] Lines = MUD.Telnet.Host.Program.WelcomeMessage.Replace("\r\n", "\n").Split('\n');

        [Fact]
        public void ContainsTheBannerArt()
        {
            string text = MUD.Telnet.Host.Program.WelcomeMessage;
            Assert.Contains("[#########[]____________________________________________\\", text);
            Assert.Contains("| |__) | | __ _ _ __   __ _ _ __", text);
            Assert.Contains("|_|  \\_\\___|\\__,_|_|_| |_| |_|___/", text);
        }

        [Fact]
        public void KeepsTitleAndHelpText()
        {
            string text = MUD.Telnet.Host.Program.WelcomeMessage;
            Assert.Contains("Welcome to Planar Realms 0.2", text);
            foreach (string word in new[] { "'connect'", "'create'", "'who'", "'quit'" })
                Assert.Contains(word, text);
        }

        [Fact]
        public void NoLineIsWiderThan80Characters()
        {
            foreach (string line in Lines)
                Assert.True(line.Length <= 80, "Line is " + line.Length + " wide: " + line);
        }
    }
}
