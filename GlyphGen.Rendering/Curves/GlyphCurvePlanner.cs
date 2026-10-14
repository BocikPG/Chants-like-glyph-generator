using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;
using GlyphGen.Rendering.Geometry;

namespace GlyphGen.Rendering.Curves;

/// <summary>
/// Plans curves for every glyph of one language so that a glyph looks the same wherever it
/// appears. CurvePlanner.Plan alone seeds each glyph by ITS OWN name and walks its edges in its
/// own order, so a component re-planned from inside a composite got different random draws (and
/// scaled edges different proximity-guard results) than the same glyph rendered alone.
///
/// Here every glyph has one "natural" plan, made exactly as before for plain glyphs. A
/// composite's layers are built by mapping each component's SOURCE glyph plan through the
/// component's own transform (scale, rotation, translation - bezier control points transform
/// exactly under these). CurveContextMode then decides, per context, whether the natural curves
/// are shown or flattened to straight lines.
///
/// If a component's source can't be found, or its geometry doesn't line up with the stored
/// component chains, that layer falls back to the old behaviour (planned from the composite's
/// own seed) rather than drawing curves that don't match the geometry.
/// </summary>
public sealed class GlyphCurvePlanner
{
    private readonly Dictionary<string, Glyph> _glyphs = new();
    private readonly CurveConfig _curve;
    private readonly ulong _languageSeed;
    private readonly Dictionary<string, PlannedGlyph> _natural = new();
    private readonly HashSet<string> _inProgress = new();
    private readonly bool _miniCurved;
    private readonly bool _combinedCurved;

    public GlyphCurvePlanner(IEnumerable<Glyph> glyphs, CurveConfig curve, ulong languageSeed)
    {
        foreach (var g in glyphs) _glyphs[g.Name] = g;
        _curve = curve;
        _languageSeed = languageSeed;

        (_miniCurved, _combinedCurved) = curve.ContextMode switch
        {
            CurveContextMode.AllCurved => (true, true),
            CurveContextMode.AllStraight => (false, false),
            CurveContextMode.CurvedMiniStraightCombined => (true, false),
            CurveContextMode.StraightMiniCurvedCombined => (false, true),
            _ => throw new NotSupportedException($"Unknown curve context mode: {curve.ContextMode}"),
        };
    }

    /// <summary>The plan to draw for this glyph: "mini" rules for a plain glyph, "combined"
    /// rules for a composite.</summary>
    public PlannedGlyph Plan(Glyph glyph)
    {
        var natural = GetNatural(glyph);
        bool curved = glyph.Components.Count > 0 ? _combinedCurved : _miniCurved;
        return curved ? natural : Straightened(natural);
    }

    private PlannedGlyph GetNatural(Glyph glyph)
    {
        if (_natural.TryGetValue(glyph.Name, out var cached)) return cached;

        _inProgress.Add(glyph.Name);
        try
        {
            ulong seed = SplitMix64.DeriveSeed(SeedTree.ForGlyph(_languageSeed, glyph.Name), "curves");
            var result = glyph.Components.Count > 0
                ? PlanComposite(glyph, seed)
                : glyph.Derived is not null
                    ? PlanDerived(glyph, seed)
                    : PlanPlain(glyph, seed);

            _natural[glyph.Name] = result;
            return result;
        }
        finally
        {
            _inProgress.Remove(glyph.Name);
        }
    }

    /// <summary>A derived glyph is planned like a plain one, then every straight edge that survived from its source
    /// gets the source's own curve back (mapped through the glyph-wide scale/mirror/rotate that was applied, with the
    /// ends snapped to the edge's real nodes). New or moved edges keep the fresh plan.</summary>
    private PlannedGlyph PlanDerived(Glyph glyph, ulong seed)
    {
        var fresh = CurvePlanner.Plan(glyph, _curve, seed);
        var derived = glyph.Derived!;
        if (!_glyphs.TryGetValue(derived.Source, out var source) || _inProgress.Contains(source.Name)) return fresh;

        var sourceEdges = new Dictionary<(int, int, int, int), PlannedEdge>();
        foreach (var layer in GetNatural(source).Layers)
            foreach (var chain in layer.Chains.Where(c => c.Kind == PlannedChainKind.Stroke))
                foreach (var e in chain.Edges)
                    sourceEdges.TryAdd(EdgeKey(e.P0, e.P3), e);

        Vec2 A(Vec2 v) => new(
            derived.M11 * v.X + derived.M12 * v.Y + derived.Tx,
            derived.M21 * v.X + derived.M22 * v.Y + derived.Ty);

        ApplyLinks(fresh, derived.Links, sourceEdges, A);

        return fresh;
    }

    /// <summary>A plain glyph's plan; the chains that are flipped copies of another chain of the same glyph (shapes with
    /// copyOf) take the original's curves, flipped with them, in the order the copies were made.</summary>
    private PlannedGlyph PlanPlain(Glyph glyph, ulong seed)
    {
        var plan = CurvePlanner.Plan(glyph, _curve, seed);
        foreach (var copy in glyph.Copies)
        {
            var edges = new Dictionary<(int, int, int, int), PlannedEdge>();
            foreach (var layer in plan.Layers)
                foreach (var chain in layer.Chains.Where(c => c.Kind == PlannedChainKind.Stroke))
                    foreach (var e in chain.Edges)
                        edges.TryAdd(EdgeKey(e.P0, e.P3), e);

            Vec2 A(Vec2 v) => new(copy.M11 * v.X + copy.M12 * v.Y + copy.Tx, copy.M21 * v.X + copy.M22 * v.Y + copy.Ty);
            ApplyLinks(plan, copy.Links, edges, A, skipSelf: true);
        }

        return plan;
    }

    /// <summary>Every straight edge of the plan that has a link takes its linked edge's curve, carried through the map
    /// with its ends snapped to the edge's own nodes.</summary>
    private static void ApplyLinks(
        PlannedGlyph target,
        IReadOnlyDictionary<(int, int, int, int), (int, int, int, int)> links,
        Dictionary<(int, int, int, int), PlannedEdge> sourceEdges,
        Func<Vec2, Vec2> A,
        bool skipSelf = false)
    {
        foreach (var layer in target.Layers)
            foreach (var chain in layer.Chains.Where(c => c.Kind == PlannedChainKind.Stroke))
                for (int i = 0; i < chain.Edges.Count; i++)
                {
                    var edge = chain.Edges[i];
                    var key = EdgeKey(edge.P0, edge.P3);
                    if (!links.TryGetValue(key, out var link) || (skipSelf && link == key)) continue;
                    if (!sourceEdges.TryGetValue(link, out var src)) continue;

                    if (src.IsLine)
                    {
                        chain.Edges[i] = new PlannedEdge { P0 = edge.P0, C1 = edge.P0, C2 = edge.P3, P3 = edge.P3, IsLine = true };
                        continue;
                    }

                    // Which end of the source edge is which: the mapped source start lies on the nearer end.
                    var mappedStart = A(src.P0);
                    bool forward = (mappedStart - edge.P0).Length <= (mappedStart - edge.P3).Length;
                    var d0 = forward ? edge.P0 : edge.P3;
                    var d3 = forward ? edge.P3 : edge.P0;

                    var c1 = A(src.C1) + (d0 - mappedStart);
                    var c2 = A(src.C2) + (d3 - A(src.P3));
                    chain.Edges[i] = forward
                        ? new PlannedEdge { P0 = d0, C1 = c1, C2 = c2, P3 = d3, IsLine = false }
                        : new PlannedEdge { P0 = d3, C1 = c2, C2 = c1, P3 = d0, IsLine = false };
                }
    }

    private static (int, int, int, int) EdgeKey(Vec2 a, Vec2 b) =>
        GlyphGen.Core.Model.DerivedFrom.Key((int)Math.Round(a.X), (int)Math.Round(a.Y), (int)Math.Round(b.X), (int)Math.Round(b.Y));

    private PlannedGlyph PlanComposite(Glyph glyph, ulong seed)
    {
        var planned = new PlannedGlyph { Name = glyph.Name };

        // Old-style plan, only computed if something needs it (own chains, or a fallback layer).
        // CurvePlanner.Plan lays out layers as: base (if any own chains), then one per component.
        PlannedGlyph? legacy = null;
        PlannedGlyph Legacy() => legacy ??= CurvePlanner.Plan(glyph, _curve, seed);

        bool hasBase = glyph.Chains.Count > 0;
        if (hasBase) planned.Layers.Add(Legacy().Layers[0]);
        int offset = hasBase ? 1 : 0;

        // CurveConfig.RecheckProximityInCombined: re-run the proximity guard on each reused
        // component, now that it has its final size and neighbours.
        bool recheck = _curve.RecheckProximityInCombined && _curve.ProximityGuard.Enabled && !_curve.ForceAllEdgesCurved;
        List<Vec2>? compositePoints = null;
        List<Vec2>? RecheckPoints()
        {
            if (!recheck) return null;
            return compositePoints ??= _curve.ProximityGuard.MinDistanceToOtherNodes > 0
                ? GlyphGeometryUtils.GetAllNodes(glyph).Select(p => new Vec2(p.X, p.Y)).Distinct().ToList()
                : new List<Vec2>();
        }

        for (int i = 0; i < glyph.Components.Count; i++)
        {
            var layer = TryReuseSourcePlan(glyph.Components[i], RecheckPoints()) ?? Legacy().Layers[offset + i];
            planned.Layers.Add(layer);
        }

        return planned;
    }

    private PlannedLayer? TryReuseSourcePlan(PlacedComponent component, List<Vec2>? recheckPoints)
    {
        if (!_glyphs.TryGetValue(component.SourceGlyphName, out var source)) return null;
        if (_inProgress.Contains(source.Name)) return null; // would be a cycle

        var map = BuildMap(component);
        if (!GeometryMatches(source, component, map)) return null;

        var layer = new PlannedLayer { Label = component.SourceGlyphName };
        foreach (var sourceLayer in GetNatural(source).Layers)
            foreach (var chain in sourceLayer.Chains)
                layer.Chains.Add(MapChain(chain, map));

        if (recheckPoints is not null) FlattenGuardedEdges(layer, recheckPoints);

        return layer;
    }

    /// <summary>The proximity guard, applied to an already-mapped component layer (a fresh copy,
    /// so the source glyph's cached plan is untouched): curved edges the guard would force
    /// straight at their new size and position become lines.</summary>
    private void FlattenGuardedEdges(PlannedLayer layer, List<Vec2> allPoints)
    {
        var guard = _curve.ProximityGuard;
        foreach (var chain in layer.Chains)
        {
            for (int i = 0; i < chain.Edges.Count; i++)
            {
                if (chain.Kind != PlannedChainKind.Stroke) break;
                var e = chain.Edges[i];
                if (e.IsLine) continue;

                if (CurvePlanner.IsForcedStraight(e.P0, e.P3, (e.P3 - e.P0).Length, guard, allPoints))
                    chain.Edges[i] = new PlannedEdge { P0 = e.P0, C1 = e.P0, C2 = e.P3, P3 = e.P3, IsLine = true };
            }
        }
    }

    /// <summary>The component's transform as a plain affine map on grid coordinates. Built by
    /// running the SAME GridPoint operations CombinationResolver uses (so the rotation
    /// direction can't drift out of sync) on den*e1 and den*e2, where the integer scale division
    /// is exact, then dividing back down.</summary>
    private static Func<Vec2, Vec2> BuildMap(PlacedComponent c)
    {
        int num = c.ScaleNumerator, den = c.ScaleDenominator;
        var zero = new GridPoint(0, 0);

        GridPoint Linear(int x, int y) =>
            new GridPoint(x * num / den, y * num / den).RotateAround(zero, c.RotationDegrees);

        var ex = Linear(den, 0);
        var ey = Linear(0, den);

        double m11 = ex.X / (double)den, m21 = ex.Y / (double)den; // image of (1,0)
        double m12 = ey.X / (double)den, m22 = ey.Y / (double)den; // image of (0,1)
        double tx = c.Translation.X, ty = c.Translation.Y;

        return v => new Vec2(m11 * v.X + m12 * v.Y + tx, m21 * v.X + m22 * v.Y + ty);
    }

    /// <summary>Safety net: the mapped source nodes must land exactly on the component's stored
    /// nodes, otherwise the source plan doesn't describe this component's geometry.</summary>
    private static bool GeometryMatches(Glyph source, PlacedComponent component, Func<Vec2, Vec2> map)
    {
        var sourceChains = GlyphGeometryUtils.GetAllChains(source).ToList();
        if (sourceChains.Count != component.TransformedChains.Count) return false;

        for (int i = 0; i < sourceChains.Count; i++)
        {
            var a = sourceChains[i].Nodes;
            var b = component.TransformedChains[i].Nodes;
            if (a.Count != b.Count) return false;

            for (int j = 0; j < a.Count; j++)
            {
                var m = map(new Vec2(a[j].X, a[j].Y));
                if (Math.Abs(m.X - b[j].X) > 1e-6 || Math.Abs(m.Y - b[j].Y) > 1e-6) return false;
            }
        }

        return true;
    }

    private static PlannedChain MapChain(PlannedChain chain, Func<Vec2, Vec2> map)
    {
        var result = new PlannedChain { Closed = chain.Closed, MotifId = chain.MotifId, ShapeTag = chain.ShapeTag, Kind = chain.Kind };
        foreach (var e in chain.Edges)
        {
            result.Edges.Add(new PlannedEdge
            {
                P0 = map(e.P0), C1 = map(e.C1), C2 = map(e.C2), P3 = map(e.P3), IsLine = e.IsLine,
            });
        }
        return result;
    }

    private static PlannedGlyph Straightened(PlannedGlyph source)
    {
        var result = new PlannedGlyph { Name = source.Name };
        foreach (var layer in source.Layers)
        {
            var newLayer = new PlannedLayer { Label = layer.Label };
            foreach (var chain in layer.Chains)
            {
                var newChain = new PlannedChain { Closed = chain.Closed, MotifId = chain.MotifId, ShapeTag = chain.ShapeTag, Kind = chain.Kind };
                foreach (var e in chain.Edges)
                    newChain.Edges.Add(chain.Kind != PlannedChainKind.Stroke
                        ? e // circles and dots are not "bent lines": flattening would wreck them
                        : new PlannedEdge { P0 = e.P0, C1 = e.P0, C2 = e.P3, P3 = e.P3, IsLine = true });
                newLayer.Chains.Add(newChain);
            }
            result.Layers.Add(newLayer);
        }
        return result;
    }
}
