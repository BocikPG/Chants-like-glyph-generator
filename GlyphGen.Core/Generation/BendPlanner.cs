using GlyphGen.Core.Config;
using GlyphGen.Core.Geometry;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Decides how the straight edges of a chain are bent, once the whole chain is known (a node's tangent depends on its
/// two neighbours, and a loop's closing edge changes the start). The result is stored on the edges
/// (<see cref="GridEdge.Bend"/>), so every later step - collision checks, copies, derivations, composites and the
/// renderer - sees the same curves. Edges that already have a bend are left as they are.
/// </summary>
public static class BendPlanner
{
    /// <summary>True when any unplanned edge of the chain could come out curved (otherwise planning is skipped entirely).</summary>
    public static bool AnyCurved(SubSymbolChain chain, CurveConfig curve) =>
        curve.ForceAllEdgesCurved
        || chain.Edges.Any(e => !e.IsCircle && e.Bend is null && (e.Curve ?? curve.Edge).ChanceToBeCurved > 0);

    /// <summary>Plans the edges whose bend is still null. With curved = false they are all planned as straight (no random draws).</summary>
    public static void Plan(SubSymbolChain chain, CurveConfig curve, DeterministicRandom rng, bool curved = true)
    {
        var edges = chain.Edges;
        if (edges.Count == 0) return;

        if (!curved)
        {
            for (int i = 0; i < edges.Count; i++)
                if (!edges[i].IsCircle && edges[i].Bend is null) edges[i] = edges[i].WithBend(EdgeBend.None);
            return;
        }

        bool hasCircle = edges.Any(e => e.IsCircle);
        bool closed = chain.IsClosedLoop && !hasCircle && chain.Nodes.Count >= 3;

        var run = new List<int>();
        for (int i = 0; i < edges.Count; i++)
        {
            if (!edges[i].IsCircle) { run.Add(i); continue; }
            PlanRun(edges, run, closed, curve, rng);
            run.Clear();
        }
        PlanRun(edges, run, closed, curve, rng);
    }

    private static void PlanRun(List<GridEdge> edges, List<int> run, bool closed, CurveConfig curve, DeterministicRandom rng)
    {
        if (run.Count == 0) return;

        var pts = new List<Vec2>();
        var segmentEdge = new List<int>(); // which chain edge each drawn segment is
        void AddPoint(Vec2 v) { if (pts.Count == 0 || (v - pts[^1]).Length > 1e-9) pts.Add(v); }

        AddPoint(new Vec2(edges[run[0]].From.X, edges[run[0]].From.Y));
        foreach (int index in run)
        {
            int before = pts.Count;
            AddPoint(new Vec2(edges[index].To.X, edges[index].To.Y));
            if (pts.Count > before) segmentEdge.Add(index);
        }

        // A closed chain's last edge returns to the start: that point is the first one again.
        if (closed && pts.Count > 1 && (pts[^1] - pts[0]).Length < 1e-9) pts.RemoveAt(pts.Count - 1);
        if (pts.Count < 2) return;
        closed = closed && pts.Count >= 3;

        int n = pts.Count;
        int segments = closed ? n : n - 1;

        for (int i = 0; i < segments && i < segmentEdge.Count; i++)
        {
            var edge = edges[segmentEdge[i]];
            if (edge.Bend is not null) continue; // already planned (a copy, a derived edge)

            int a = i, b = (i + 1) % n;
            Vec2 pa = pts[a], pb = pts[b];
            double len = (pb - pa).Length;
            if (len < 1e-9) continue;

            var style = edge.Curve ?? curve.Edge;

            // Everything below is drawn for every edge, even one that ends up straight: random draws stay the same
            // whatever the chance or the guard decide, so changing one edge's settings does not shift the others.
            bool rolledStraight = rng.NextBool(1.0 - Math.Clamp(style.ChanceToBeCurved / 100.0, 0.0, 1.0));
            var (outDir, outMag, inDir, inMag) = MakeHandles(pts, a, b, closed, style, rng);
            bool tooShort = curve.ProximityGuard.Enabled && len < curve.ProximityGuard.MinEdgeLength;

            if ((rolledStraight || tooShort) && !curve.ForceAllEdgesCurved)
            {
                edges[segmentEdge[i]] = edge.WithBend(EdgeBend.None);
                continue;
            }

            var c1 = pa + outDir * (outMag * len);
            var c2 = pb - inDir * (inMag * len);
            edges[segmentEdge[i]] = edge.WithBend(EdgeBend.FromControlPoints(pa.X, pa.Y, pb.X, pb.Y, c1.X, c1.Y, c2.X, c2.Y));
        }
    }

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

    /// <summary>Catmull-Rom style tangent: direction from the previous to the next node (open chain ends just use their single edge's direction).</summary>
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
