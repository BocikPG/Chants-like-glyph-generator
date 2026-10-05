using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Default ICombinationResolver.
///
/// Placement search is exhaustive-but-randomized, not a bounded random sample: for a given
/// scale/rotation, every grid position the shape could legally occupy (derived from its
/// bounding box) is enumerated, shuffled, and walked until one fits or none do - so a fit is
/// found whenever one exists anywhere on the grid at that scale/rotation, not just when random
/// luck happens to land on it. The (0,0) true overlay is always tried first even after
/// shuffling, since "translated by default" means identity is preferred, not that it gets the
/// same odds as every other position. Only if translation alone (scale 1/1, rotation 0) can't
/// find a fit anywhere does the search widen to every allowed scale/rotation combination
/// (itself shuffled), each with its own full exhaustive translation search. Only once every
/// combination is exhausted does this fall back to regenerating a dependency.
///
/// Each placed component counts as ONE shape for SubSymbolSpacingConfig: a candidate placement
/// must satisfy the language's spacing rules (via SubSymbolSpacingChecker, the same code the
/// glyph generator uses for sub-symbols) against every component already placed in this
/// composite, on top of the no-crossing check.
///
/// Optionally (Combination.Bias) the search is steered instead of taking the first legal
/// placement: every legal candidate is ranked by how well the resulting composite's bounding box
/// matches the bias (tallest, narrowest, squarest, ...) and the best one that passes ALL the
/// normal checks wins. The bias is only a ranking - it never relaxes a constraint.
/// </summary>
public sealed class CombinationResolver : ICombinationResolver
{
    public Glyph? TryCombine(
        string compositeName,
        IReadOnlyList<string> componentNames,
        Dictionary<string, Glyph> generated,
        CombinationConfig config,
        LatticeGrid grid,
        DeterministicRandom rng,
        GlyphUsageTracker usageTracker,
        Func<string, int, Glyph> regenerateComponent,
        SubSymbolSpacingConfig spacing,
        int variation = 0)
    {
        var working = new Dictionary<string, Glyph>();
        foreach (var name in componentNames)
        {
            if (!generated.TryGetValue(name, out var g))
            {
                throw new GlyphGenerationException(
                    $"Combination recipe for '{compositeName}' references '{name}', which has " +
                    "not been generated yet (check generation ordering / for a typo).");
            }
            working[name] = g;
        }

        for (int attempt = 0; attempt < config.MaxRetriesPerRecipe; attempt++)
        {
            var attemptRng = rng.Derive($"combine-attempt-{attempt}");

            // Components don't cross each other unless Combination.AllowComponentsToCross says so,
            // regardless of the language's general AllowSymbolsToCross - overlay composition is
            // deliberate controlled stacking, not free intersection.
            var geometry = new GeometryContext(allowCrossing: config.AllowComponentsToCross);
            var placedComponents = new List<PlacedComponent>();
            bool allPlaced = true;

            foreach (var name in componentNames)
            {
                var placed = TryPlaceComponent(working[name], name, config, grid, attemptRng, geometry, spacing, variation);
                if (placed is null)
                {
                    allPlaced = false;
                    break;
                }

                geometry.CommitAsOneShape(placed.TransformedChains);
                placedComponents.Add(placed);
            }

            if (allPlaced)
            {
                foreach (var name in componentNames) usageTracker.RecordUsage(name);

                var composite = new Glyph { Name = compositeName };
                composite.Components.AddRange(placedComponents);
                return composite;
            }

            // Escalate: regenerate the least-used dependency first, cycling through the
            // recipe's remaining components as attempts continue. The regenerated glyph
            // replaces the CANONICAL entry in the shared registry - not just this recipe's
            // local working copy - so the standalone glyph of that name, and every other
            // already-built recipe referencing it, all move to the same new version rather
            // than silently diverging.
            var leastUsedFirst = componentNames.OrderBy(usageTracker.GetUsageCount).ToList();
            var toRegenerate = leastUsedFirst[attempt % leastUsedFirst.Count];

            var regenerated = regenerateComponent(toRegenerate, attempt);
            working[toRegenerate] = regenerated;
            generated[toRegenerate] = regenerated;

            foreach (var (otherName, otherComponents) in config.Recipes)
            {
                if (otherName == compositeName) continue; // this recipe's own loop handles itself
                if (!otherComponents.Contains(toRegenerate)) continue;
                if (!generated.ContainsKey(otherName)) continue; // not built yet - will pick up
                                                                   // the new canonical version
                                                                   // naturally when its turn comes

                generated[otherName] = regenerateComponent(otherName, attempt);
            }
        }

        return null;
    }

    private static PlacedComponent? TryPlaceComponent(
        Glyph source,
        string sourceName,
        CombinationConfig config,
        LatticeGrid grid,
        DeterministicRandom rng,
        GeometryContext geometry,
        SubSymbolSpacingConfig spacing,
        int variation)
    {
        var sourceChains = GlyphGeometryUtils.GetAllChains(source).ToList();

        if (config.Bias is { IsActive: true })
            return TryPlaceBiased(sourceChains, sourceName, config, grid, rng, geometry, spacing, variation);

        // Phase 1: identity scale/rotation, exhaustive-but-shuffled search across every
        // position the shape could legally occupy.
        var identity = TryAllTranslations(sourceChains, 1, 1, 0, sourceName, grid, rng.Derive("identity"), geometry, spacing, variation);
        if (identity is not null) return identity;

        if (!config.AllowScale && !config.AllowRotate) return null;

        var combos = OrderedAlternativeCombos(config, rng);

        foreach (var (num, den, rot) in combos)
        {
            if (!IsLatticeSafeScale(sourceChains, num, den)) continue;

            var comboRng = rng.Derive($"combo-{num}-{den}-{rot}");
            var result = TryAllTranslations(sourceChains, num, den, rot, sourceName, grid, comboRng, geometry, spacing, variation);
            if (result is not null) return result;
        }

        return null;
    }

    /// <summary>
    /// Every scale/rotation combination the config allows other than the identity (1/1, 0
    /// degrees), in the order they should be tried. A combination's weight is its scale factor's
    /// Weight times (for a non-zero angle) Rotation.Weight, so heavier combinations tend to come
    /// first; weight 0 means never use it. When every weight is equal this is the plain uniform
    /// shuffle it always was (so such configs keep their exact results); otherwise combinations
    /// are drawn one at a time by weight, without replacement.
    /// </summary>
    private static List<(int Numerator, int Denominator, int Rotation)> OrderedAlternativeCombos(
        CombinationConfig config, DeterministicRandom rng)
    {
        var scaleOptions = config.AllowScale && config.AllowedScales.Count > 0
            ? config.AllowedScales.Select(s => (s.Numerator, s.Denominator, s.Weight)).ToList()
            : new List<(int Numerator, int Denominator, double Weight)> { (1, 1, 1.0) };
        var rotationOptions = config.AllowRotate && config.Rotation.Allowed.Count > 0
            ? config.Rotation.Allowed.Select(angle => (Angle: angle, Weight: angle == 0 ? 1.0 : config.Rotation.Weight)).ToList()
            : new List<(int Angle, double Weight)> { (0, 1.0) };

        var combos = new List<(int Numerator, int Denominator, int Rotation)>();
        var weights = new List<double>();
        foreach (var (num, den, scaleWeight) in scaleOptions)
            foreach (var (rot, rotationWeight) in rotationOptions)
            {
                if (num == 1 && den == 1 && rot == 0) continue; // the identity is always tried first, separately

                double weight = scaleWeight * rotationWeight;
                if (!(weight > 0)) continue; // weight 0 (or not a number): excluded
                combos.Add((num, den, rot));
                weights.Add(weight);
            }

        if (weights.Count <= 1 || weights.All(w => w == weights[0]))
        {
            ShuffleInPlace(combos, rng);
            return combos;
        }

        var ordered = new List<(int Numerator, int Denominator, int Rotation)>(combos.Count);
        while (combos.Count > 0)
        {
            int pick = combos.Count == 1 ? 0 : rng.WeightedIndex(weights);
            ordered.Add(combos[pick]);
            combos.RemoveAt(pick);
            weights.RemoveAt(pick);
        }

        return ordered;
    }

    /// <summary>The one place a candidate placement is turned into a PlacedComponent: transform,
    /// then every hard check (bounds, no crossing, spacing). Null if any check fails.</summary>
    private static PlacedComponent? TryBuildPlacement(
        List<SubSymbolChain> sourceChains,
        int numerator,
        int denominator,
        int rotationDegrees,
        GridPoint translation,
        string sourceName,
        LatticeGrid grid,
        GeometryContext geometry,
        SubSymbolSpacingConfig spacing)
    {
        var transformedChains = TransformChains(sourceChains, numerator, denominator, rotationDegrees, translation);

        if (!AllNodesInBounds(transformedChains, grid)) return null; // safety net; range math should already guarantee this
        if (!FitsGeometry(transformedChains, geometry)) return null;
        if (!SubSymbolSpacingChecker.SatisfiesSpacingAsOneShape(transformedChains, geometry.NodeGroups, spacing)) return null;

        var placed = new PlacedComponent
        {
            SourceGlyphName = sourceName,
            Translation = translation,
            RotationDegrees = rotationDegrees,
            ScaleNumerator = numerator,
            ScaleDenominator = denominator,
        };
        placed.TransformedChains.AddRange(transformedChains);
        return placed;
    }

    private readonly record struct BiasCandidate(
        int Numerator, int Denominator, int Rotation, GridPoint Translation, double Score);

    /// <summary>
    /// Bias-steered placement. Candidates (translations, and with IncludeScaleAndRotation also
    /// every allowed scale/rotation) are scored by the bounding box of "everything already
    /// placed + this component" - cheap, since a shifted shape's box is just its box plus the
    /// offset - then walked best-first (stable: ties keep the shuffled order, so identity and
    /// randomness behave as in the unbiased search) until one passes every hard check.
    /// </summary>
    private static PlacedComponent? TryPlaceBiased(
        List<SubSymbolChain> sourceChains,
        string sourceName,
        CombinationConfig config,
        LatticeGrid grid,
        DeterministicRandom rng,
        GeometryContext geometry,
        SubSymbolSpacingConfig spacing,
        int variation)
    {
        var bias = config.Bias;
        var existing = BoundsOfGroups(geometry.NodeGroups);

        List<BiasCandidate> CollectFor(int num, int den, int rot, DeterministicRandom comboRng)
        {
            var zero = new GridPoint(0, 0);
            var shape = sourceChains
                .SelectMany(c => c.Nodes)
                .Select(p => new GridPoint((p.X * num) / den, (p.Y * num) / den).RotateAround(zero, rot))
                .ToList();

            var list = new List<BiasCandidate>();
            if (shape.Count == 0)
            {
                list.Add(new BiasCandidate(num, den, rot, new GridPoint(0, 0), 0.0));
                return list;
            }

            int sMinX = shape.Min(p => p.X), sMaxX = shape.Max(p => p.X);
            int sMinY = shape.Min(p => p.Y), sMaxY = shape.Max(p => p.Y);

            foreach (var t in EnumerateShuffledTranslations(shape, grid, comboRng, preferIdentity: variation == 0))
            {
                int minX = sMinX + t.X, maxX = sMaxX + t.X, minY = sMinY + t.Y, maxY = sMaxY + t.Y;
                if (existing is { } e)
                {
                    minX = Math.Min(minX, e.MinX); maxX = Math.Max(maxX, e.MaxX);
                    minY = Math.Min(minY, e.MinY); maxY = Math.Max(maxY, e.MaxY);
                }

                list.Add(new BiasCandidate(num, den, rot, t, BiasScore(bias, maxX - minX, maxY - minY, grid)));
            }

            return list;
        }

        PlacedComponent? PickFrom(IEnumerable<BiasCandidate> candidates)
        {
            // OrderBy is stable: equal scores keep their (shuffled, identity-first) order.
            foreach (var c in candidates.OrderBy(c => c.Score))
            {
                var placed = TryBuildPlacement(
                    sourceChains, c.Numerator, c.Denominator, c.Rotation, c.Translation, sourceName, grid, geometry, spacing);
                if (placed is not null) return placed;
            }
            return null;
        }

        var identity = CollectFor(1, 1, 0, rng.Derive("identity"));

        var alternatives = config.AllowScale || config.AllowRotate
            ? OrderedAlternativeCombos(config, rng)
            : new List<(int Numerator, int Denominator, int Rotation)>();

        if (bias.IncludeScaleAndRotation)
        {
            var all = new List<BiasCandidate>(identity);
            foreach (var (num, den, rot) in alternatives)
            {
                if (!IsLatticeSafeScale(sourceChains, num, den)) continue;
                all.AddRange(CollectFor(num, den, rot, rng.Derive($"combo-{num}-{den}-{rot}")));
            }
            return PickFrom(all);
        }

        // Translation-only bias: same escalation order as the unbiased search - identity first,
        // then the first scale/rotation combination that has any legal placement.
        var result = PickFrom(identity);
        if (result is not null) return result;

        foreach (var (num, den, rot) in alternatives)
        {
            if (!IsLatticeSafeScale(sourceChains, num, den)) continue;

            result = PickFrom(CollectFor(num, den, rot, rng.Derive($"combo-{num}-{den}-{rot}")));
            if (result is not null) return result;
        }

        return null;
    }

    /// <summary>Lower is better. Each active mode contributes a term on a 0..1 scale (extents
    /// are divided by the grid's extent, squareness is already a ratio) and the terms are
    /// summed (each multiplied by its configured weight, 1 by default).</summary>
    private static double BiasScore(CombinationBiasConfig bias, int width, int height, LatticeGrid grid)
    {
        double w = (double)width / Math.Max(1, grid.Width - 1);
        double h = (double)height / Math.Max(1, grid.Height - 1);

        double score = 0.0;
        foreach (var mode in bias.Modes)
        {
            score += bias.WeightOf(mode) * mode switch
            {
                CombinationBiasMode.Tallest => -h,
                CombinationBiasMode.Shortest => h,
                CombinationBiasMode.Widest => -w,
                CombinationBiasMode.Narrowest => w,
                CombinationBiasMode.Squarest => Math.Max(width, height) > 0
                    ? 1.0 - (double)Math.Min(width, height) / Math.Max(width, height)
                    : 0.0,
                _ => 0.0,
            };
        }

        return score;
    }

    private static (int MinX, int MaxX, int MinY, int MaxY)? BoundsOfGroups(
        IReadOnlyList<IReadOnlyList<GridPoint>> groups)
    {
        bool any = false;
        int minX = int.MaxValue, maxX = int.MinValue, minY = int.MaxValue, maxY = int.MinValue;

        foreach (var group in groups)
            foreach (var p in group)
            {
                any = true;
                minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X);
                minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y);
            }

        return any ? (minX, maxX, minY, maxY) : null;
    }

    /// <summary>Exhaustive-but-shuffled search across every valid translation for one fixed
    /// scale/rotation - not a bounded random sample.</summary>
    private static PlacedComponent? TryAllTranslations(
        List<SubSymbolChain> sourceChains,
        int numerator,
        int denominator,
        int rotationDegrees,
        string sourceName,
        LatticeGrid grid,
        DeterministicRandom rng,
        GeometryContext geometry,
        SubSymbolSpacingConfig spacing,
        int variation)
    {
        var origin = new GridPoint(0, 0);

        GridPoint ScaleRotate(GridPoint p)
        {
            var scaled = new GridPoint((p.X * numerator) / denominator, (p.Y * numerator) / denominator);
            return scaled.RotateAround(origin, rotationDegrees);
        }

        var preTranslated = sourceChains.SelectMany(c => c.Nodes).Select(ScaleRotate).ToList();

        foreach (var translation in EnumerateShuffledTranslations(preTranslated, grid, rng, preferIdentity: variation == 0))
        {
            var placed = TryBuildPlacement(
                sourceChains, numerator, denominator, rotationDegrees, translation, sourceName, grid, geometry, spacing);
            if (placed is not null) return placed;
        }

        return null;
    }

    /// <summary>Every translation offset that keeps a shape (already scaled/rotated, but not
    /// yet translated) fully within grid bounds, in shuffled order, with the (0,0) identity
    /// offset moved to the front when it's a valid option - unless preferIdentity is false (a
    /// quality retry of a composite, which needs a different arrangement than the first try).</summary>
    private static IEnumerable<GridPoint> EnumerateShuffledTranslations(
        List<GridPoint> shapeAtOrigin, LatticeGrid grid, DeterministicRandom rng, bool preferIdentity)
    {
        if (shapeAtOrigin.Count == 0)
        {
            yield return new GridPoint(0, 0);
            yield break;
        }

        int minX = shapeAtOrigin.Min(p => p.X), maxX = shapeAtOrigin.Max(p => p.X);
        int minY = shapeAtOrigin.Min(p => p.Y), maxY = shapeAtOrigin.Max(p => p.Y);

        int dxLow = -minX, dxHigh = grid.Width - 1 - maxX;
        int dyLow = -minY, dyHigh = grid.Height - 1 - maxY;

        if (dxHigh < dxLow || dyHigh < dyLow) yield break; // can never fit at this scale/rotation

        var offsets = new List<GridPoint>((dxHigh - dxLow + 1) * (dyHigh - dyLow + 1));
        for (int dx = dxLow; dx <= dxHigh; dx++)
            for (int dy = dyLow; dy <= dyHigh; dy++)
                offsets.Add(new GridPoint(dx, dy));

        ShuffleInPlace(offsets, rng);

        int identityIndex = preferIdentity ? offsets.IndexOf(new GridPoint(0, 0)) : -1;
        if (identityIndex > 0)
        {
            (offsets[0], offsets[identityIndex]) = (offsets[identityIndex], offsets[0]);
        }

        foreach (var o in offsets) yield return o;
    }

    private static void ShuffleInPlace<T>(List<T> list, DeterministicRandom rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.NextInt(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    /// <summary>Exact divisibility check: a shrink factor is only lattice-safe if every node's
    /// raw coordinate scales to a whole number. Upscaling is always safe. Anchored at (0,0) -
    /// the shared origin every glyph's own grid already uses.</summary>
    private static bool IsLatticeSafeScale(List<SubSymbolChain> chains, int numerator, int denominator)
    {
        if (numerator == denominator) return true;

        foreach (var chain in chains)
            foreach (var node in chain.Nodes)
            {
                if ((node.X * numerator) % denominator != 0) return false;
                if ((node.Y * numerator) % denominator != 0) return false;
            }

        return true;
    }

    private static List<SubSymbolChain> TransformChains(
        List<SubSymbolChain> source,
        int numerator,
        int denominator,
        int rotationDegrees,
        GridPoint translation)
    {
        var origin = new GridPoint(0, 0);
        var result = new List<SubSymbolChain>(source.Count);

        GridPoint Transform(GridPoint p)
        {
            var scaled = new GridPoint((p.X * numerator) / denominator, (p.Y * numerator) / denominator);
            var rotated = scaled.RotateAround(origin, rotationDegrees);
            return rotated.Translate(translation.X, translation.Y);
        }

        foreach (var chain in source)
        {
            var transformed = new SubSymbolChain { IsClosedLoop = chain.IsClosedLoop, MotifId = chain.MotifId };
            transformed.Nodes.AddRange(chain.Nodes.Select(Transform));
            foreach (var edge in chain.Edges)
                transformed.Edges.Add(new GridEdge(Transform(edge.From), Transform(edge.To)));

            result.Add(transformed);
        }

        return result;
    }

    private static bool AllNodesInBounds(List<SubSymbolChain> chains, LatticeGrid grid) =>
        chains.All(c => c.Nodes.All(grid.Contains));

    private static bool FitsGeometry(List<SubSymbolChain> chains, GeometryContext geometry)
    {
        if (geometry.AllowCrossing) return true;

        foreach (var chain in chains)
            foreach (var edge in chain.Edges)
                foreach (var existing in geometry.ExistingEdges)
                    if (edge.Intersects(existing)) return false;

        return true;
    }
}
