using System.Linq;
using MUD.Core.Import;
using Xunit;

namespace MUD.Tests
{
    // Characterization of the importer's room validation (specs/ROOM_FILES.md sections 3, 6 and 11), written before the
    // rules were extracted into MUD.Data (specs/WEB_EDITING.md section 13 step 8). These pin what the importer
    // accepts and rejects today so the extraction is a deliberate, reviewed change.
    public class RoomRulesCharacterizationTests
    {
        private static string Yaml(string prefix, string key, string shortText = "a hall", string longText = "A hall.", string exits = "")
        {
            return "area:\n  schema_version: 1\n  prefix: " + prefix + "\n  name: T\nrooms:\n  - key: " + key + "\n    short: " + shortText + "\n    long: " + longText + "\n" + exits;
        }

        private static ImportResult Import(string yaml) { return new AreaImporter(new FakeStore()).Import(yaml, true); }

        [Theory]
        [InlineData("hall")]
        [InlineData("hall_2")]
        [InlineData("a1")]
        public void ValidLocalKeysAreAccepted(string key)
        {
            Assert.True(Import(Yaml("t", key)).Success);
        }

        [Theory]
        [InlineData("Hall")]
        [InlineData("has space")]
        [InlineData("a-b")]
        public void InvalidLocalKeysAreRejected(string key)
        {
            var result = Import(Yaml("t", "\"" + key + "\""));
            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("key must match"));
        }

        [Theory]
        [InlineData("t")]
        [InlineData("tower_of_aellioc")]
        [InlineData("a.b.c")]
        public void ValidAreaPrefixesAreAccepted(string prefix)
        {
            Assert.True(Import(Yaml(prefix, "hall")).Success);
        }

        [Theory]
        [InlineData("T")]
        [InlineData("a..b")]
        [InlineData("a b")]
        public void InvalidAreaPrefixesAreRejected(string prefix)
        {
            var result = Import(Yaml("\"" + prefix + "\"", "hall"));
            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("not a valid key prefix"));
        }

        [Fact]
        public void DuplicateExitDirectionsInOneRoomAreRejectedCaseInsensitively()
        {
            var exits = "    exits:\n      - {dir: north, to: t.b}\n      - {dir: North, to: t.b}\n  - key: b\n    short: b\n    long: b\n";
            var result = Import(Yaml("t", "a", exits: exits));
            Assert.False(result.Success);
            Assert.Contains(result.Errors, e => e.Contains("duplicate exit"));
        }

        [Fact]
        public void NormalizeTextTrimsLineEndsAndConvertsLineEndings()
        {
            Assert.Equal("a\nb", AreaParser.NormalizeText("  a  \r\nb \r\n\r\n"));
            Assert.Equal("a\nb", AreaParser.NormalizeText("a\rb"));
            Assert.Equal("", AreaParser.NormalizeText("   "));
        }

        [Fact]
        public void KeyPatternsAreExposed()
        {
            Assert.True(AreaParser.LocalKeyPattern.IsMatch("hall_1"));
            Assert.False(AreaParser.LocalKeyPattern.IsMatch("a.b"));
            Assert.True(AreaParser.PrefixPattern.IsMatch("a.b_c"));
            Assert.False(AreaParser.PrefixPattern.IsMatch("a."));
        }

        [Fact]
        public void ExitDirectionsAndTextLengthsAreNotLimitedToday()
        {
            // Pinned on purpose: the importer has no length or direction grammar yet. The extraction in the next commit
            // changes this deliberately (specs/WEB_EDITING.md section 10, validation parity) and updates this test.
            var longText = new string('x', 5000);
            var exits = "    exits:\n      - {dir: \"go-to 1\", to: t.a}\n";
            Assert.True(Import(Yaml("t", "a", longText: longText, exits: exits)).Success);
        }
    }
}
