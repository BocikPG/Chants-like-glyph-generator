using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Default ILanguageGenerator. Ordering: base glyphs first (components that exist only for
/// recipes), then every glyph that is NOT a combination-recipe key, then every glyph that IS
/// one (single-level recipes only; nested composites would need a real topological sort).
/// The returned list is BaseGlyphs followed by Glyphs, in their configured order.
///
/// Base glyphs go through exactly the same quality gate as every other glyph: each is compared
/// against everything accepted before it, and everything after is compared against it.
///
/// Every glyph EXCEPT composites is wrapped in a quality gate (see QualityConfig): it's compared
/// against every glyph already accepted earlier in this language, and regenerated with a fresh
/// seed if it's too similar to one of them or is just a single straight line. Composites are
/// exempt: they exist only as combinations of other, already-gated glyphs and contain their
/// components by construction, so judging them for similarity is meaningless - each is
/// generated once and accepted as is. Doing this per glyph, in
/// generation order, means all standalone glyphs are finalized BEFORE any composite is built -
/// so a standalone glyph never changes after a composite has already used it (which would
/// leave that composite stale).
/// </summary>
public sealed class LanguageGenerator : ILanguageGenerator
{
    private readonly IGlyphGenerator _glyphGenerator;

    public LanguageGenerator(IGlyphGenerator glyphGenerator)
    {
        _glyphGenerator = glyphGenerator;
    }

    public IReadOnlyList<Glyph> GenerateAll(LanguageDefinition language, LatticeGrid grid, ulong languageSeed)
    {
        var generated = new Dictionary<string, Glyph>();
        var accepted = new List<Glyph>();
        var usageTracker = new GlyphUsageTracker();
        var composites = new HashSet<string>(language.Combination.Recipes.Keys);

        foreach (var glyphDef in language.BaseGlyphs)
        {
            GenerateInto(language, grid, languageSeed, glyphDef, generated, accepted, usageTracker);
        }

        foreach (var glyphDef in language.Glyphs.Where(g => !composites.Contains(g.Name)))
        {
            GenerateInto(language, grid, languageSeed, glyphDef, generated, accepted, usageTracker);
        }

        foreach (var glyphDef in language.Glyphs.Where(g => composites.Contains(g.Name)))
        {
            GenerateInto(language, grid, languageSeed, glyphDef, generated, accepted, usageTracker);
        }

        return language.BaseGlyphs.Concat(language.Glyphs).Select(g => generated[g.Name]).ToList();
    }

    private void GenerateInto(
        LanguageDefinition language,
        LatticeGrid grid,
        ulong languageSeed,
        GlyphDefinition glyphDef,
        Dictionary<string, Glyph> generated,
        List<Glyph> accepted,
        GlyphUsageTracker usageTracker)
    {
        ulong baseSeed = SeedTree.ForGlyph(languageSeed, glyphDef.Name);

        if (language.Combination.Recipes.ContainsKey(glyphDef.Name))
        {
            generated[glyphDef.Name] =
                _glyphGenerator.GenerateGlyph(language, glyphDef, grid, baseSeed, generated, usageTracker);
            return;
        }

        var quality = language.Quality;
        int maxAttempts = Math.Max(1, quality.MaxRegenerationAttempts);

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            // Attempt 0 uses the glyph's normal seed; later attempts derive a fresh, still
            // fully deterministic seed from it, so a quality retry is reproducible too.
            ulong seed = attempt == 0
                ? baseSeed
                : SplitMix64.DeriveSeed(baseSeed, $"quality-retry-{attempt}");

            var candidate = _glyphGenerator.GenerateGlyph(language, glyphDef, grid, seed, generated, usageTracker);

            bool trivialLine = quality.RejectStraightLineOnly && GlyphQualityChecker.IsTrivialStraightLine(candidate);
            bool tooSimilar = !trivialLine
                && GlyphQualityChecker.IsTooSimilar(candidate, accepted, quality.MaxSimilarity);

            if (trivialLine || tooSimilar) continue;

            accepted.Add(candidate);
            generated[glyphDef.Name] = candidate;
            return;
        }

        throw new GlyphGenerationException(
            $"[{language.Name}] Could not generate a sufficiently distinct, non-trivial glyph for " +
            $"'{glyphDef.Name}' after {maxAttempts} attempts " +
            $"(max similarity {quality.MaxSimilarity:0.##}, reject straight line: {quality.RejectStraightLineOnly}).");
    }
}
