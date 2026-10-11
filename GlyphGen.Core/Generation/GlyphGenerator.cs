using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Thrown when a glyph cannot be generated within its configured constraints/attempt budgets -
/// a shape could not be placed, or a composite recipe could not be satisfied.
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
            $"{SpacingRejections} broke an arrangement.spacing rule, {Collisions} left the grid or crossed existing strokes.";

        string hint =
            ChainFailures >= SpacingRejections && ChainFailures >= Collisions && ChainFailures > 0
                ? "The chain keeps running out of moves: try a shorter chain.length, more or longer chain.connectionRules, chain.allowNodeRevisit, or a larger grid."
            : SpacingRejections >= Collisions && SpacingRejections > 0
                ? "Spacing is rejecting most candidates: relax arrangement.spacing (minCenterDistance, minNodeDistance), chain.selfSpacing, or raise placement.maxPlacementAttempts."
            : Collisions > 0
                ? "Strokes are colliding or leaving the grid: set arrangement.allowCrossing, use a larger grid, a lower count, or raise placement.maxPlacementAttempts."
            : "";

        return hint.Length == 0 ? summary : summary + " " + hint;
    }
}

/// <summary>
/// Default IGlyphGenerator. For a composite glyph (its name is a key in the language's
/// Combination.Recipes), generation is delegated to ICombinationResolver. Otherwise every entry
/// of the language's Shapes list is placed in order, in ONE loop: guaranteed shapes (no count)
/// and free-form strokes (a rolled count) differ only in how many instances they produce. Each
/// instance is validated against a shared GeometryContext using its own placement, chain rules
/// and arrangement.
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
        int variation = 0,
        ulong languageSeed = 0)
        => GenerateGlyphCore(language, glyphDef, grid, glyphSeed, generated, usageTracker, new HashSet<string>(), variation, languageSeed);

    private Glyph GenerateGlyphCore(
        LanguageDefinition language,
        GlyphDefinition glyphDef,
        LatticeGrid grid,
        ulong glyphSeed,
        Dictionary<string, Glyph> generated,
        GlyphUsageTracker usageTracker,
        HashSet<string> inProgress,
        int variation,
        ulong languageSeed = 0)
    {
        if (language.Combination.Recipes.TryGetValue(glyphDef.Name, out var recipe))
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
                    language, glyphDef, grid, glyphSeed, generated, usageTracker, recipe, inProgress, variation, languageSeed);
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

        var geometry = new GeometryContext
        {
            GridWidth = grid.Width,
            GridHeight = grid.Height,
        };

        // Usage bias: glyphs that are components of many recipes roll lower counts/lengths.
        // Only shapes with a count are affected; guaranteed shapes have their own fixed rules.
        double usageDraws = language.UsageSizeBias.ExtraDraws(UsageCount(language, glyphDef.Name));
        double countExtraDraws = language.UsageSizeBias.AffectSubSymbolCount ? usageDraws : 0.0;
        double lengthExtraDraws = language.UsageSizeBias.AffectSubSymbolLength ? usageDraws : 0.0;

        foreach (var shape in language.Shapes)
        {
            // No count = exactly one instance and NO roll (a roll would consume a draw from the
            // glyph's random stream and shift everything placed after it).
            int count = shape.Count is null ? 1 : ResolveLowBiased(shape.Count, rng, countExtraDraws);

            for (int i = 0; i < count; i++)
            {
                var diagnostics = new PlacementDiagnostics();
                double lengthExtra = shape.Count is null ? 0.0 : lengthExtraDraws;
                var placed = GenerateShape(language, shape, grid, rng, geometry, i, lengthExtra, diagnostics)
                    ?? throw new GlyphGenerationException(
                        $"[{language.Name}] Shape '{shape.Id}'" +
                        (shape.Count is null ? "" : $" (instance {i + 1}/{count})") +
                        $" for glyph '{glyphDef.Name}' could not be placed within its configured " +
                        $"attempt budget. {diagnostics.Describe()}");

                geometry.Commit(placed, shape.Arrangement.AllowCrossing);
                glyph.Chains.Add(placed);
            }
        }

        return glyph;
    }

    /// <summary>Places one instance of a shape: asks the placement resolver for a legal placement
    /// and, when the shape's arrangement has an active bias, for up to Candidates of them, keeping
    /// the one whose glyph bounding box scores best. Without a bias this is exactly one resolver
    /// call, so the result is the first legal placement.</summary>
    private SubSymbolChain? GenerateShape(
        LanguageDefinition language,
        ShapeDefinition shape,
        LatticeGrid grid,
        DeterministicRandom glyphRng,
        GeometryContext geometry,
        int instanceIndex,
        double lengthExtraDraws,
        PlacementDiagnostics diagnostics)
    {
        var rng = glyphRng.Derive(shape.StreamLabel(instanceIndex));
        var rotationOptions = RotationOptionsFor(language, shape.Placement);
        bool shapeVariesPerAttempt = shape.Kind == ShapeKind.Generative;

        geometry.AllowCrossing = shape.Arrangement.AllowCrossing;

        SubSymbolChain? ShapeFactory(GridPoint origin, int rotation, DeterministicRandom attemptRng)
        {
            SubSymbolChain? chain = shape.Kind switch
            {
                ShapeKind.Literal => BuildLiteralChain(shape, origin, rotation),
                ShapeKind.Generative => GenerateShapeChain(shape, grid, origin, attemptRng, geometry, lengthExtraDraws),
                _ => throw new NotSupportedException($"Unknown shape kind: {shape.Kind}"),
            };

            if (chain is null) { diagnostics.ChainFailures++; return null; }

            if (!SubSymbolSpacingChecker.SatisfiesSpacing(chain.Nodes, geometry.NodeGroups, shape.Arrangement.Spacing))
            {
                diagnostics.SpacingRejections++;
                return null;
            }

            return chain;
        }

        var bias = shape.Arrangement.Bias;

        // A side pull (placement.side): the sides are listed, or drawn here for this instance from a derived
        // stream (which leaves every other sequence untouched). It ranks the candidates like a bias does.
        List<GlyphSide>? sides = null;
        double sideWeight = 1.0;
        if (shape.Placement.Side is { } pull)
        {
            var chosen = SideScoring.Choose(pull.Mode == SidePullMode.SetRandom, pull.Sides, pull.Pick, rng.Derive("side"));
            if (chosen.Count > 0) { sides = chosen; sideWeight = pull.Weight; }
        }

        bool ranked = bias.IsActive || sides is not null;
        int wanted = ranked ? Math.Max(1, shape.Arrangement.Candidates) : 1;

        var candidates = new List<SubSymbolChain>();
        var seen = new HashSet<string>();
        int repeats = 0;
        for (int k = 0; k < wanted; k++)
        {
            // The first call uses the shape's own stream, so with no bias (or Candidates 1) the
            // outcome is the plain first legal placement; extra candidates fork their own streams.
            var candidateRng = k == 0 ? rng : rng.Derive($"candidate-{k}");
            var result = _placementResolver.TryResolve(
                shape.Placement, grid, candidateRng, geometry, shapeVariesPerAttempt, rotationOptions, ShapeFactory);
            diagnostics.Attempts += result.AttemptsUsed;

            if (!result.Success || result.PlacedShape is null) break;

            // A repeat of an earlier candidate is skipped (a small grid repeats by chance); several in a row
            // mean nothing varies (a fixed literal shape), so asking for more would only give copies.
            if (!seen.Add(Signature(result.PlacedShape)))
            {
                if (++repeats >= 4) break;
                continue;
            }

            repeats = 0;
            candidates.Add(result.PlacedShape);
        }

        if (candidates.Count == 0) return null;
        if (candidates.Count == 1 || !ranked) return candidates[0];

        double Score(SubSymbolChain candidate)
        {
            double score = bias.IsActive
                ? BiasScoring.ScoreWithCandidate(bias, geometry.NodeGroups, candidate.Nodes, grid)
                : 0.0;

            if (sides is not null)
            {
                int minX = candidate.Nodes.Min(n => n.X), maxX = candidate.Nodes.Max(n => n.X);
                int minY = candidate.Nodes.Min(n => n.Y), maxY = candidate.Nodes.Max(n => n.Y);
                score += SideScoring.Score(sides, sideWeight, (minX + maxX) / 2.0, (minY + maxY) / 2.0, grid);
            }

            return score;
        }

        // Ranking only picks among shapes that already passed every hard rule. Strict < keeps the
        // earliest of equal scores, so ties resolve to the first-found candidate.
        SubSymbolChain best = candidates[0];
        double bestScore = Score(best);
        for (int i = 1; i < candidates.Count; i++)
        {
            double score = Score(candidates[i]);
            if (score < bestScore) { best = candidates[i]; bestScore = score; }
        }
        return best;
    }

    private static string Signature(SubSymbolChain chain) =>
        string.Join(";", chain.Nodes.Select(n => $"{n.X},{n.Y}")) + "|" + chain.IsClosedLoop;

    private SubSymbolChain? GenerateShapeChain(
        ShapeDefinition shape,
        LatticeGrid grid,
        GridPoint origin,
        DeterministicRandom rng,
        GeometryContext geometry,
        double lengthExtraDraws)
    {
        var chainRules = shape.Chain;
        var request = new ChainGenerationRequest
        {
            StartNode = origin,
            TargetLength = ResolveLowBiased(chainRules.Length, rng, lengthExtraDraws),
            ConnectionRules = chainRules.ConnectionRules,
            AllowNodeRevisit = chainRules.AllowNodeRevisit,
            AllowEdgeRevisit = chainRules.AllowEdgeRevisit,
            AllowClosedLoop = chainRules.AllowClosedLoop,
            ConnectionWeighting = chainRules.ConnectionWeighting,
            SelfSpacingRules = chainRules.SelfSpacing,
        };

        var chain = _chainGenerator.TryGenerateChain(grid, request, rng, geometry);
        if (chain is not null && shape.Count is null) chain.MotifId = shape.Id;
        return chain;
    }

    private static SubSymbolChain BuildLiteralChain(ShapeDefinition shape, GridPoint origin, int rotationDegrees)
    {
        var chain = new SubSymbolChain { MotifId = shape.Count is null ? shape.Id : null };
        var zero = new GridPoint(0, 0);

        var absolutePoints = shape.Nodes!
            .Select(n => new GridPoint(n[0], n[1]))
            .Select(p => p.RotateAround(zero, rotationDegrees))
            .Select(p => p.Translate(origin.X, origin.Y))
            .ToList();

        chain.Nodes.AddRange(absolutePoints);
        for (int i = 0; i < absolutePoints.Count - 1; i++)
            chain.Edges.Add(new GridEdge(absolutePoints[i], absolutePoints[i + 1]));

        return chain;
    }

    private static int UsageCount(LanguageDefinition language, string glyphName) =>
        language.Combination.Recipes.Values.Sum(r => r.Components.Count(name => name == glyphName));

    /// <summary>Rolls a range; with extraDraws > 0 rolls it that many more times (a fraction is
    /// honoured probabilistically) and keeps the lowest. See UsageSizeBiasConfig.</summary>
    private static int ResolveLowBiased(IntRange range, DeterministicRandom rng, double extraDraws)
    {
        int result = range.Resolve(rng);
        if (extraDraws <= 0) return result;

        int whole = (int)Math.Floor(extraDraws);
        double fraction = extraDraws - whole;
        if (fraction > 0 && rng.NextDouble() < fraction) whole++;

        for (int i = 0; i < whole; i++)
            result = Math.Min(result, range.Resolve(rng));

        return result;
    }

    /// <summary>A placement's own rotations list wins; otherwise the combination's rotation
    /// setting applies when it is on (the original coupling).</summary>
    private static RotationConfig? RotationOptionsFor(LanguageDefinition language, PlacementConfig placement)
    {
        if (placement.Rotations is not null)
            return placement.Rotations.Count == 0 ? null : new RotationConfig { Allowed = placement.Rotations };

        return language.Combination.AllowRotate ? language.Combination.Rotation : null;
    }

    private Glyph GenerateComposite(
        LanguageDefinition language,
        GlyphDefinition glyphDef,
        LatticeGrid grid,
        ulong glyphSeed,
        Dictionary<string, Glyph> generated,
        GlyphUsageTracker usageTracker,
        RecipeConfig recipe,
        HashSet<string> inProgress,
        int variation,
        ulong languageSeed = 0)
    {
        var rng = new DeterministicRandom(glyphSeed);

        Glyph RegenerateComponent(string name, int attemptIndex)
        {
            var componentDef = language.Glyphs.FirstOrDefault(g => g.Name == name)
                ?? new GlyphDefinition { Name = name };
            ulong seed = SplitMix64.DeriveSeed(glyphSeed, $"regen-{name}-{attemptIndex}");
            return GenerateGlyphCore(language, componentDef, grid, seed, generated, usageTracker, inProgress, variation: 0, languageSeed: languageSeed);
        }

        var composite = _combinationResolver.TryCombine(
            glyphDef.Name,
            recipe.Components,
            generated,
            language.Combination,
            grid,
            rng,
            usageTracker,
            RegenerateComponent,
            recipe.EffectiveArrangement ?? language.Combination.Arrangement,
            variation,
            languageSeed);

        if (composite is null)
        {
            var used = recipe.EffectiveArrangement ?? language.Combination.Arrangement;
            bool hasFixed = used.Placements.Any(p => p.Mode == ComponentPlacementMode.Fixed && recipe.Components.Contains(p.Glyph));

            throw new GlyphGenerationException(
                $"[{language.Name}] Could not satisfy combination recipe for '{glyphDef.Name}' " +
                $"after {language.Combination.MaxRetriesPerRecipe} attempts." +
                (used.Placements.Any(p => p.Mode != ComponentPlacementMode.Fixed && recipe.Components.Contains(p.Glyph))
                    ? " Side rules are hard: each ruled component's middle must be on its side of the others' middles, " +
                      "so conflicting rules (or too little room on a small grid) make a recipe unbuildable."
                    : "") +
                (hasFixed
                    ? " A component with a Fixed placement has no alternatives: check that its fixed position stays on the grid " +
                      "and leaves room under the crossing and spacing rules, and list fixed components first in the recipe."
                    : ""));
        }

        composite.LanguageName = language.Name;
        composite.GridWidth = grid.Width;
        composite.GridHeight = grid.Height;
        return composite;
    }
}
