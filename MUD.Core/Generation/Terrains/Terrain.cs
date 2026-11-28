using System;
using System.Collections.Generic;
using System.Linq;

namespace MUD.Core.Generation.Terrains
{
    // Generic terrain generator. Works from the map-file template (short/long pools) exactly as the
    // generator always has; the library, when supplied, adds fragments (only where the template has no
    // text), features and neighbour phrases. Draw order is fixed: template short, template long pools,
    // then library draws (so adding library content never changes template-driven text).
    public class Terrain : ITerrainGenerator
    {
        public const string ShortSlot = "short";

        private readonly string _terrainId;

        public Terrain(string terrainId) { _terrainId = terrainId; }

        // The plain legend template name (the map file's own template data is keyed by it).
        public virtual string TerrainId { get { return _terrainId; } }

        // The terrain type id the library is asked with: "<map prefix>.<legend template name>" (a map's prefix is its area).
        protected string LibraryTerrainId(GenContext ctx)
        {
            return TerrainTypeRules.IdFor(ctx.Prefix, TerrainId);
        }
        public virtual IReadOnlyList<string> Slots { get { return new[] { "ground" }; } }
        public virtual int MaxNeighborSentences { get { return 2; } }
        public virtual int MaxFeatures { get { return 2; } }

        public virtual GeneratedRoom Generate(GenContext ctx)
        {
            var t = ctx.Template;
            var rng = ctx.Rng;
            var lib = ctx.Library;
            var room = new GeneratedRoom();

            string shortText = t.Short.Count == 0 ? null : t.Short[rng.Next(t.Short.Count)];
            var picks = new List<string>();
            foreach (var pool in t.Long) { picks.Add(pool[rng.Next(pool.Count)]); }

            if (lib != null)
            {
                if (shortText == null) { shortText = PickFragment(ctx, ShortSlot); }
                if (t.Long.Count == 0)
                {
                    foreach (var slot in Slots)
                    {
                        var text = PickFragment(ctx, slot);
                        if (text != null) { picks.Add(text); }
                    }
                }
                var orientation = Orientation(ctx);
                if (orientation != null) { picks.Add(orientation); }
                picks.AddRange(NeighbourSentences(ctx));
                room.Features = PickFeatures(ctx);
            }

            room.Short = shortText;
            room.Long = picks.Count == 0 ? null : string.Join(" ", picks);
            room.Handles = new List<string>(t.Handles);
            room.Adjectives = new List<string>(t.Adjectives);
            room.Plurals = new List<string>(t.Plurals);
            return room;
        }

        // Hook for bespoke classes (e.g. river flow direction from adjacent water). Called after the
        // standard picks; may draw from ctx.Rng. Returns a sentence appended to the description, or null.
        protected virtual string Orientation(GenContext ctx) { return null; }

        protected string PickFragment(GenContext ctx, string slot, string[] requireTags = null, string[] excludeTags = null)
        {
            var eligible = Filter(ctx.Library.Fragments(LibraryTerrainId(ctx), slot), requireTags, excludeTags);
            var pick = WeightedPick(ctx.Rng, eligible, f => f.Weight);
            return pick == null ? null : pick.Text;
        }

        protected IReadOnlyList<DescriptionFragment> Filter(IReadOnlyList<DescriptionFragment> all, string[] requireTags, string[] excludeTags)
        {
            return all.Where(f => (requireTags == null || requireTags.All(r => f.Tags.Contains(r)))
                    && (excludeTags == null || !excludeTags.Any(e => f.Tags.Contains(e))))
                .ToList();
        }

        // Sum of weights; weight <= 0 excluded; null when nothing is eligible (and then no draw is made).
        protected T WeightedPick<T>(SeededRandom rng, IReadOnlyList<T> items, Func<T, int> weight) where T : class
        {
            long total = 0;
            foreach (var i in items) { total += Math.Max(0, weight(i)); }
            if (total <= 0) { return null; }
            long roll = (long)(rng.NextUInt64() % (ulong)total);
            foreach (var i in items)
            {
                var w = Math.Max(0, weight(i));
                if (roll < w) { return i; }
                roll -= w;
            }
            return null;
        }

        // One chance roll per eligible template (pass or fail, for stability), then weighted picks
        // without replacement among the passers up to MaxFeatures. Duplicate shorts in a room are skipped.
        protected virtual List<GeneratedFeature> PickFeatures(GenContext ctx)
        {
            var result = new List<GeneratedFeature>();
            var eligible = ctx.Library.Features(LibraryTerrainId(ctx)).ToList();
            var passed = new List<FeatureTemplate>();
            foreach (var f in eligible)
            {
                var roll = ctx.Rng.Next(1000000);
                if (roll < f.Chance * 1000000) { passed.Add(f); }
            }
            var shorts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            while (passed.Count > 0 && result.Count < MaxFeatures)
            {
                var pick = WeightedPick(ctx.Rng, passed, f => f.Weight);
                if (pick == null) { break; }
                passed.Remove(pick);
                if (!shorts.Add(pick.Short)) { continue; }
                result.Add(new GeneratedFeature
                {
                    Short = pick.Short,
                    Long = pick.Long,
                    RoomLine = pick.RoomLine,
                    Handles = new List<string>(pick.Handles),
                    Adjectives = new List<string>(pick.Adjectives),
                    TemplateId = pick.Id,
                    TemplateVersion = pick.Version,
                });
            }
            return result;
        }

        protected virtual List<string> NeighbourSentences(GenContext ctx)
        {
            return NeighbourPhrases.Build(ctx, TerrainId, MaxNeighborSentences);
        }
    }
}
