using MUD.Core;
using Xunit;

namespace MUD.Tests
{
    public class WorldSettingsTests
    {
        [Fact]
        public void ImportOnStartup_DefaultsToFalse()
        {
            Assert.False(new WorldSettings().ImportOnStartup);
        }
    }
}
