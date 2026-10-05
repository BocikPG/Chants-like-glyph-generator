using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Default ILanguageGenerator. Ordering: base glyphs first (components that exist only for
/// recipes), then every glyph that is NOT a combination-recipe key, then every glyph that IS
/// one (composites are generated in the order they are listed, so a composite built from
/// another composite must come after it - the config loader enforces this). The returned list
/// is BaseGlyphs followed by Glyphs, in their configured order.
///
/// Every plain and base glyph goes through the quality gate (see QualityConfig): it is compared
/// against every plain/base glyph accepted before it, and regenerated with a fresh seed if it is
/// too similar to one of them or is just a single straight line. Composites contain their
/// components by construction, so they are never compared with plain or base glyphs; they are
/// only compared with each other, and only when quality.compositeMaxSimilarity is below 1.
/// Doing this per glyph, in generation order, means all standalone glyphs are finalized BEFORE
/// any composite is built - so a standalone glyph never changes after a composite has already
/// used it (which would leave that composite stale).
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
        var acceptedComposites = new List<Glyph>();
        var usageTracker = new GlyphUsageTracker();
        var composites = new HashSet<string>(language.Combination.Recipes.Keys);

        foreach (var glyphDef in language.BaseGlyphs)
        {
            GenerateInto(language, grid, languageSeed, glyphDef, generated, accepted, acceptedComposites, usageTracker);
        }

        foreach (var glyphDef in language.Glyphs.Where(g => !composites.Contains(g.Name)))
        {
            GenerateInto(language, grid, languageSeed, glyphDef, generated, accepted, acceptedComposites, usageTracker);
        }

        foreach (var glyphDef in language.Glyphs.Where(g => composites.Contains(g.Name)))
        {
            GenerateInto(language, grid, languageSeed, glyphDef, generated, accepted, acceptedComposites, usageTracker);
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
        List<Glyph> acceptedComposites,
        GlyphUsageTracker usageTracker)
    {
        ulong baseSeed = SeedTree.ForGlyph(languageSeed, glyphDef.Name);
        var quality = language.Quality;

        // Plain/base glyphs: the full gate against every accepted plain/base glyph. Composites:
        // no straight-line check, and similarity only against other composites - which with the
        // default compositeMaxSimilarity of 1 means no checks at all, so the first build is
        // accepted exactly as before. (A rebuilt composite re-records its components' usage in
        // the tracker; that only influences which component is regenerated first on a later
        // placement failure.)
        bool isComposite = language.Combination.Recipes.ContainsKey(glyphDef.Name);
        var pool = isComposite ? acceptedComposites : accepted;
        double maxSimilarity = isComposite ? quality.CompositeMaxSimilarity : quality.MaxSimilarity;
        bool rejectStraightLine = !isComposite && quality.RejectStraightLineOnly;

        int maxAttempts = Math.Max(1, quality.MaxRegenerationAttempts);

        var rejections = new List<string>();

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            // Attempt 0 uses the glyph's normal seed; later attempts derive a fresh, still
            // fully deterministic seed from it, so a quality retry is reproducible too.
            ulong seed = attempt == 0
                ? baseSeed
                : SplitMix64.DeriveSeed(baseSeed, $"quality-retry-{attempt}");

            var candidate = _glyphGenerator.GenerateGlyph(language, glyphDef, grid, seed, generated, usageTracker, variation: attempt);

            bool trivialLine = rejectStraightLine && GlyphQualityChecker.IsTrivialStraightLine(candidate);
            var similar = trivialLine
                ? null
                : GlyphQualityChecker.FindTooSimilar(candidate, pool, maxSimilarity);

            if (trivialLine || similar is not null)
            {
                rejections.Add(trivialLine
                    ? "it is a single straight line"
                    : $"too similar to '{similar!.Value.Other.Name}' ({similar.Value.Similarity:0.00})");
                continue;
            }

            pool.Add(candidate);
            generated[glyphDef.Name] = candidate;
            return;
        }

        // Say WHY each attempt was rejected (capped, so a huge attempt budget doesn't flood the console).
        const int shown = 8;
        string reasons = string.Join("; ", rejections.Take(shown).Select((r, i) => $"attempt {i + 1}: {r}"))
            + (rejections.Count > shown ? $"; ... and {rejections.Count - shown} more" : "");

        bool mostlySimilar = rejections.Count(r => r.StartsWith("too similar")) * 2 >= rejections.Count;
        string hint = isComposite
            ? "Try raising quality.compositeMaxSimilarity, or giving the recipes different components or more placement freedom (scale/rotation, a larger grid)."
            : mostlySimilar
                ? "Try raising quality.maxSimilarity, widening subSymbolCount/subSymbolLength, or using a larger grid."
                : "Try raising subSymbolLength/subSymbolCount, or set quality.rejectStraightLineOnly to false.";

        string what = isComposite ? "a composite sufficiently distinct from the other composites" : "a sufficiently distinct, non-trivial glyph";

        throw new GlyphGenerationException(
            $"[{language.Name}] Could not generate {what} for " +
            $"'{glyphDef.Name}' after {maxAttempts} attempts " +
            $"(max similarity {maxSimilarity:0.##}, reject straight line: {rejectStraightLine}). " +
            $"Rejected because: {reasons}. {hint}");
    }
}
