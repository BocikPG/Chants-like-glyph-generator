using GlyphGen.Core.Generation;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Geometry;

namespace GlyphGen.Rendering.Curves;

/// <summary>
/// Stage 1 of rendering: turns a glyph's chains into a drawable plan (each edge a line or a cubic bezier). The
/// curves were decided while the glyph was generated and are stored on the edges (GridEdge.Bend), so this only
/// converts them - no random draws happen here, and what is drawn is exactly what the collision checks saw.
/// </summary>
public static class CurvePlanner
{
    // Circle through 4 beziers: control distance for a quarter arc.
    private const double Kappa = 0.5522847498307936;

    public static PlannedGlyph Plan(Glyph glyph)
    {
        var planned = new PlannedGlyph { Name = glyph.Name };

        if (glyph.Chains.Count > 0)
            planned.Layers.Add(PlanLayer("base", glyph.Chains));

        foreach (var component in glyph.Components)
            planned.Layers.Add(PlanLayer(component.SourceGlyphName, component.TransformedChains));

        return planned;
    }

    private static PlannedLayer PlanLayer(string label, IEnumerable<SubSymbolChain> chains)
    {
        var layer = new PlannedLayer { Label = label };
        foreach (var chain in chains) layer.Chains.AddRange(PlanChain(chain));
        return layer;
    }

    /// <summary>One stroke chain becomes one planned chain per unbroken run of straight-line edges; every
    /// circle or dot in it becomes a planned chain of its own.</summary>
    private static List<PlannedChain> PlanChain(SubSymbolChain chain)
    {
        var result = new List<PlannedChain>();
        var run = new List<GridEdge>();

        bool hasCircle = chain.Edges.Any(e => e.IsCircle);
        bool closed = chain.IsClosedLoop && !hasCircle && chain.Nodes.Count >= 3;

        void Flush()
        {
            if (run.Count == 0) return;
            var planned = PlanRun(run, closed, chain.MotifId, chain.ShapeTag);
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

        if (edge.IsArc)
        {
            // Part of the circle: open, in pieces of at most a quarter turn each.
            planned.Closed = false;
            var (acx, acy, ar) = edge.CircleGeometry;
            double first = edge.ArcStartDegrees * Math.PI / 180.0, sweep = edge.Arc!.Value.Sweep * Math.PI / 180.0;
            int pieces = Math.Max(1, (int)Math.Ceiling(sweep / (Math.PI / 2) - 1e-9));
            double piece = sweep / pieces, handle = 4.0 / 3.0 * Math.Tan(piece / 4) * ar;
            Vec2 At(double angle) => new(acx + ar * Math.Cos(angle), acy + ar * Math.Sin(angle));
            Vec2 Along(double angle) => new(-Math.Sin(angle), Math.Cos(angle));
            for (int i = 0; i < pieces; i++)
            {
                double t0 = first + i * piece, t1 = t0 + piece;
                var q0 = At(t0); var q3 = At(t1);
                planned.Edges.Add(new PlannedEdge { P0 = q0, P3 = q3, IsLine = false, C1 = q0 + Along(t0) * handle, C2 = q3 - Along(t1) * handle });
            }
            return planned;
        }

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

    private static PlannedChain? PlanRun(List<GridEdge> edges, bool closed, string? motifId, string? shapeTag)
    {
        var result = new PlannedChain { Closed = closed && edges.Count >= 3, MotifId = motifId, ShapeTag = shapeTag };

        foreach (var e in edges)
        {
            var pa = new Vec2(e.From.X, e.From.Y);
            var pb = new Vec2(e.To.X, e.To.Y);
            if ((pb - pa).Length < 1e-9) continue;

            if (e.Bend is null || e.Bend.IsNone)
            {
                result.Edges.Add(new PlannedEdge { P0 = pa, P3 = pb, C1 = pa, C2 = pb, IsLine = true });
                continue;
            }

            var (c1x, c1y, c2x, c2y) = e.Bend.Controls(e.From, e.To);
            result.Edges.Add(new PlannedEdge { P0 = pa, C1 = new Vec2(c1x, c1y), C2 = new Vec2(c2x, c2y), P3 = pb, IsLine = false });
        }

        return result.Edges.Count > 0 ? result : null;
    }
}
