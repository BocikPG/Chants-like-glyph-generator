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
/// Each placed component counts as ONE shape for arrangement spacing: a candidate placement
/// must satisfy the arrangement's spacing rules (via SubSymbolSpacingChecker, the same code the
/// glyph generator uses for sub-symbols) against every component already placed in this
/// composite, on top of the no-crossing check.
///
/// A component can also have a placement rule (Arrangement.Placements): Fixed places it at exactly one
/// position; a side rule (Prefer / SetRandom) is a HARD requirement that its middle ends up on that side of the
/// other components' middles, and ranks its legal placements by nearness to that side like a bias does. Ruled
/// components are placed first.
///
/// Optionally (Arrangement.Bias) the search is steered instead of taking the first legal
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
        CombinationArrangementRules arrangement,
        int variation = 0,
        ulong languageSeed = 0,
        Action<string>? explainFailure = null,
        Func<string, Glyph?>? rebuildComposite = null,
        bool isRebuild = false,
        IReadOnlyDictionary<string, (CrossingConfig Crossing, double Clearance)>? strictShapes = null)
    {
        var report = new Dictionary<string, ComponentTally>();
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

        // Side preferences: listed sides, or SetRandom's draw - made once per composite (from a derived
        // stream, so nothing else's randomness moves) and used by every attempt. A glyph listed twice in a
        // recipe gets the same sides both times.
        var sides = new Dictionary<string, (List<GlyphSide> Sides, double Weight)>();
        foreach (var componentName in componentNames.Distinct())
        {
            var rule = arrangement.RuleFor(componentName);
            if (rule is null || rule.Mode == ComponentPlacementMode.Fixed) continue;
            bool random = rule.Mode == ComponentPlacementMode.SetRandom;
            // SetRandom: by default one draw per language and glyph (the same side in every composite, and
            // not moved by quality retries, which reseed the composite); Scope Composite draws per composite.
            var sideRng = random && languageSeed != 0 && (rule.Scope ?? SetRandomScope.Language) == SetRandomScope.Language
                ? new DeterministicRandom(SplitMix64.DeriveSeed(languageSeed, $"setrandom-side-{componentName}"))
                : rng.Derive($"side-{componentName}");
            var chosen = SideScoring.Choose(random, rule.Sides, rule.Pick, sideRng);
            if (chosen.Count > 0) sides[componentName] = (chosen, rule.Weight);
        }

        for (int attempt = 0; attempt < (isRebuild ? 1 : config.MaxRetriesPerRecipe); attempt++)
        {
            var attemptRng = rng.Derive($"combine-attempt-{attempt}");

            // Whatever happened since the last attempt (a component redrawn here, or deeper down while another recipe was being
            // rebuilt) is already in the shared registry: always build from the current originals, never from an older copy.
            foreach (var componentName in componentNames) working[componentName] = generated[componentName];

            // Components don't cross each other unless the arrangement says so - overlay
            // composition is deliberate controlled stacking, not free intersection. (A crossing
            // needs BOTH components to allow it; here they all share this arrangement.)
            var geometry = new GeometryContext(arrangement.Crossing);
            if (arrangement.Crossing.Mode == CrossingMode.AnywhereButRespectStrict && strictShapes is { Count: > 0 })
                geometry.ChainRules = chain => chain.ShapeTag is { } tag && tag.LastIndexOf('#') > 0
                    && strictShapes.TryGetValue(tag[..tag.LastIndexOf('#')], out var rules) ? rules : (arrangement.Crossing, 0.0);
            var placedByIndex = new PlacedComponent?[componentNames.Count];
            bool allPlaced = true;

            // Components with a placement rule go first (Fixed ones before side rules), so they get the
            // pick of the grid; the rest are placed around them. Stable, so recipe order breaks ties, and
            // the finished composite lists its components in recipe order again.
            int Priority(int i)
            {
                var rule = arrangement.RuleFor(componentNames[i]);
                if (rule is { Mode: ComponentPlacementMode.Fixed }) return 0;
                return sides.ContainsKey(componentNames[i]) ? 1 : 2;
            }

            foreach (int index in Enumerable.Range(0, componentNames.Count).OrderBy(Priority))
            {
                var name = componentNames[index];
                sides.TryGetValue(name, out var pull);
                geometry.CurrentSides = pull.Sides;
                if (!report.TryGetValue(name, out var tally)) report[name] = tally = new ComponentTally();
                var placed = TryPlaceComponent(working[name], name, config, grid, attemptRng, geometry, arrangement, pull.Sides, pull.Weight, variation, tally);
                if (placed is null && !isRebuild && rebuildComposite is not null
                    && arrangement.RuleFor(name) is { Mode: ComponentPlacementMode.Fixed, IfBlocked: FixedBlockedAction.Translate } moveRule)
                {
                    placed = TryMoveOriginal(name, moveRule, working, generated, compositeName, config, grid, attemptRng, geometry, arrangement, variation, tally, rebuildComposite);
                }

                if (placed is null)
                {
                    tally.Failures++;
                    allPlaced = false;
                    break;
                }

                geometry.CommitAsOneShape(placed.TransformedChains, arrangement.Crossing);
                var (cx, cy) = CenterOf(placed.TransformedChains);
                geometry.PlacedSides.Add((cx, cy, (IReadOnlyList<GlyphSide>)(pull.Sides ?? new List<GlyphSide>())));
                placedByIndex[index] = placed;
            }

            var placedComponents = placedByIndex.Where(p => p is not null).Select(p => p!).ToList();

            if (allPlaced)
            {
                foreach (var name in componentNames) usageTracker.RecordUsage(name);

                var composite = new Glyph { Name = compositeName };
                composite.Components.AddRange(placedComponents);
                return composite;
            }

            if (isRebuild) return null; // a rebuild is one attempt: nothing is regenerated for it

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

            foreach (var (otherName, otherRecipe) in config.Recipes)
            {
                if (otherName == compositeName) continue; // this recipe's own loop handles itself
                if (!otherRecipe.Components.Contains(toRegenerate)) continue;
                if (!generated.ContainsKey(otherName)) continue; // not built yet - will pick up
                                                                   // the new canonical version
                                                                   // naturally when its turn comes

                generated[otherName] = regenerateComponent(otherName, attempt);
            }
        }

        explainFailure?.Invoke(ComponentTally.Describe(report, config.MaxRetriesPerRecipe, arrangement, grid));
        return null;
    }

    /// <summary>
    /// The component's fixed position is blocked. Slides it to another free position - and moves the ORIGINAL glyph the
    /// same way, so the component still sits exactly where its own glyph does - provided every other finished composite
    /// that uses the glyph can be rebuilt with it. A position where one of them fails is given up and the next is tried.
    /// Null when no position works (the caller then regenerates as usual).
    /// </summary>
    private static PlacedComponent? TryMoveOriginal(
        string name,
        ComponentPlacementRule rule,
        Dictionary<string, Glyph> working,
        Dictionary<string, Glyph> generated,
        string compositeName,
        CombinationConfig config,
        LatticeGrid grid,
        DeterministicRandom rng,
        GeometryContext geometry,
        CombinationArrangementRules arrangement,
        int variation,
        ComponentTally tally,
        Func<string, Glyph?> rebuildComposite)
    {
        var original = working[name];
        if (original.IsComposite) return null; // only a drawn glyph can be moved as a whole

        var offset = rule.Offset ?? new GridPoint(0, 0);
        var chains = GlyphGeometryUtils.GetAllChains(original).ToList();
        var nodes = chains.SelectMany(c => c.Nodes).ToList();
        var others = config.Recipes.Where(r => r.Key != compositeName && r.Value.Components.Contains(name) && generated.ContainsKey(r.Key))
            .Select(r => r.Key).ToList();

        foreach (var translation in EnumerateShuffledTranslations(nodes, grid, rng.Derive($"move-{name}"), preferIdentity: variation == 0))
        {
            if (translation.X == offset.X && translation.Y == offset.Y) continue;

            var placed = TryBuildPlacement(chains, 1, 1, 0, translation, name, grid, geometry, arrangement, tally);
            if (placed is null) continue;

            // The original moves by the difference, so that placed again at the offset it lands exactly here.
            var shift = new GridPoint(translation.X - offset.X, translation.Y - offset.Y);
            var moved = TranslateGlyph(original, shift);
            if (!AllNodesInBounds(GlyphGeometryUtils.GetAllChains(moved).ToList(), grid)) continue;

            var before = generated[name];
            generated[name] = moved;
            var rebuilt = new Dictionary<string, Glyph>();
            bool ok = true;
            foreach (var other in others)
            {
                Glyph? result;
                try { result = rebuildComposite(other); }
                catch (GlyphGenerationException) { result = null; }
                if (result is null) { ok = false; break; }
                rebuilt[other] = result;
            }

            if (!ok) { generated[name] = before; continue; }

            foreach (var (other, glyph) in rebuilt) generated[other] = glyph;
            working[name] = moved;
            placed.Translation = offset;
            return placed;
        }

        return null;
    }

    /// <summary>A copy of a drawn glyph with everything moved by shift.</summary>
    private static Glyph TranslateGlyph(Glyph source, GridPoint shift)
    {
        var moved = new Glyph { Name = source.Name, LanguageName = source.LanguageName, GridWidth = source.GridWidth, GridHeight = source.GridHeight, Derived = source.Derived };
        moved.Chains.AddRange(TransformChains(source.Chains, 1, 1, 0, shift));
        return moved;
    }

    private static PlacedComponent? TryPlaceComponent(
        Glyph source,
        string sourceName,
        CombinationConfig config,
        LatticeGrid grid,
        DeterministicRandom rng,
        GeometryContext geometry,
        CombinationArrangementRules arrangement,
        List<GlyphSide>? preferSides,
        double sideWeight,
        int variation,
        ComponentTally tally)
    {
        var sourceChains = GlyphGeometryUtils.GetAllChains(source).ToList();

        // A placement rule for this component: Fixed has exactly one candidate (the component moved by its
        // offset, nothing else); Prefer ranks the legal placements by their nearness to the chosen side(s).
        var rule = arrangement.RuleFor(sourceName);
        if (rule is { Mode: ComponentPlacementMode.Fixed })
        {
            return TryBuildPlacement(
                sourceChains, 1, 1, 0, rule.Offset ?? new GridPoint(0, 0), sourceName, grid, geometry, arrangement, tally);
        }

        if (arrangement.Bias is { IsActive: true } || preferSides is { Count: > 0 })
            return TryPlaceBiased(sourceChains, sourceName, config, grid, rng, geometry, arrangement, preferSides, sideWeight, variation, tally);

        // Phase 1: identity scale/rotation, exhaustive-but-shuffled search across every
        // position the shape could legally occupy.
        var identity = TryAllTranslations(sourceChains, 1, 1, 0, sourceName, grid, rng.Derive("identity"), geometry, arrangement, variation, tally);
        if (identity is not null) return identity;

        if (!config.AllowScale && !config.AllowRotate) return null;

        var combos = OrderedAlternativeCombos(config, rng);

        foreach (var (num, den, rot) in combos)
        {
            if (!IsLatticeSafeScale(sourceChains, num, den)) continue;

            var comboRng = rng.Derive($"combo-{num}-{den}-{rot}");
            var result = TryAllTranslations(sourceChains, num, den, rot, sourceName, grid, comboRng, geometry, arrangement, variation, tally);
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
        CombinationArrangementRules arrangement,
        ComponentTally? tally = null)
    {
        var transformedChains = TransformChains(sourceChains, numerator, denominator, rotationDegrees, translation);

        if (tally is not null) tally.Tried++;
        if (!AllNodesInBounds(transformedChains, grid)) { if (tally is not null) tally.OffGrid++; return null; } // safety net; range math should already guarantee this
        if (!FitsGeometry(transformedChains, geometry)) { if (tally is not null) tally.Crossing++; return null; }
        if (!SatisfiesSides(transformedChains, geometry)) { if (tally is not null) tally.Sides++; return null; }
        if (!SubSymbolSpacingChecker.SatisfiesSpacingAsOneShape(transformedChains, geometry.NodeGroups, arrangement.Spacing)) { if (tally is not null) tally.Spacing++; return null; }

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

    /// <summary>Middle of the bounding box of a component's nodes.</summary>
    private static (double X, double Y) CenterOf(IEnumerable<SubSymbolChain> chains)
    {
        var nodes = chains.SelectMany(c => c.Nodes).ToList();
        if (nodes.Count == 0) return (0, 0);
        return ((nodes.Min(n => n.X) + nodes.Max(n => n.X)) / 2.0, (nodes.Min(n => n.Y) + nodes.Max(n => n.Y)) / 2.0);
    }

    private static bool Beyond(GlyphSide side, double x, double y, double otherX, double otherY) => side switch
    {
        GlyphSide.Left => x < otherX,
        GlyphSide.Right => x > otherX,
        GlyphSide.Top => y < otherY,
        _ => y > otherY,
    };

    /// <summary>The hard side rule: a component with a side rule must have its middle strictly on that side of
    /// the middle of every other component - the ones already placed (when it is placed) and the ones placed
    /// later (checked when they are). Two components asking for the same side: the one placed first is the more
    /// extreme. Horizontal and vertical are compared separately, so a corner means both.</summary>
    private static bool SatisfiesSides(List<SubSymbolChain> chains, GeometryContext geometry)
    {
        var mine = geometry.CurrentSides ?? Array.Empty<GlyphSide>();
        if (mine.Count == 0 && geometry.PlacedSides.All(p => p.Sides.Count == 0)) return true;

        var (cx, cy) = CenterOf(chains);
        foreach (var (qx, qy, qSides) in geometry.PlacedSides)
        {
            foreach (var side in mine)
                if (!qSides.Contains(side) && !Beyond(side, cx, cy, qx, qy)) return false;

            foreach (var side in qSides)
                if (!Beyond(side, qx, qy, cx, cy)) return false;
        }

        return true;
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
        CombinationArrangementRules arrangement,
        List<GlyphSide>? preferSides,
        double sideWeight,
        int variation,
        ComponentTally tally)
    {
        var bias = arrangement.Bias;
        var existing = BiasScoring.BoundsOfGroups(geometry.NodeGroups);

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
                double score = 0.0;

                if (preferSides is { Count: > 0 })
                    score += SideScoring.Score(preferSides, sideWeight, (minX + maxX) / 2.0, (minY + maxY) / 2.0, grid);

                if (bias.IsActive)
                {
                    if (existing is { } e)
                    {
                        minX = Math.Min(minX, e.MinX); maxX = Math.Max(maxX, e.MaxX);
                        minY = Math.Min(minY, e.MinY); maxY = Math.Max(maxY, e.MaxY);
                    }

                    score += BiasScoring.Score(bias, maxX - minX, maxY - minY, grid);
                }

                list.Add(new BiasCandidate(num, den, rot, t, score));
            }

            return list;
        }

        PlacedComponent? PickFrom(IEnumerable<BiasCandidate> candidates)
        {
            // OrderBy is stable: equal scores keep their (shuffled, identity-first) order.
            foreach (var c in candidates.OrderBy(c => c.Score))
            {
                var placed = TryBuildPlacement(
                    sourceChains, c.Numerator, c.Denominator, c.Rotation, c.Translation, sourceName, grid, geometry, arrangement, tally);
                if (placed is not null) return placed;
            }
            return null;
        }

        var identity = CollectFor(1, 1, 0, rng.Derive("identity"));

        var alternatives = config.AllowScale || config.AllowRotate
            ? OrderedAlternativeCombos(config, rng)
            : new List<(int Numerator, int Denominator, int Rotation)>();

        if (bias.IsActive && bias.IncludeScaleAndRotation)
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
        CombinationArrangementRules arrangement,
        int variation,
        ComponentTally tally)
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
                sourceChains, numerator, denominator, rotationDegrees, translation, sourceName, grid, geometry, arrangement, tally);
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
            var transformed = new SubSymbolChain { IsClosedLoop = chain.IsClosedLoop, MotifId = chain.MotifId, ShapeTag = chain.ShapeTag };
            transformed.Nodes.AddRange(chain.Nodes.Select(Transform));
            foreach (var edge in chain.Edges)
                transformed.Edges.Add(edge.WithPoints(Transform(edge.From), Transform(edge.To)));

            result.Add(transformed);
        }

        return result;
    }

    private static bool AllNodesInBounds(List<SubSymbolChain> chains, LatticeGrid grid) =>
        chains.All(c => c.Nodes.All(grid.Contains) && c.Edges.All(e => EdgeInGrid(e, grid)));

    /// <summary>A circle's outline can bulge past its two nodes, so check its whole extent.</summary>
    private static bool EdgeInGrid(GridEdge edge, LatticeGrid grid)
    {
        if (!edge.IsCircle) return true;
        var (minX, minY, maxX, maxY) = edge.Extent();
        return minX >= -1e-9 && minY >= -1e-9 && maxX <= grid.Width - 1 + 1e-9 && maxY <= grid.Height - 1 + 1e-9;
    }

    private static bool FitsGeometry(List<SubSymbolChain> chains, GeometryContext geometry)
    {
        var ownNodes = chains.SelectMany(c => c.AllNodes()).ToList();
        var (defaultCrossing, defaultClearance) = (geometry.Crossing, geometry.CurrentClearance);
        try
        {
            foreach (var chain in chains)
            {
                // Per chain settings: a Strict shape's strokes keep their rule inside a combined glyph (AnywhereButRespectStrict).
                if (geometry.ChainRules is { } rules)
                {
                    var (crossing, clearance) = rules(chain);
                    geometry.SetCurrent(null, crossing, clearance);
                }

                foreach (var edge in chain.Edges)
                    if (geometry.IsBlocked(edge, ownNodes)) return false;
            }
        }
        finally { geometry.SetCurrent(null, defaultCrossing, defaultClearance); }

        return true;
    }
}
/// <summary>Why the placements of one recipe component failed, over every attempt: how many candidate placements were
/// tried and which rule rejected each (counted once, under the first rule it broke).</summary>
internal sealed class ComponentTally
{
    public int Failures, Tried, OffGrid, Crossing, Sides, Spacing;

    public static string Describe(
        Dictionary<string, ComponentTally> report, int attempts, CombinationArrangementRules arrangement, LatticeGrid grid)
    {
        var failing = report.Where(kv => kv.Value.Failures > 0).OrderByDescending(kv => kv.Value.Failures).ToList();
        if (failing.Count == 0) return "";

        var lines = new List<string>();
        foreach (var (name, t) in failing.Take(3))
        {
            var reasons = new List<string>();
            if (t.OffGrid > 0) reasons.Add($"{t.OffGrid} left the grid");
            if (t.Crossing > 0) reasons.Add($"{t.Crossing} crossed another component");
            if (t.Sides > 0) reasons.Add($"{t.Sides} broke a side rule");
            if (t.Spacing > 0) reasons.Add($"{t.Spacing} broke arrangement.spacing");

            bool fixedRule = arrangement.RuleFor(name) is { Mode: ComponentPlacementMode.Fixed, IfBlocked: FixedBlockedAction.Regenerate };
            lines.Add($"'{name}' could not be placed in {t.Failures} of {attempts} attempt(s)" +
                (fixedRule ? " (it has a Fixed placement: one position per attempt)" : "") +
                $"; of {t.Tried} placement(s) tried, " + (reasons.Count == 0 ? "none were available" : string.Join(", ", reasons)) + ".");
        }

        var worst = failing[0].Value;
        int top = new[] { worst.OffGrid, worst.Crossing, worst.Sides, worst.Spacing }.Max();
        string hint =
            top == 0 ? ""
          : worst.Crossing == top ? " Components cross each other by default: set arrangement.crossing.mode, or give the recipe components that are smaller or sparser."
          : worst.Sides == top ? " The side rules are hard: check that the sides do not contradict each other and leave room on a grid of " + grid.Width + " x " + grid.Height + "."
          : worst.Spacing == top ? " Spacing rejects most placements: relax arrangement.spacing."
          : " The components are too big for the grid at the sizes allowed: turn on combination.allowScale with smaller allowedScales, or use a larger grid.";
        return string.Join(" ", lines) + hint;
    }
}
