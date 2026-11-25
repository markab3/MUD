using System;
using System.Collections.Generic;
using System.Linq;

namespace MUD.Core.Generation.Terrains
{
    // Neighbour scan helpers (spec 7.4).
    public static class NeighbourPhrases
    {
        public static readonly int[] Dx = { 0, 1, 1, 1, 0, -1, -1, -1 };
        public static readonly int[] Dy = { 1, 1, 0, -1, -1, -1, 0, 1 };

        private static readonly string[] Names = { "north", "northeast", "east", "southeast", "south", "southwest", "west", "northwest" };
        // Wording order: cardinals N, E, S, W then diagonals NE, SE, SW, NW.
        private static readonly Dir[] WordingOrder = { Dir.N, Dir.E, Dir.S, Dir.W, Dir.NE, Dir.SE, Dir.SW, Dir.NW };

        public static string Name(Dir d) { return Names[(int)d]; }

        // "north", "north and east", "north, east and south"; all eight sides -> "all around".
        public static string JoinDirs(IEnumerable<Dir> dirs)
        {
            var set = new HashSet<Dir>(dirs);
            if (set.Count == 8) { return "all around"; }
            var words = WordingOrder.Where(set.Contains).Select(Name).ToList();
            if (words.Count == 0) { return ""; }
            if (words.Count == 1) { return words[0]; }
            return string.Join(", ", words.Take(words.Count - 1)) + " and " + words[words.Count - 1];
        }

        private class Candidate
        {
            public int Sides;
            public Dir First;
            public string Terrain;
            public string Text;
        }

        // At most `cap` sentences. One weighted phrase pick per neighbour terrain (draws happen for every
        // qualifying group before the cap is applied, so the cap never shifts later draws). Groups with the
        // most matching sides win; ties by first Dir order, then neighbour terrain ordinal. No RNG in ranking.
        public static List<string> Build(GenContext ctx, string roomTerrain, int cap)
        {
            var result = new List<string>();
            if (ctx.Library == null || cap <= 0) { return result; }

            var groups = ctx.Neighbours.Where(n => n.Exists && n.TerrainId != null)
                .GroupBy(n => n.TerrainId).OrderBy(g => g.Min(n => (int)n.Dir)).ToList();
            var candidates = new List<Candidate>();
            foreach (var g in groups)
            {
                var phrases = ctx.Library.Phrases(roomTerrain, g.Key)
                    .Where(p => InMemoryLibrary.Uses(p.Terrains, roomTerrain) && p.NeighborTerrain == g.Key
                        && p.Text != null && p.Text.Contains("{dir}")).ToList();
                var pick = Pick(ctx.Rng, phrases);
                if (pick == null) { continue; }
                var dirs = g.Select(n => n.Dir).OrderBy(d => (int)d).ToList();
                if (pick.Merge)
                {
                    candidates.Add(new Candidate { Sides = dirs.Count, First = dirs[0], Terrain = g.Key, Text = pick.Text.Replace("{dir}", JoinDirs(dirs)) });
                }
                else
                {
                    foreach (var d in dirs)
                    {
                        candidates.Add(new Candidate { Sides = 1, First = d, Terrain = g.Key, Text = pick.Text.Replace("{dir}", Name(d)) });
                    }
                }
            }
            return candidates.OrderByDescending(c => c.Sides).ThenBy(c => (int)c.First)
                .ThenBy(c => c.Terrain, StringComparer.Ordinal).Take(cap).Select(c => c.Text).ToList();
        }

        private static NeighborPhrase Pick(SeededRandom rng, List<NeighborPhrase> phrases)
        {
            long total = phrases.Sum(p => (long)Math.Max(0, p.Weight));
            if (total <= 0) { return null; }
            long roll = (long)(rng.NextUInt64() % (ulong)total);
            foreach (var p in phrases)
            {
                var w = Math.Max(0, p.Weight);
                if (roll < w) { return p; }
                roll -= w;
            }
            return null;
        }
    }
}
