using GlyphGen.Core.Config;
using GlyphGen.Rendering.Geometry;

namespace GlyphGen.Rendering.Typefaces;

/// <summary>Half-widths (px) on the left and right of the centre-line at a point.</summary>
public readonly record struct WidthSample(double Left, double Right)
{
    public static WidthSample Uniform(double halfWidth) => new(Math.Max(0, halfWidth), Math.Max(0, halfWidth));
}

/// <summary>
/// The shared toolkit every typeface builds on. Ink is produced as many small CONVEX pieces
/// (one trapezoid per segment, plus a wedge/fan per join and cap), all oriented the same way.
/// Filling them together under a non-zero winding rule gives a clean union with no seams, and
/// avoids the holes/loops that a single offset outline develops on tight curves.
///
/// Widths are supplied per segment (start and end), each with independent left/right
/// half-widths, so styles can vary width smoothly along a stroke, per segment, or per side.
/// </summary>
public static class StrokeOutlineBuilder
{
    /// <summary>
    /// Emits the segment bodies and the end caps. Joins are NOT built here for grid nodes: when a sink is
    /// given, each node's arms (and the tips') are recorded there and the renderer builds the joins per
    /// node once every stroke is known (see JoinBuilder). Smooth points along a curve, and every point
    /// when no sink is given, get their outer join straight away.
    /// </summary>
    public static void Build(
        StrokePath path,
        IReadOnlyList<WidthSample> segStart,
        IReadOnlyList<WidthSample> segEnd,
        JoinsConfig joins,
        CapStyle cap,
        List<Vec2[]> output,
        JoinSink? sink = null,
        int strokeId = 0)
    {
        var pts = path.Points;
        int segCount = pts.Count - 1;
        if (segCount < 1) return;

        var dirs = new Vec2[segCount];
        for (int i = 0; i < segCount; i++)
            dirs[i] = (pts[i + 1] - pts[i]).Normalized();

        for (int i = 0; i < segCount; i++)
        {
            Vec2 n = dirs[i].Perp;
            Emit(output, new[]
            {
                pts[i] + n * segStart[i].Left,
                pts[i + 1] + n * segEnd[i].Left,
                pts[i + 1] - n * segEnd[i].Right,
                pts[i] - n * segStart[i].Right,
            });
        }

        var cum = path.CumulativeLengths();
        var nodes = path.NodeIndices;
        var nodeSet = new HashSet<int>(nodes);

        // Distance along the path from point i to the next / previous grid node.
        double ReachForward(int i)
        {
            foreach (int idx in nodes) if (idx > i) return cum[idx] - cum[i];
            return double.MaxValue;
        }

        double ReachBack(int i)
        {
            double reach = double.MaxValue;
            foreach (int idx in nodes) if (idx < i) reach = cum[i] - cum[idx];
            return reach;
        }

        // The two arms of the path at point i: one running back along segment i-1, one forward along segment i.
        // Widths are given relative to the arm's own direction, so the backward arm swaps left and right.
        JoinArm Back(int i, int segment, double reach) =>
            new(pts[i], -dirs[segment], new WidthSample(segEnd[segment].Right, segEnd[segment].Left), reach, strokeId);

        JoinArm Forward(int i, int segment, double reach) =>
            new(pts[i], dirs[segment], segStart[segment], reach, strokeId);

        for (int i = 1; i < segCount; i++)
        {
            var back = Back(i, i - 1, ReachBack(i));
            var forward = Forward(i, i, ReachForward(i));

            if (sink is not null && nodeSet.Contains(i))
            {
                sink.Arms.Add(back);
                sink.Arms.Add(forward);
            }
            else
            {
                JoinBuilder.BuildSmoothCorner(back, forward, joins, output);
            }
        }

        if (path.Closed)
        {
            if (segCount >= 2)
            {
                double seamBack = nodes.Count >= 2 ? cum[^1] - cum[nodes[^2]] : double.MaxValue;
                double seamForward = nodes.Count >= 2 ? cum[nodes[1]] : double.MaxValue;
                var back = Back(0, segCount - 1, seamBack);
                var forward = Forward(0, 0, seamForward);

                if (sink is not null)
                {
                    sink.Arms.Add(back);
                    sink.Arms.Add(forward);
                }
                else
                {
                    JoinBuilder.BuildSmoothCorner(back, forward, joins, output);
                }
            }
        }
        else
        {
            // Start cap: outward is -dir, which swaps which side counts as "left".
            var s = segStart[0];
            var startCap = new List<Vec2[]>();
            var endCap = new List<Vec2[]>();
            AddCap(pts[0], -dirs[0], new WidthSample(s.Right, s.Left), cap, startCap);
            AddCap(pts[^1], dirs[segCount - 1], segEnd[segCount - 1], cap, endCap);
            EmitTip(sink, pts[0], startCap, output);
            EmitTip(sink, pts[^1], endCap, output);

            // The tips take part in junctions with other strokes.
            sink?.Arms.Add(Forward(0, 0, ReachForward(0)));
            sink?.Arms.Add(Back(segCount, segCount - 1, ReachBack(segCount)));
        }
    }

    /// <summary>Draws something at a free tip. With a sink it is held back, so a junction that wants its
    /// tips trimmed can drop it; without one it is drawn at once.</summary>
    public static void EmitTip(JoinSink? sink, Vec2 point, List<Vec2[]> polygons, List<Vec2[]> output)
    {
        if (polygons.Count == 0) return;
        if (sink is null) output.AddRange(polygons);
        else sink.Tips.Add((point, polygons));
    }

    /// <summary>A disc at a free tip (ball terminal, start blob), subject to junction trimming.</summary>
    public static void AddTipDisc(JoinSink? sink, Vec2 center, double radius, List<Vec2[]> output)
    {
        var disc = new List<Vec2[]>();
        AddDisc(center, radius, disc);
        EmitTip(sink, center, disc, output);
    }

    public static void AddDisc(Vec2 center, double radius, List<Vec2[]> output)
    {
        if (radius <= 1e-9) return;

        int steps = Math.Clamp((int)Math.Ceiling(radius * 1.5), 12, 48);
        var poly = new Vec2[steps];
        for (int i = 0; i < steps; i++)
        {
            double a = 2 * Math.PI * i / steps;
            poly[i] = center + new Vec2(Math.Cos(a), Math.Sin(a)) * radius;
        }
        Emit(output, poly);
    }

    private static void AddCap(Vec2 p, Vec2 outward, WidthSample w, CapStyle cap, List<Vec2[]> output)
    {
        if (cap == CapStyle.Butt) return;

        Vec2 n = outward.Perp;

        if (cap == CapStyle.Square)
        {
            double ext = Math.Max(w.Left, w.Right);
            Emit(output, new[]
            {
                p + n * w.Left,
                p + n * w.Left + outward * ext,
                p - n * w.Right + outward * ext,
                p - n * w.Right,
            });
            return;
        }

        // Round: half disc sweeping from the +n side, through outward, to the -n side.
        double phi0 = Math.Atan2(n.Y, n.X);
        const int steps = 12;
        var fan = new List<Vec2>(steps + 2) { p };
        for (int j = 0; j <= steps; j++)
        {
            double t = j / (double)steps;
            double ang = phi0 - Math.PI * t;
            double r = w.Left + (w.Right - w.Left) * t;
            fan.Add(p + new Vec2(Math.Cos(ang), Math.Sin(ang)) * r);
        }
        Emit(output, fan.ToArray());
    }

    /// <summary>Adds a polygon with a consistent orientation (needed for a clean non-zero
    /// winding union), skipping degenerate ones.</summary>
    internal static void Emit(List<Vec2[]> output, Vec2[] poly)
    {
        double area = 0;
        for (int i = 0; i < poly.Length; i++)
        {
            Vec2 a = poly[i], b = poly[(i + 1) % poly.Length];
            area += a.X * b.Y - b.X * a.Y;
        }

        if (Math.Abs(area) < 1e-9) return;
        if (area < 0) Array.Reverse(poly);
        output.Add(poly);
    }
}
