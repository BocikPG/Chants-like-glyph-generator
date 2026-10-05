using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;
using GlyphGen.Rendering.Geometry;

namespace GlyphGen.Rendering.Curves;

/// <summary>
/// Stage 1 of rendering: turns a glyph's straight grid edges into a drawable plan (each edge a
/// line or a cubic bezier), per the language's CurveConfig. Pure geometry, fully determined by
/// the seed. Topology and intersection checks only ever saw the straight edges, so a curve can
/// in principle bulge into a neighbouring stroke; the tension cap keeps that rare, and
/// CurveConfig.ProximityGuard (opt-in) forces an edge straight outright when geometry makes
/// that bulge more likely - but neither guarantees it can't happen.
/// </summary>
public static class CurvePlanner
{
    private readonly record struct NodeHandle(Vec2 OutDir, Vec2 InDir, double OutMag, double InMag);

    public static PlannedGlyph Plan(Glyph glyph, CurveConfig curve, ulong seed)
    {
        var rng = new DeterministicRandom(seed);

        // Resolved once per glyph, then applied as a per-edge probability - a soft hint, so
        // the realised share of straight edges is only approximately this value.
        double straightProbability = Math.Clamp(curve.StraightLineBias.Resolve(rng) / 100.0, 0.0, 1.0);

        // Gathered once for the proximity guard's MinDistanceToOtherNodes check, which needs
        // visibility across every chain/component in the glyph, not just the one being planned.
        var allPoints = curve.ProximityGuard.Enabled && curve.ProximityGuard.MinDistanceToOtherNodes > 0
            ? GlyphGeometryUtils.GetAllNodes(glyph).Select(p => new Vec2(p.X, p.Y)).Distinct().ToList()
            : new List<Vec2>();

        var planned = new PlannedGlyph { Name = glyph.Name };

        if (glyph.Chains.Count > 0)
            planned.Layers.Add(PlanLayer("base", glyph.Chains, curve, straightProbability, allPoints, rng));

        foreach (var component in glyph.Components)
            planned.Layers.Add(PlanLayer(component.SourceGlyphName, component.TransformedChains, curve, straightProbability, allPoints, rng));

        return planned;
    }

    private static PlannedLayer PlanLayer(
        string label,
        IEnumerable<SubSymbolChain> chains,
        CurveConfig curve,
        double straightProbability,
        List<Vec2> allPoints,
        DeterministicRandom rng)
    {
        var layer = new PlannedLayer { Label = label };
        foreach (var chain in chains)
        {
            var planned = PlanChain(chain, curve, straightProbability, allPoints, rng);
            if (planned is not null) layer.Chains.Add(planned);
        }
        return layer;
    }

    private static PlannedChain? PlanChain(
        SubSymbolChain chain,
        CurveConfig curve,
        double straightProbability,
        List<Vec2> allPoints,
        DeterministicRandom rng)
    {
        var pts = new List<Vec2>();
        foreach (var node in chain.Nodes)
        {
            var v = new Vec2(node.X, node.Y);
            if (pts.Count == 0 || (v - pts[^1]).Length > 1e-9) pts.Add(v);
        }

        if (pts.Count < 2) return null;

        bool closed = chain.IsClosedLoop && pts.Count >= 3;
        int n = pts.Count;

        var handles = new NodeHandle[n];
        for (int i = 0; i < n; i++)
            handles[i] = MakeHandle(pts, i, closed, curve, rng);

        var result = new PlannedChain { Closed = closed, MotifId = chain.MotifId };
        int edgeCount = closed ? n : n - 1;

        for (int i = 0; i < edgeCount; i++)
        {
            int a = i, b = (i + 1) % n;
            Vec2 pa = pts[a], pb = pts[b];
            double len = (pb - pa).Length;
            if (len < 1e-9) continue;

            // Always drawn, even when the proximity guard will override the result below -
            // keeps RNG consumption per edge identical regardless of whether the guard is
            // enabled, so toggling it on/off doesn't shift unrelated downstream draws.
            bool rolledStraight = rng.NextBool(straightProbability);
            bool forcedStraight = IsForcedStraight(pa, pb, len, curve.ProximityGuard, allPoints);

            if (rolledStraight || forcedStraight)
            {
                result.Edges.Add(new PlannedEdge { P0 = pa, P3 = pb, C1 = pa, C2 = pb, IsLine = true });
                continue;
            }

            var c1 = pa + handles[a].OutDir * (handles[a].OutMag * len);
            var c2 = pb - handles[b].InDir * (handles[b].InMag * len);
            result.Edges.Add(new PlannedEdge { P0 = pa, C1 = c1, C2 = c2, P3 = pb, IsLine = false });
        }

        return result.Edges.Count > 0 ? result : null;
    }

    /// <summary>CurveConfig.ProximityGuard: forces an edge straight when its own length is
    /// short, or (opt-in) when some other node in the glyph sits close to either endpoint.</summary>
    private static bool IsForcedStraight(
        Vec2 pa, Vec2 pb, double edgeLen, CurveProximityGuardConfig guard, List<Vec2> allPoints)
    {
        if (!guard.Enabled) return false;
        if (edgeLen < guard.MinEdgeLength) return true;

        if (guard.MinDistanceToOtherNodes > 0)
        {
            foreach (var p in allPoints)
            {
                bool isOwnEndpoint = (p - pa).Length < 1e-9 || (p - pb).Length < 1e-9;
                if (isOwnEndpoint) continue;

                if ((p - pa).Length < guard.MinDistanceToOtherNodes || (p - pb).Length < guard.MinDistanceToOtherNodes)
                    return true;
            }
        }

        return false;
    }

    private static NodeHandle MakeHandle(List<Vec2> pts, int i, bool closed, CurveConfig curve, DeterministicRandom rng)
    {
        Vec2 tangent = DerivedTangent(pts, i, closed);

        double DrawMagnitude() => Math.Clamp(curve.Tension.Resolve(rng), 0.0, 0.5);

        bool explicitNode = curve.Mode == CurveMode.Explicit
            || (curve.Mode == CurveMode.Hybrid && rng.NextBool(0.5));

        if (!explicitNode)
        {
            double m = DrawMagnitude();
            return new NodeHandle(tangent, tangent, m, m);
        }

        // Explicit: in and out handles deviate independently, so kinks are possible.
        double amplitude = curve.ExplicitJitterDegrees.Resolve(rng) * Math.PI / 180.0;
        Vec2 outDir = tangent.Rotated((rng.NextDouble() * 2 - 1) * amplitude);
        Vec2 inDir = tangent.Rotated((rng.NextDouble() * 2 - 1) * amplitude);
        double outMag = DrawMagnitude();
        double inMag = DrawMagnitude();
        return new NodeHandle(outDir, inDir, outMag, inMag);
    }

    /// <summary>Catmull-Rom style tangent: direction from the previous to the next node (open
    /// chain ends just use their single edge's direction).</summary>
    private static Vec2 DerivedTangent(List<Vec2> pts, int i, bool closed)
    {
        int n = pts.Count;
        bool hasPrev = closed || i > 0;
        bool hasNext = closed || i < n - 1;

        Vec2 prev = hasPrev ? pts[(i - 1 + n) % n] : pts[i];
        Vec2 next = hasNext ? pts[(i + 1) % n] : pts[i];

        Vec2 t = (next - prev).Normalized();
        if (t.Length < 0.5) // prev == next (a doubled-back chain): fall back to one side
            t = hasNext ? (next - pts[i]).Normalized() : (pts[i] - prev).Normalized();

        return t;
    }
}
