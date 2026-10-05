using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

/// <summary>Run-wide counter of how many times each glyph name has been used as a combination
/// component - drives the "regenerate least-used dependency first" tie-break on placement
/// failure.</summary>
public sealed class GlyphUsageTracker
{
    private readonly Dictionary<string, int> _counts = new();

    public int GetUsageCount(string glyphName) =>
        _counts.TryGetValue(glyphName, out var c) ? c : 0;

    public void RecordUsage(string glyphName) =>
        _counts[glyphName] = GetUsageCount(glyphName) + 1;
}

/// <summary>
/// Builds composite glyphs via overlay composition: translates (and, if configured,
/// scales/rotates) already-generated component glyphs onto one grid. On a placement failure,
/// regenerates the least-used dependency first (via GlyphUsageTracker), escalating through the
/// recipe's remaining components up to CombinationConfig.MaxRetriesPerRecipe before giving up.
///
/// generated is the run's shared, mutable glyph registry (not just this recipe's own
/// components) - when a dependency is regenerated, this resolver writes the new version back
/// into it directly, so the standalone glyph of that name AND every other already-built recipe
/// referencing it stay in sync rather than silently diverging. Any other already-generated
/// recipe that also references the regenerated name is rebuilt too, via the same
/// regenerateComponent callback (which handles composite names fine, recursively).
///
/// regenerateComponent: (componentName, attemptIndex) -> a freshly generated Glyph for that
/// name. The caller (glyph-generation layer) is responsible for both the actual regeneration
/// logic and for guarding against circular dependencies introduced by this cascading rebuild
/// (e.g. two composites that transitively depend on each other's components).
/// </summary>
public interface ICombinationResolver
{
    /// <summary>
    /// Builds a composite glyph by placing each component of a recipe (translate/scale/rotate
    /// per config) without crossing the others. spacing is the language's SubSymbolSpacing -
    /// each placed component is treated as one shape under it. Returns null if the recipe
    /// can't be satisfied within config.MaxRetriesPerRecipe attempts.
    /// </summary>
    Glyph? TryCombine(
        string compositeName,
        IReadOnlyList<string> componentNames,
        Dictionary<string, Glyph> generated,
        CombinationConfig config,
        LatticeGrid grid,
        DeterministicRandom rng,
        GlyphUsageTracker usageTracker,
        Func<string, int, Glyph> regenerateComponent,
        SubSymbolSpacingConfig spacing);
}
