using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Thrown when a glyph cannot be generated within its configured constraints/attempt budgets -
/// a guaranteed motif or a required sub-symbol could not be placed, a composite recipe could
/// not be satisfied, or a cascading regeneration (see ICombinationResolver) would recurse into
/// a composite that is already being resolved further up the call stack.
/// </summary>
public sealed class GlyphGenerationException : Exception
{
    public GlyphGenerationException(string message) : base(message) { }
}

/// <summary>
/// Why the placement attempts for ONE shape failed, so the error can say what to change. Only the
/// shape factory can tell "the chain couldn't be built" and "a spacing rule rejected it" apart;
/// everything else that failed an attempt (left the grid, crossed existing strokes) is what's
/// left over, since every attempt of a failed placement failed for some reason.
/// </summary>
public sealed class PlacementDiagnostics
{
    public int Attempts { get; set; }
    public int ChainFailures { get; set; }
    public int SpacingRejections { get; set; }
    public int Collisions => Math.Max(0, Attempts - ChainFailures - SpacingRejections);

    public string Describe()
    {
        if (Attempts <= 0) return "";

        string summary =
            $"Of {Attempts} attempt(s): {ChainFailures} ran out of valid moves before reaching the target length, " +
            $"{SpacingRejections} broke a subSymbolSpacing rule, {Collisions} left the grid or crossed existing strokes.";

        string hint =
            ChainFailures >= SpacingRejections && ChainFailures >= Collisions && ChainFailures > 0
                ? "The chain keeps running out of moves: try a shorter subSymbolLength, more or longer connectionRules, allowNodeRevisit, or a larger grid."
            : SpacingRejections >= Collisions && SpacingRejections > 0
                ? "Spacing is rejecting most candidates: relax subSymbolSpacing (minCenterDistance, minNodeDistance, selfSpacing) or raise maxPlacementAttempts."
            : Collisions > 0
                ? "Strokes are colliding or leaving the grid: allow crossing, use a larger grid, fewer sub-symbols, or raise maxPlacementAttempts."
            : "";

        return hint.Length == 0 ? summary : summary + " " + hint;
    }
}

/// <summary>
/// Default IGlyphGenerator. For a composite glyph (its name is a key in the language's
/// Combination.Recipes), generation is delegated to ICombinationResolver. Otherwise: guaranteed
/// motifs generate first (most-constrained-first ordering), then free-form sub-symbols, all
/// validated against one shared GeometryContext.
///
/// generated is the run's shared, mutable glyph registry - passed straight through to
/// ICombinationResolver, which may both read from it (to find already-built components) and
/// write to it (when a dependency is regenerated and needs to propagate to every other recipe
/// using it). inProgress tracks which composite names are currently being resolved on this
/// call stack, so a cascading regeneration can never recurse into a cycle - two composites that
/// end up transitively depending on each other's components fail loudly instead of hanging.
/// </summary>
public sealed class GlyphGenerator : IGlyphGenerator
{
    private readonly IChainGenerator _chainGenerator;
    private readonly IPlacementResolver _placementResolver;
    private readonly ICombinationResolver _combinationResolver;

    public GlyphGenerator(
        IChainGenerator chainGenerator,
        IPlacementResolver placementResolver,
        ICombinationResolver combinationResolver)
    {
        _chainGenerator = chainGenerator;
        _placementResolver = placementResolver;
        _combinationResolver = combinationResolver;
    }

    public Glyph GenerateGlyph(
        LanguageDefinition language,
        GlyphDefinition glyphDef,
        LatticeGrid grid,
        ulong glyphSeed,
        Dictionary<string, Glyph> generated,
        GlyphUsageTracker usageTracker,
        int variation = 0)
        => GenerateGlyphCore(language, glyphDef, grid, glyphSeed, generated, usageTracker, new HashSet<string>(), variation);

    private Glyph GenerateGlyphCore(
        LanguageDefinition language,
        GlyphDefinition glyphDef,
        LatticeGrid grid,
        ulong glyphSeed,
        Dictionary<string, Glyph> generated,
        GlyphUsageTracker usageTracker,
        HashSet<string> inProgress,
        int variation)
    {
        if (language.Combination.Recipes.TryGetValue(glyphDef.Name, out var componentNames))
        {
            if (!inProgress.Add(glyphDef.Name))
            {
                throw new GlyphGenerationException(
                    $"[{language.Name}] Circular combination dependency detected involving " +
                    $"'{glyphDef.Name}' - two or more recipes transitively depend on each " +
                    "other's components, so regenerating one keeps forcing a rebuild of the other.");
            }

            try
            {
                return GenerateComposite(
                    language, glyphDef, grid, glyphSeed, generated, usageTracker, componentNames, inProgress, variation);
            }
            finally
            {
                inProgress.Remove(glyphDef.Name);
            }
        }

        var rng = new DeterministicRandom(glyphSeed);
        var glyph = new Glyph
        {
            Name = glyphDef.Name,
            LanguageName = language.Name,
            GridWidth = grid.Width,
            GridHeight = grid.Height,
        };
        var geometry = new GeometryContext(language.AllowSymbolsToCross);

        foreach (var motif in language.GuaranteedMotifs)
        {
            var diagnostics = new PlacementDiagnostics();
            var chain = GenerateMotif(language, motif, grid, rng, geometry, diagnostics)
                ?? throw new GlyphGenerationException(
                    $"[{language.Name}] Guaranteed motif '{motif.Id}' for glyph '{glyphDef.Name}' " +
                    $"could not be placed within its configured attempt budget. {diagnostics.Describe()}");

            geometry.Commit(chain);
            glyph.Chains.Add(chain);
        }

        // Usage bias (UsageSizeBiasConfig): glyphs used in more recipes roll lower values.
        // With the feature off, or the glyph unused, both are 0 and every Resolve below is the
        // single plain draw it always was.
        double usageDraws = language.UsageSizeBias.ExtraDraws(UsageCount(language, glyphDef.Name));
        double countDraws = language.UsageSizeBias.AffectSubSymbolCount ? usageDraws : 0.0;
        double lengthDraws = language.UsageSizeBias.AffectSubSymbolLength ? usageDraws : 0.0;

        int subSymbolCount = ResolveLowBiased(language.SubSymbolCount, rng, countDraws);
        for (int i = 0; i < subSymbolCount; i++)
        {
            var diagnostics = new PlacementDiagnostics();
            var chain = GenerateFreeFormSubSymbol(language, grid, rng, geometry, i, lengthDraws, diagnostics)
                ?? throw new GlyphGenerationException(
                    $"[{language.Name}] Sub-symbol {i + 1}/{subSymbolCount} for glyph " +
                    $"'{glyphDef.Name}' could not be placed within its configured attempt budget. {diagnostics.Describe()}");

            geometry.Commit(chain);
            glyph.Chains.Add(chain);
        }

        return glyph;
    }

    private Glyph GenerateComposite(
        LanguageDefinition language,
        GlyphDefinition glyphDef,
        LatticeGrid grid,
        ulong glyphSeed,
        Dictionary<string, Glyph> generated,
        GlyphUsageTracker usageTracker,
        List<string> componentNames,
        HashSet<string> inProgress,
        int variation)
    {
        var rng = new DeterministicRandom(glyphSeed);

        // Regenerating a dependency (or a sibling recipe being cascade-patched) means
        // re-running ITS OWN generation with a fresh seed scoped under THIS composite's seed,
        // rather than through the top-level SeedTree.ForGlyph chain a standalone lookup would
        // use. Reproducible relative to this composite, but a different stream from generating
        // that name fresh at the top level. inProgress is threaded through unchanged so nested
        // regenerations stay covered by the same cycle guard.
        Glyph RegenerateComponent(string name, int attemptIndex)
        {
            var componentDef = language.Glyphs.FirstOrDefault(g => g.Name == name)
                ?? new GlyphDefinition { Name = name };
            ulong seed = SplitMix64.DeriveSeed(glyphSeed, $"regen-{name}-{attemptIndex}");
            return GenerateGlyphCore(language, componentDef, grid, seed, generated, usageTracker, inProgress, variation: 0);
        }

        var composite = _combinationResolver.TryCombine(
            glyphDef.Name,
            componentNames,
            generated,
            language.Combination,
            grid,
            rng,
            usageTracker,
            RegenerateComponent,
            language.SubSymbolSpacing,
            variation);

        if (composite is null)
        {
            throw new GlyphGenerationException(
                $"[{language.Name}] Could not satisfy combination recipe for '{glyphDef.Name}' " +
                $"after {language.Combination.MaxRetriesPerRecipe} attempts.");
        }

        composite.LanguageName = language.Name;
        composite.GridWidth = grid.Width;
        composite.GridHeight = grid.Height;
        return composite;
    }

    private SubSymbolChain? GenerateMotif(
        LanguageDefinition language,
        GuaranteedMotifConfig motif,
        LatticeGrid grid,
        DeterministicRandom glyphRng,
        GeometryContext geometry,
        PlacementDiagnostics diagnostics)
    {
        var motifRng = glyphRng.Derive($"motif-{motif.Id}");
        bool shapeVaries = motif.Type == MotifType.Generative;

        SubSymbolChain? ShapeFactory(GridPoint origin, int rotation, DeterministicRandom attemptRng)
        {
            SubSymbolChain? chain = motif.Type switch
            {
                MotifType.Literal => BuildLiteralChain(motif.Literal!, origin, rotation, motif.Id),
                MotifType.Generative => GenerateMotifChain(
                    language, motif.Generative!, grid, origin, attemptRng, geometry, motif.Id),
                _ => throw new NotSupportedException($"Unknown motif type: {motif.Type}"),
            };

            if (chain is null) { diagnostics.ChainFailures++; return null; }

            // Checked after the shape is complete (a centroid needs every node), same as for
            // free-form sub-symbols below - a failure here is just another reason this attempt
            // didn't work, handled by the same retry loop as a collision would be.
            if (!SubSymbolSpacingChecker.SatisfiesSpacing(chain.Nodes, geometry.NodeGroups, language.SubSymbolSpacing))
            {
                diagnostics.SpacingRejections++;
                return null;
            }

            return chain;
        }

        var rotationOptions = RotationOptionsFor(language, motif.Placement);

        var result = _placementResolver.TryResolve(
            motif.Placement, grid, motifRng, geometry, shapeVaries, rotationOptions, ShapeFactory);
        diagnostics.Attempts = result.AttemptsUsed;

        return result.Success ? result.PlacedShape : null;
    }

    private SubSymbolChain? GenerateMotifChain(
        LanguageDefinition language,
        GenerativeMotifConfig generative,
        LatticeGrid grid,
        GridPoint origin,
        DeterministicRandom rng,
        GeometryContext geometry,
        string motifId)
    {
        var request = new ChainGenerationRequest
        {
            StartNode = origin,
            TargetLength = generative.LengthRange.Resolve(rng),
            ConnectionRules = generative.ConnectionRules,
            AllowNodeRevisit = language.AllowNodeRevisit,
            AllowEdgeRevisit = language.AllowEdgeRevisit,
            AllowClosedLoop = generative.AllowClosedLoop,
            ClosedLoopWeight = generative.ClosedLoopWeight,
            ConnectionWeighting = language.ConnectionWeighting,
            SelfSpacingRules = language.SubSymbolSpacing.ActiveSelfSpacing,
        };

        var chain = _chainGenerator.TryGenerateChain(grid, request, rng, geometry);
        if (chain is not null) chain.MotifId = motifId;
        return chain;
    }

    private static SubSymbolChain BuildLiteralChain(
        LiteralMotifConfig literal, GridPoint origin, int rotationDegrees, string motifId)
    {
        var chain = new SubSymbolChain { MotifId = motifId };
        var zero = new GridPoint(0, 0);

        var absolutePoints = literal.Nodes
            .Select(relative => new GridPoint(relative[0], relative[1]))
            .Select(p => p.RotateAround(zero, rotationDegrees))
            .Select(p => p.Translate(origin.X, origin.Y))
            .ToList();

        chain.Nodes.AddRange(absolutePoints);
        for (int i = 0; i < absolutePoints.Count - 1; i++)
            chain.Edges.Add(new GridEdge(absolutePoints[i], absolutePoints[i + 1]));

        return chain;
    }

    /// <summary>How many times a glyph's name appears across all combination recipes.</summary>
    private static int UsageCount(LanguageDefinition language, string glyphName) =>
        language.Combination.Recipes.Values.Sum(components => components.Count(name => name == glyphName));

    /// <summary>Resolves a range normally, then (extraDraws > 0) re-rolls it that many more times
    /// and keeps the lowest - same range, same rules, just skewed low. A fractional part of
    /// extraDraws is applied as a probability.</summary>
    private static int ResolveLowBiased(IntRange range, DeterministicRandom rng, double extraDraws)
    {
        int best = range.Resolve(rng);
        if (extraDraws <= 0) return best;

        int whole = (int)Math.Floor(extraDraws);
        double fraction = extraDraws - whole;
        if (fraction > 0 && rng.NextDouble() < fraction) whole++;

        for (int i = 0; i < whole; i++)
            best = Math.Min(best, range.Resolve(rng));

        return best;
    }

    private SubSymbolChain? GenerateFreeFormSubSymbol(
        LanguageDefinition language, LatticeGrid grid, DeterministicRandom glyphRng, GeometryContext geometry,
        int subSymbolIndex, double lengthExtraDraws, PlacementDiagnostics diagnostics)
    {
        // The index is essential here, not cosmetic: DeterministicRandom.Derive is a pure
        // function of the parent's CURRENT state plus this label, and nothing else draws from
        // glyphRng between loop iterations - a fixed label would derive the exact same seed
        // for every sub-symbol in the glyph, producing identical placement and shape for all
        // of them whenever subSymbolCount > 1.
        var placementRng = glyphRng.Derive($"subsymbol-{subSymbolIndex}-placement");
        var rotationOptions = RotationOptionsFor(language, language.SubSymbolPlacement);

        SubSymbolChain? ShapeFactory(GridPoint origin, int rotation, DeterministicRandom attemptRng)
        {
            var request = new ChainGenerationRequest
            {
                StartNode = origin,
                TargetLength = ResolveLowBiased(language.SubSymbolLength, attemptRng, lengthExtraDraws),
                ConnectionRules = language.ConnectionRules,
                AllowNodeRevisit = language.AllowNodeRevisit,
                AllowEdgeRevisit = language.AllowEdgeRevisit,
                AllowClosedLoop = language.AllowClosedLoop,
                ClosedLoopWeight = language.ClosedLoopWeight,
                ConnectionWeighting = language.ConnectionWeighting,
                SelfSpacingRules = language.SubSymbolSpacing.ActiveSelfSpacing,
            };

            var chain = _chainGenerator.TryGenerateChain(grid, request, attemptRng, geometry);
            if (chain is null) { diagnostics.ChainFailures++; return null; }

            // Checked once the chain is complete (a centroid needs every node) - a failure here
            // is just another reason this attempt didn't work, same retry path as a collision.
            if (!SubSymbolSpacingChecker.SatisfiesSpacing(chain.Nodes, geometry.NodeGroups, language.SubSymbolSpacing))
            {
                diagnostics.SpacingRejections++;
                return null;
            }

            return chain;
        }

        var result = _placementResolver.TryResolve(
            language.SubSymbolPlacement, grid, placementRng, geometry,
            shapeVariesPerAttempt: true, rotationOptions, ShapeFactory);
        diagnostics.Attempts = result.AttemptsUsed;

        return result.Success ? result.PlacedShape : null;
    }

    /// <summary>
    /// The rotations a placement may use. A placement's own Rotations list wins (empty = never
    /// rotate); without one it inherits Combination.Rotation, as before, when Combination.AllowRotate
    /// is on. Only Literal motifs actually use the result - a generated chain is already random, so
    /// it ignores the rotation it is handed.
    /// </summary>
    private static RotationConfig? RotationOptionsFor(LanguageDefinition language, PlacementConfig placement)
    {
        if (placement.Rotations is not null)
            return placement.Rotations.Count == 0 ? null : new RotationConfig { Allowed = placement.Rotations };

        return language.Combination.AllowRotate ? language.Combination.Rotation : null;
    }
}
