using System;
using System.Collections.Generic;
using System.Linq;
using MUD.Core.Formatting;
using Xunit;

namespace MUD.Tests
{
    // Characterization of the markup-to-terminal token tables. The golden strings were taken from the handlers as they
    // were before they moved out of core into the telnet project; the move changes where the handlers live, not these values.
    public class TerminalHandlerTests
    {
        // Select Graphic Rendition codes, identical for the Ansi and GMud handlers.
        private static readonly Dictionary<string, int> GoldenCodes = new Dictionary<string, int>
        {
            { "Reset", 0 }, { "Bold", 1 }, { "Underline", 4 },
            { "Black", 30 }, { "Red", 31 }, { "Green", 32 }, { "Yellow", 33 },
            { "Blue", 34 }, { "Magenta", 35 }, { "Cyan", 36 }, { "White", 37 },
            { "BrightBlack", 90 }, { "BrightRed", 91 }, { "BrightGreen", 92 }, { "BrightYellow", 93 },
            { "BrightBlue", 94 }, { "BrightMagenta", 95 }, { "BrightCyan", 96 }, { "BrightWhite", 97 },
            { "BGBlack", 40 }, { "BGRed", 41 }, { "BGGreen", 42 }, { "BGYellow", 43 },
            { "BGBlue", 44 }, { "BGMagenta", 45 }, { "BGCyan", 46 }, { "BGWhite", 47 },
            { "BGBrightBlack", 100 }, { "BGBrightRed", 101 }, { "BGBrightGreen", 102 }, { "BGBrightYellow", 103 },
            { "BGBrightBlue", 104 }, { "BGBrightMagenta", 105 }, { "BGBrightCyan", 106 }, { "BGBrightWhite", 107 },
        };

        private static string Convert(ITerminalHandler handler, string tokenName)
        {
            return handler.ResolveOutput("%^" + tokenName + "%^");
        }

        [Fact]
        public void GoldenTableCoversEveryFormatOption()
        {
            Assert.Equal(Enum.GetNames(typeof(EFormatOptions)).OrderBy(n => n), GoldenCodes.Keys.OrderBy(n => n));
        }

        [Fact]
        public void AnsiHandlerTokenTable()
        {
            var handler = new AnsiTerminalHandler();
            Assert.Equal("Ansi", handler.TerminalName);
            Assert.Equal(new[] { "Mudlet" }, handler.Aliases);
            foreach (var entry in GoldenCodes)
            {
                Assert.Equal("\u001b[" + entry.Value + "m", Convert(handler, entry.Key));
            }
        }

        [Fact]
        public void GMudHandlerTokenTable()
        {
            var handler = new GMudTerminalHandler();
            Assert.Equal("GMud", handler.TerminalName);
            Assert.Null(handler.Aliases);
            foreach (var entry in GoldenCodes)
            {
                Assert.Equal("\u001b[" + entry.Value + "m", Convert(handler, entry.Key));
            }
        }

        [Fact]
        public void DefaultHandlerRemovesEveryToken()
        {
            var handler = new DefaultTerminalHandler();
            Assert.Equal("Default", handler.TerminalName);
            Assert.Null(handler.Aliases);
            foreach (var name in GoldenCodes.Keys)
            {
                Assert.Equal("", Convert(handler, name));
            }
        }

        [Fact]
        public void HandlersLeaveOrdinaryAndUnknownTextAlone()
        {
            foreach (ITerminalHandler handler in new ITerminalHandler[] { new AnsiTerminalHandler(), new GMudTerminalHandler(), new DefaultTerminalHandler() })
            {
                Assert.Equal("plain text", handler.ResolveOutput("plain text"));
                Assert.Equal("%^NoSuchToken%^", handler.ResolveOutput("%^NoSuchToken%^"));
            }
        }

        [Fact]
        public void HandlersConvertTokensInsideText()
        {
            ITerminalHandler ansi = new AnsiTerminalHandler();
            ITerminalHandler neutral = new DefaultTerminalHandler();
            Assert.Equal("\u001b[31mhi\u001b[0m", ansi.ResolveOutput("%^Red%^hi%^Reset%^"));
            Assert.Equal("hi", neutral.ResolveOutput("%^Red%^hi%^Reset%^"));
        }
    }
}
