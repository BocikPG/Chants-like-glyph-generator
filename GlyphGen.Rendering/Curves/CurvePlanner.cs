using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using GlyphGen.Core.Grid;
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
    // Circle through 4 beziers: control distance for a quarter arc.
    private const double Kappa = 0.5522847498307936;

    public static PlannedGlyph Plan(Glyph glyph, CurveConfig curve, ulong seed)
    {
        var rng = new DeterministicRandom(seed);

        // Gathered once for the proximity guard's MinDistanceToOtherNodes check, which needs
        // visibility across every chain/component in the glyph, not just the one being planned.
        var allPoints = curve.ProximityGuard.Enabled && curve.ProximityGuard.MinDistanceToOtherNodes > 0
            ? GlyphGeometryUtils.GetAllNodes(glyph).Select(p => new Vec2(p.X, p.Y)).Distinct().ToList()
            : new List<Vec2>();

        var planned = new PlannedGlyph { Name = glyph.Name };

        if (glyph.Chains.Count > 0)
            planned.Layers.Add(PlanLayer("base", glyph.Chains, curve, allPoints, rng));

        foreach (var component in glyph.Components)
            planned.Layers.Add(PlanLayer(component.SourceGlyphName, component.TransformedChains, curve, allPoints, rng));

        return planned;
    }

    private static PlannedLayer PlanLayer(
        string label,
        IEnumerable<SubSymbolChain> chains,
        CurveConfig curve,
        List<Vec2> allPoints,
        DeterministicRandom rng)
    {
        var layer = new PlannedLayer { Label = label };
        foreach (var chain in chains)
            layer.Chains.AddRange(PlanChain(chain, curve, allPoints, rng));
        return layer;
    }

    /// <summary>One stroke chain becomes one planned chain per unbroken run of straight edges; every
    /// circle or dot in it becomes a planned chain of its own (no random draws).</summary>
    private static List<PlannedChain> PlanChain(
        SubSymbolChain chain, CurveConfig curve, List<Vec2> allPoints, DeterministicRandom rng)
    {
        var result = new List<PlannedChain>();
        var run = new List<GridEdge>();

        bool hasCircle = chain.Edges.Any(e => e.IsCircle);
        bool closed = chain.IsClosedLoop && !hasCircle && chain.Nodes.Count >= 3;

        void Flush()
        {
            if (run.Count == 0) return;
            var planned = PlanRun(run, closed, chain.MotifId, curve, allPoints, rng, chain.ShapeTag);
            if (planned is not null) result.Add(planned);
            run.Clear();
        }

        foreach (var edge in chain.Edges)
        {
            if (!edge.IsCircle) { run.Add(edge); continue; }
            Flush();
            result.Add(PlanCircle(edge, chain.MotifId, chain.ShapeTag));
        }
        Flush();

        return result;
    }

    private static PlannedChain PlanCircle(GridEdge edge, string? motifId, string? shapeTag)
    {
        var a = new Vec2(edge.From.X, edge.From.Y);
        var b = new Vec2(edge.To.X, edge.To.Y);
        var planned = new PlannedChain { MotifId = motifId, ShapeTag = shapeTag };

        if (edge.IsDot)
        {
            planned.Kind = PlannedChainKind.Dot;
            planned.Edges.Add(new PlannedEdge { P0 = a, C1 = a, C2 = a, P3 = a, IsLine = true });
            return planned;
        }

        planned.Kind = PlannedChainKind.Circle;
        planned.Closed = true;

        var centre = new Vec2((a.X + b.X) / 2, (a.Y + b.Y) / 2);
        double r = (b - a).Length / 2;
        double start = Math.Atan2(a.Y - centre.Y, a.X - centre.X);

        Vec2 Pt(double angle) => new(centre.X + r * Math.Cos(angle), centre.Y + r * Math.Sin(angle));
        Vec2 Tan(double angle) => new(-Math.Sin(angle), Math.Cos(angle));

        for (int i = 0; i < 4; i++)
        {
            double t0 = start + i * Math.PI / 2, t1 = t0 + Math.PI / 2;
            var p0 = Pt(t0);
            var p3 = Pt(t1);
            planned.Edges.Add(new PlannedEdge
            {
                P0 = p0, P3 = p3, IsLine = false,
                C1 = p0 + Tan(t0) * (Kappa * r),
                C2 = p3 - Tan(t1) * (Kappa * r),
            });
        }

        return planned;
    }

    private static PlannedChain? PlanRun(
        List<GridEdge> edges, bool closed, string? motifId, CurveConfig curve,
        List<Vec2> allPoints, DeterministicRandom rng, string? shapeTag = null)
    {
        var pts = new List<Vec2>();
        var styles = new List<EdgeCurveConfig>();

        void AddPoint(Vec2 v) { if (pts.Count == 0 || (v - pts[^1]).Length > 1e-9) pts.Add(v); }

        AddPoint(new Vec2(edges[0].From.X, edges[0].From.Y));
        foreach (var e in edges)
        {
            int before = pts.Count;
            AddPoint(new Vec2(e.To.X, e.To.Y));
            if (pts.Count > before) styles.Add(e.Curve ?? curve.Edge); // one style per drawn segment
        }

        // A closed chain's last edge returns to the start: that point is the first one again.
        if (closed && pts.Count > 1 && (pts[^1] - pts[0]).Length < 1e-9) pts.RemoveAt(pts.Count - 1);

        if (pts.Count < 2) return null;
        closed = closed && pts.Count >= 3;

        int n = pts.Count;
        var result = new PlannedChain { Closed = closed, MotifId = motifId, ShapeTag = shapeTag };
        int edgeCount = closed ? n : n - 1;

        for (int i = 0; i < edgeCount && i < styles.Count; i++)
        {
            int a = i, b = (i + 1) % n;
            Vec2 pa = pts[a], pb = pts[b];
            double len = (pb - pa).Length;
            if (len < 1e-9) continue;

            var style = styles[i];

            // Everything below is drawn for every edge, even one that ends up straight - keeps RNG
            // consumption identical whatever the chance or the proximity guard decide, so changing
            // one edge's settings doesn't shift unrelated downstream draws.
            bool rolledStraight = rng.NextBool(1.0 - Math.Clamp(style.ChanceToBeCurved / 100.0, 0.0, 1.0));
            var (outDir, outMag, inDir, inMag) = MakeHandles(pts, a, b, closed, style, rng);
            bool forcedStraight = IsForcedStraight(pa, pb, len, curve.ProximityGuard, allPoints);

            if ((rolledStraight || forcedStraight) && !curve.ForceAllEdgesCurved)
            {
                result.Edges.Add(new PlannedEdge { P0 = pa, P3 = pb, C1 = pa, C2 = pb, IsLine = true });
                continue;
            }

            var c1 = pa + outDir * (outMag * len);
            var c2 = pb - inDir * (inMag * len);
            result.Edges.Add(new PlannedEdge { P0 = pa, C1 = c1, C2 = c2, P3 = pb, IsLine = false });
        }

        return result.Edges.Count > 0 ? result : null;
    }

    /// <summary>CurveConfig.ProximityGuard: forces an edge straight when its own length is
    /// short, or (opt-in) when some other node in the glyph sits close to either endpoint.</summary>
    internal static bool IsForcedStraight(
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

    /// <summary>The two bezier handles of edge a-&gt;b: the out-handle at a and the in-handle at b, per this
    /// edge's own style. Derived: along the smooth tangent of each end node. Explicit: each deviates
    /// independently, so kinks are possible. Hybrid: one or the other for the whole edge.</summary>
    private static (Vec2 OutDir, double OutMag, Vec2 InDir, double InMag) MakeHandles(
        List<Vec2> pts, int a, int b, bool closed, EdgeCurveConfig style, DeterministicRandom rng)
    {
        Vec2 tangentA = DerivedTangent(pts, a, closed);
        Vec2 tangentB = DerivedTangent(pts, b, closed);

        double DrawMagnitude() => Math.Clamp(style.Tension.Resolve(rng), 0.0, 0.5);

        bool isExplicit = style.Mode == CurveMode.Explicit
            || (style.Mode == CurveMode.Hybrid && rng.NextBool(0.5));

        if (!isExplicit)
        {
            double outMag = DrawMagnitude();
            double inMag = DrawMagnitude();
            return (tangentA, outMag, tangentB, inMag);
        }

        double ampA = style.ExplicitJitterDegrees.Resolve(rng) * Math.PI / 180.0;
        Vec2 outDir = tangentA.Rotated((rng.NextDouble() * 2 - 1) * ampA);
        double outM = DrawMagnitude();
        double ampB = style.ExplicitJitterDegrees.Resolve(rng) * Math.PI / 180.0;
        Vec2 inDir = tangentB.Rotated((rng.NextDouble() * 2 - 1) * ampB);
        double inM = DrawMagnitude();
        return (outDir, outM, inDir, inM);
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
