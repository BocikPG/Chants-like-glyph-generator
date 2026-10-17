using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Thrown when a glyph cannot be generated within its configured constraints/attempt budgets -
/// a shape could not be placed, or a composite recipe could not be satisfied.
/// </summary>
public class GlyphGenerationException : Exception
{
    public GlyphGenerationException(string message) : base(message) { }
}

/// <summary>A generative or literal shape could not be placed (placement.redoShape / redrawGlyph decide whether it is retried).</summary>
public sealed class ShapePlacementException : GlyphGenerationException
{
    public ShapeDefinition Shape { get; }
    public ShapePlacementException(ShapeDefinition shape, string message) : base(message) { Shape = shape; }
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
                ? "Strokes are colliding or leaving the grid: set arrangement.crossing.mode, use a larger grid, a lower count, or raise placement.maxPlacementAttempts."
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
public sealed partial class GlyphGenerator : IGlyphGenerator
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
        if (language.Derivations.TryGetValue(glyphDef.Name, out var derivation))
        {
            if (!inProgress.Add(glyphDef.Name))
            {
                throw new GlyphGenerationException(
                    $"[{language.Name}] Circular derivation involving '{glyphDef.Name}'.");
            }

            try
            {
                return GenerateDerived(language, glyphDef, derivation, grid, glyphSeed, generated);
            }
            finally
            {
                inProgress.Remove(glyphDef.Name);
            }
        }

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

        // A copy shape that cannot be placed first redraws its base shape (copyOf.regenerateBase times), then the whole
        // glyph from a fresh seed (copyOf.redrawGlyph times). Nothing is retried when no copy fails, so such glyphs
        // come out exactly as they always did.
        int maxRedraws = language.Shapes.Select(x => Math.Max(x.CopyOf?.RedrawGlyph ?? 0, x.Placement.RedrawGlyph)).DefaultIfEmpty(0).Max();
        int shapeRedos = 0;
        int baseRedraws = 0, glyphRedraws = 0;
        GlyphGenerationException? last = null;

        for (int redraw = 0; redraw <= maxRedraws; redraw++)
        {
            ulong seed = redraw == 0 ? glyphSeed : SplitMix64.DeriveSeed(glyphSeed, $"redraw-{redraw}");
            var salts = new Dictionary<string, int>(StringComparer.Ordinal);
            if (redraw > 0) glyphRedraws++;

            while (true)
            {
                try
                {
                    return BuildPlainGlyph(language, glyphDef, grid, seed, salts);
                }
                catch (ShapePlacementException ex)
                {
                    last = ex;
                    int used = salts.GetValueOrDefault(ex.Shape.Id);
                    if (used < ex.Shape.Placement.RedoShape) { salts[ex.Shape.Id] = used + 1; shapeRedos++; continue; }
                    if (redraw >= ex.Shape.Placement.RedrawGlyph) // nothing left to try for this shape
                        throw new GlyphGenerationException(ex.Message + Retried(shapeRedos, glyphRedraws, ex.Shape));
                    break; // redo spent: redraw the whole glyph
                }
                catch (CopyPlacementException ex)
                {
                    last = ex;
                    string root = RootShapeId(language, ex.Shape);
                    int used = salts.GetValueOrDefault(root);
                    if (used >= ex.Shape.CopyOf!.RegenerateBase) break; // base redraws spent: redraw the whole glyph
                    salts[root] = used + 1;
                    baseRedraws++;
                }
            }
        }

        if (last is ShapePlacementException lastShape)
            throw new GlyphGenerationException(last.Message + Retried(shapeRedos, glyphRedraws, lastShape.Shape));

        throw new GlyphGenerationException(
            last!.Message + (baseRedraws + glyphRedraws == 0 ? "" :
                $" Retried before giving up: the base shape was redrawn {baseRedraws} time(s) and the whole glyph {glyphRedraws} time(s) " +
                "(copyOf.regenerateBase and copyOf.redrawGlyph set how many are allowed)."));
    }

    private static string Retried(int shapeRedos, int glyphRedraws, ShapeDefinition shape) =>
        shapeRedos + glyphRedraws == 0 ? "" :
            $" Retried before giving up: the shape was redrawn {shapeRedos} time(s) and the whole glyph {glyphRedraws} time(s) " +
            $"(placement.redoShape and placement.redrawGlyph on '{shape.Id}' set how many are allowed).";

    /// <summary>The shape a chain of copies ultimately comes from: the first one that is not itself a copy.</summary>
    private static string RootShapeId(LanguageDefinition language, ShapeDefinition shape)
    {
        var current = shape;
        for (int guard = 0; guard < language.Shapes.Count && current.CopyOf is { } c; guard++)
            current = language.Shapes.FirstOrDefault(x => x.Id == c.Shape) ?? current;
        return current.Id;
    }

    /// <summary>One go at building a plain glyph. salts: shapes whose random streams are re-keyed (a base shape being redrawn).</summary>
    private Glyph BuildPlainGlyph(
        LanguageDefinition language, GlyphDefinition glyphDef, LatticeGrid grid, ulong glyphSeed, Dictionary<string, int> salts)
    {
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

        var placedByShape = new Dictionary<string, List<SubSymbolChain>>(StringComparer.Ordinal);

        foreach (var shape in language.Shapes)
        {
            // A shape that is not for this glyph draws nothing (and so its copies make none); it takes no random draw,
            // so glyphs without a filter come out exactly as before.
            if (!shape.AppliesTo(glyphDef.Name) || !shape.PassesChance(rng))
            {
                placedByShape[shape.Id] = new List<SubSymbolChain>();
                continue;
            }

            if (shape.CopyOf is { } copyOf)
            {
                // An origin with a count of zero leaves nothing to copy, and so no copies.
                var origins = placedByShape.GetValueOrDefault(copyOf.Shape) ?? new List<SubSymbolChain>();

                var made = MakeCopies(language, shape, origins, grid, rng, geometry, glyphDef.Name, optional: false, world: glyph,
                    chainsOfShape: id => placedByShape.GetValueOrDefault(id));

                placedByShape[shape.Id] = made.Select(m => m.Copy).ToList();
                continue;
            }

            // No count = exactly one instance and NO roll (a roll would consume a draw from the
            // glyph's random stream and shift everything placed after it).
            int count = shape.Count is null ? 1 : ResolveLowBiased(shape.Count, rng, countExtraDraws);

            var usedOrigins = new HashSet<GridPoint>();
            for (int i = 0; i < count; i++)
            {
                var diagnostics = new PlacementDiagnostics();
                double lengthExtra = shape.Count is null ? 0.0 : lengthExtraDraws;
                var placed = GenerateShape(language, shape, grid, rng, geometry, i, lengthExtra, diagnostics, salts.GetValueOrDefault(shape.Id), usedOrigins)
                    ?? throw new ShapePlacementException(shape,
                        $"[{language.Name}] Shape '{shape.Id}'" +
                        (shape.Count is null ? "" : $" (instance {i + 1}/{count})") +
                        $" for glyph '{glyphDef.Name}' could not be placed within its configured " +
                        $"attempt budget. {diagnostics.Describe()}");

                placed.ShapeTag = $"{shape.Id}#{i}";
                geometry.Commit(placed, shape.Arrangement.Crossing);
                glyph.Chains.Add(placed);

                if (!placedByShape.TryGetValue(shape.Id, out var instances))
                    placedByShape[shape.Id] = instances = new List<SubSymbolChain>();
                instances.Add(placed);
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
        PlacementDiagnostics diagnostics,
        int redo = 0,
        HashSet<GridPoint>? usedOrigins = null)
    {
        // discardUsedOrigins: the origins earlier instances took are not offered again.
        var placement = shape.Placement;
        if (usedOrigins is not null && placement is { Mode: PlacementMode.Fixed, DiscardUsedOrigins: true, FixedOrigin: { } fixedOrigins })
        {
            var left = fixedOrigins.Points.Where(p => !usedOrigins.Contains(p)).ToList();
            if (left.Count == 0) return null;
            placement = placement.WithOrigins(left);
        }

        var originOf = new Dictionary<SubSymbolChain, GridPoint>(ReferenceEqualityComparer.Instance);

        // A placement.bounds box is the edge of the grid for this shape.
        if (shape.Placement.Bounds is { } box) grid = grid.Restricted(box.MinX, box.MinY, box.MaxX, box.MaxY);

        var rng = glyphRng.Derive(shape.StreamLabel(instanceIndex) + (redo > 0 ? $"-redo{redo}" : ""));
        var rotationOptions = RotationOptionsFor(language, shape.Placement);
        bool shapeVariesPerAttempt = shape.Kind == ShapeKind.Generative;

        geometry.SetCurrent(shape.Id, shape.Arrangement.Crossing);

        SubSymbolChain? ShapeFactory(GridPoint origin, int rotation, DeterministicRandom attemptRng)
        {
            SubSymbolChain? chain = shape.Kind switch
            {
                ShapeKind.Literal => BuildLiteralChain(shape, origin, rotation),
                ShapeKind.Generative => GenerateShapeChain(shape, grid, origin, attemptRng, geometry, lengthExtraDraws, language.Curve.ForceAllEdgesCurved),
                _ => throw new NotSupportedException($"Unknown shape kind: {shape.Kind}"),
            };

            if (chain is null) { diagnostics.ChainFailures++; return null; }

            // Curves are planned now, while the shape is placed, so later checks and steps see the real strokes.
            BendPlanner.Plan(chain, language.Curve, attemptRng.Derive("bend"), curved: BendPlanner.AnyCurved(chain, language.Curve));

            if (!SubSymbolSpacingChecker.SatisfiesSpacing(chain.AllNodes(), geometry.NodeGroups, shape.Arrangement.Spacing))
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
                placement, grid, candidateRng, geometry, shapeVariesPerAttempt, rotationOptions, ShapeFactory);
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
            originOf[result.PlacedShape] = result.Origin;
        }

        if (candidates.Count == 0) return null;

        SubSymbolChain Taken(SubSymbolChain chosen)
        {
            if (usedOrigins is not null && originOf.TryGetValue(chosen, out var origin)) usedOrigins.Add(origin);
            return chosen;
        }

        if (candidates.Count == 1 || !ranked) return Taken(candidates[0]);

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
        return Taken(best);
    }

    private static string Signature(SubSymbolChain chain) =>
        string.Join(";", chain.Nodes.Select(n => $"{n.X},{n.Y}")) + "|" + chain.IsClosedLoop;

    private SubSymbolChain? GenerateShapeChain(
        ShapeDefinition shape,
        LatticeGrid grid,
        GridPoint origin,
        DeterministicRandom rng,
        GeometryContext geometry,
        double lengthExtraDraws,
        bool allEdgesCurved)
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
            AllEdgesCurved = allEdgesCurved,
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
            // A recipe still being built further up is not rebuilt from here: its own attempt loop is what is running, and
            // it takes up the regenerated component by itself. (Rebuilding it would re-enter it and look like a cycle.)
            if (inProgress.Contains(name) && generated.TryGetValue(name, out var building)) return building;

            var componentDef = language.Glyphs.FirstOrDefault(g => g.Name == name)
                ?? language.BaseGlyphs.FirstOrDefault(g => g.Name == name)
                ?? new GlyphDefinition { Name = name };
            ulong seed = SplitMix64.DeriveSeed(glyphSeed, $"regen-{name}-{attemptIndex}");
            return GenerateGlyphCore(language, componentDef, grid, seed, generated, usageTracker, inProgress, variation: 0, languageSeed: languageSeed);
        }

        // Rebuilds another finished composite after one of its components was moved: a single attempt, nothing regenerated
        // or moved, null if it no longer fits.
        Glyph? RebuildComposite(string otherName)
        {
            if (!language.Combination.Recipes.TryGetValue(otherName, out var otherRecipe)) return null;
            var otherRng = new DeterministicRandom(SplitMix64.DeriveSeed(languageSeed, $"rebuild-{otherName}"));
            var rebuilt = _combinationResolver.TryCombine(
                otherName, otherRecipe.Components, generated, language.Combination, grid, otherRng, usageTracker,
                (_, _) => throw new GlyphGenerationException("A rebuild never regenerates components."),
                otherRecipe.EffectiveArrangement ?? language.Combination.Arrangement,
                0, languageSeed, null, null, isRebuild: true);
            if (rebuilt is null) return null;
            rebuilt.LanguageName = language.Name;
            rebuilt.GridWidth = grid.Width;
            rebuilt.GridHeight = grid.Height;
            return rebuilt;
        }

        string explanation = "";
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
            languageSeed,
            text => explanation = text,
            // Moving an original is only done by a composite that is built on its own, not by one rebuilt inside another's cascade.
            inProgress.Count <= 1 ? RebuildComposite : null);

        if (composite is null)
        {
            var used = recipe.EffectiveArrangement ?? language.Combination.Arrangement;
            bool hasFixed = used.AnyRuleFor(recipe.Components, p => p.Mode == ComponentPlacementMode.Fixed);

            throw new GlyphGenerationException(
                $"[{language.Name}] Could not satisfy combination recipe for '{glyphDef.Name}' " +
                $"after {language.Combination.MaxRetriesPerRecipe} attempts." +
                (explanation.Length > 0 ? " " + explanation : "") +
                (used.AnyRuleFor(recipe.Components, p => p.Mode != ComponentPlacementMode.Fixed)
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
