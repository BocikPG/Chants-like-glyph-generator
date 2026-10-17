using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

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
/// A Fixed placement rule with ifBlocked Translate keeps a component's position in the composite equal to its position in
/// its own glyph: when the fixed position is blocked, the component is moved to a free position AND the original glyph is
/// moved with it (generated is updated), and every other already-built composite using that glyph is rebuilt through
/// rebuildComposite (a single attempt, no regeneration, no moving of originals - isRebuild). If any of them cannot be
/// rebuilt, the next position is tried; if none works, the attempt fails and the usual regeneration follows.
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
    /// per config) without crossing the others. arrangement governs how each placed component
    /// relates to the others (crossing, spacing, bias). Returns null if the recipe
    /// can't be satisfied within config.MaxRetriesPerRecipe attempts. variation 0 is the normal
    /// search, which prefers the plain (0,0) overlay; a higher value (a quality retry) drops that
    /// preference so the same components come out arranged differently.
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
        CombinationArrangementRules arrangement,
        int variation = 0,
        ulong languageSeed = 0,
        Action<string>? explainFailure = null,
        Func<string, Glyph?>? rebuildComposite = null,
        bool isRebuild = false);
}
