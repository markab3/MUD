using System.IO;
using System.Linq;
using MUD.Core.Generation;
using MUD.Core.Import;
using Xunit;

namespace MUD.Tests
{
    public class MapGeneratorTests
    {
        private const string Map = @"
generator:
  prefix: city
  name: Test City
  seed: s1
grid:
  - '#####'
  - '#..P#'
  - '#.#.#'
  - '#...#'
  - '#####'
legend:
  '#': wall
  '.': street
  P: plaza
templates:
  wall: {walkable: false}
  street:
    short: [a street, a lane, an alley]
    long:
      - [One a., One b., One c., One d.]
      - [Two a., Two b., Two c., Two d.]
  plaza:
    short: a plaza
    long: [[A plaza.]]
overrides:
  - at: {x: 1, y: 1}
    short: the landmark
    long: A hand-written landmark.
";

        private static AreaDefinition Parse(string yaml)
        {
            var parsed = AreaParser.Parse(yaml);
            Assert.True(parsed.Errors.Count == 0, string.Join("; ", parsed.Errors));
            return parsed.Area;
        }

        private static string MapPath()
        {
            for (var d = new DirectoryInfo(Directory.GetCurrentDirectory()); d != null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, "MUD.Main", "Content", "maps", "sample_city.map.yaml");
                if (File.Exists(candidate)) { return candidate; }
            }
            return null;
        }

        [Fact]
        public void KeysAreDerivedFromCoordinates()
        {
            var result = MapGenerator.Generate(Map);
            Assert.True(result.Success, string.Join("; ", result.Errors));
            var area = Parse(result.Yaml);
            // 3x3 interior minus the centre pillar = 8 rooms; walls make none.
            Assert.Equal(8, area.Rooms.Count);
            Assert.Equal(8, result.RoomCount);
            Assert.Contains(area.Rooms, r => r.FullKey == "city.x01y01" && r.X == 1 && r.Y == 1);
            // Row 1 of the grid is the second from the north, so the plaza (column 3) sits at y=3.
            Assert.Contains(area.Rooms, r => r.FullKey == "city.x03y03");
            Assert.DoesNotContain(area.Rooms, r => r.FullKey == "city.x02y02");
        }

        [Fact]
        public void SameInputGivesByteIdenticalOutput()
        {
            Assert.Equal(MapGenerator.Generate(Map).Yaml, MapGenerator.Generate(Map).Yaml);
        }

        [Fact]
        public void DifferentSeedChangesDescriptions()
        {
            var a = MapGenerator.Generate(Map).Yaml;
            var seeds = new[] { "s2", "s3", "s4", "s5" };
            Assert.Contains(seeds, s => MapGenerator.Generate(Map.Replace("seed: s1", "seed: " + s)).Yaml != a);
        }

        [Fact]
        public void CellTextIsIndependentOfOtherCells()
        {
            // Add a row to the south and a column to the east: existing cells keep their key AND their text.
            var bigger = Map.Substring(0, Map.IndexOf("grid:")) + @"grid:
  - '######'
  - '#..P.#'
  - '#.#.##'
  - '#...##'
  - '######'
" + Map.Substring(Map.IndexOf("legend:"));
            var before = Parse(MapGenerator.Generate(Map).Yaml);
            var after = Parse(MapGenerator.Generate(bigger).Yaml);
            foreach (var room in before.Rooms)
            {
                var other = after.Rooms.Single(r => r.FullKey == room.FullKey);
                Assert.Equal(room.Short, other.Short);
                Assert.Equal(room.Long, other.Long);
            }
        }

        [Fact]
        public void ExitsAreSymmetricAndOnlyJoinWalkableNeighbours()
        {
            var area = Parse(MapGenerator.Generate(Map).Yaml);
            var byKey = area.Rooms.ToDictionary(r => r.FullKey);
            var opposite = new System.Collections.Generic.Dictionary<string, string>
            {
                { "north", "south" }, { "south", "north" }, { "east", "west" }, { "west", "east" },
            };
            int exitCount = 0;
            foreach (var room in area.Rooms)
            {
                Assert.Equal(room.Exits.Count, room.Exits.Select(e => e.Dir).Distinct().Count());
                foreach (var exit in room.Exits)
                {
                    exitCount++;
                    var destKey = exit.To;
                    Assert.True(byKey.ContainsKey(destKey), room.FullKey + " exits to missing " + destKey);
                    var back = byKey[destKey].Exits.SingleOrDefault(e => e.Dir == opposite[exit.Dir]);
                    Assert.NotNull(back);
                    Assert.Equal(room.FullKey, back.To);
                }
            }
            // 8 ring rooms form a cycle of 8 links = 16 directed exits.
            Assert.Equal(16, exitCount);
            // The pillar blocks the centre: no room links to it.
            Assert.DoesNotContain(area.Rooms.SelectMany(r => r.Exits), e => e.To == "city.x02y02");
        }

        [Fact]
        public void OverrideReplacesGeneratedTextAndFeaturesPassThrough()
        {
            var map = Map.Replace("    long: A hand-written landmark.\n", @"    long: A hand-written landmark.
    features:
      - short: a statue
        long: A statue.
        handles: [statue]
");
            var area = Parse(MapGenerator.Generate(map).Yaml);
            var room = area.Rooms.Single(r => r.FullKey == "city.x01y01");
            Assert.Equal("the landmark", room.Short);
            Assert.Equal("A hand-written landmark.", room.Long);
            Assert.Equal("a statue", room.Features.Single().Short);
        }

        [Fact]
        public void UnknownGlyphUnknownTemplateAndBadOverrideAreAllReported()
        {
            var map = Map.Replace("'#..P#'", "'#.?P#'")
                         .Replace("at: {x: 1, y: 1}", "at: {x: 40, y: 40}");
            var result = MapGenerator.Generate(map);
            Assert.False(result.Success);
            Assert.Null(result.Yaml);
            Assert.Contains(result.Errors, e => e.Contains("'?'"));
            Assert.Contains(result.Errors, e => e.Contains("no cell at x=40"));
        }

        [Fact]
        public void KeyFormatMatchesSpecExample()
        {
            Assert.Equal("x12y07", MapGenerator.KeyFor(12, 7));
            Assert.Equal("xn03y100", MapGenerator.KeyFor(-3, 100));
        }

        [Fact]
        public void SampleMapGeneratesAnImportableAreaThatReimportsUnchanged()
        {
            var path = MapPath();
            Assert.NotNull(path);
            var result = MapGenerator.Generate(File.ReadAllText(path));
            Assert.True(result.Success, string.Join("; ", result.Errors));

            var store = new FakeStore();
            var importer = new AreaImporter(store);
            var first = importer.Import(result.Yaml);
            Assert.True(first.Success, string.Join("; ", first.Errors));
            Assert.Equal(result.RoomCount, first.Count(ImportAction.Insert));

            // Regenerate and re-import: nothing changes, nothing is written.
            var writes = store.UpsertCount;
            var again = importer.Import(MapGenerator.Generate(File.ReadAllText(path)).Yaml);
            Assert.Equal(result.RoomCount, again.Count(ImportAction.Unchanged));
            Assert.Equal(writes, store.UpsertCount);
        }
    }
}
