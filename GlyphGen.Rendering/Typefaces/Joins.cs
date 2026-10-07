using GlyphGen.Core.Config;
using GlyphGen.Rendering.Geometry;

namespace GlyphGen.Rendering.Typefaces;

/// <summary>
/// One stroke leaving a node: where the node is, the direction the stroke runs AWAY from it, the
/// half-widths of the stroke right there (Left is the +Perp side of Dir) and how far it runs before
/// the next node (so an inner fillet never reaches past it).
/// </summary>
public readonly record struct JoinArm(Vec2 Point, Vec2 Dir, WidthSample Width, double Reach, int Stroke);

/// <summary>
/// Collects the arms of every stroke in one layer, so joins can be built per NODE once all strokes are
/// known (a bend inside one stroke and a meeting of several strokes are the same problem).
/// </summary>
public sealed class JoinSink
{
    public List<JoinArm> Arms { get; } = new();

    /// <summary>What a stroke draws at a free tip (cap, ball, blob), held back until it is known whether
    /// the tip ends at a junction that wants it trimmed.</summary>
    public List<(Vec2 Point, List<Vec2[]> Polygons)> Tips { get; } = new();
}

public static class JoinResolver
{
    /// <summary>How far past the centre-line a join can reach, as a multiple of the half-width (used
    /// only for framing padding). Real grid corners are at most ~2.6 for a 45 degree turn.</summary>
    public static double ExtentFactor(JoinsConfig joins)
    {
        double factor = 1.0;
        void Consider(JoinSideConfig? side)
        {
            if (side is { Style: JoinStyle.Miter })
                factor = Math.Max(factor, Math.Min(Math.Max(1.0, side.MiterLimit), 2.62));
        }

        foreach (var context in new[] { joins.Corner, joins.Junction })
        {
            Consider(context.Outer);
            foreach (var rule in context.ByAngle) Consider(rule.Outer);
        }

        return factor;
    }

    /// <summary>The outer and inner join for a node whose strokes meet at angleDegrees (180 = straight).</summary>
    public static (JoinSideConfig Outer, JoinSideConfig Inner) Resolve(JoinContextConfig context, double angleDegrees)
    {
        foreach (var rule in context.ByAngle)
        {
            if (angleDegrees < rule.Angle.Min || angleDegrees > rule.Angle.Max) continue;
            return (rule.Outer ?? context.Outer, rule.Inner ?? context.Inner);
        }

        return (context.Outer, context.Inner);
    }
}

/// <summary>
/// Builds the ink that joins strokes at a node. The strokes' bodies are already drawn; around a node the
/// strokes leave in several directions, and between each pair of neighbouring directions there is a gap:
///   gap &gt; 180 degrees: the convex OUTSIDE of a bend - the outer join adds a point / bevel / arc;
///   gap &lt; 180 degrees: a NOTCH between two strokes - the inner join may fill it with a chamfer / fillet;
///   gap = 180 degrees: a straight run, nothing to do.
/// A two-stroke bend has one gap of each kind; a T, a Y or a crossing only has notches.
/// </summary>
public static class JoinBuilder
{
    private const double Eps = 1e-6;
    private const double TwoPi = 2 * Math.PI;

    private static (long, long) KeyOf(Vec2 p) => ((long)Math.Round(p.X * 100), (long)Math.Round(p.Y * 100));

    /// <summary>Builds the joins for every node in the sink, then adds the tip decorations (caps, balls,
    /// blobs) - except those at a junction when junction.trimCaps is on. A node with exactly two arms from
    /// one stroke is a corner; anything else where arms meet is a junction.</summary>
    public static void BuildNodes(JoinSink sink, JoinsConfig joins, List<Vec2[]> output)
    {
        var nodes = new Dictionary<(long, long), List<JoinArm>>();
        foreach (var arm in sink.Arms)
        {
            var key = KeyOf(arm.Point);
            if (!nodes.TryGetValue(key, out var list)) nodes[key] = list = new List<JoinArm>();
            list.Add(arm);
        }

        foreach (var group in nodes.Values)
        {
            if (group.Count < 2) continue;
            bool corner = group.Count == 2 && group[0].Stroke == group[1].Stroke;
            BuildNode(group, corner ? joins.Corner : joins.Junction, corner ? null : joins.Junction, outerOnly: false, output);
        }

        foreach (var (point, polygons) in sink.Tips)
        {
            bool atJunction = nodes.TryGetValue(KeyOf(point), out var here) && here.Count >= 2;
            if (atJunction && joins.Junction.TrimCaps) continue;
            output.AddRange(polygons);
        }
    }

    /// <summary>A bend at a point inside a stroke that is not a grid node (a smooth curve sample):
    /// outer side only, as only real nodes have notches worth filling.</summary>
    public static void BuildSmoothCorner(JoinArm back, JoinArm forward, JoinsConfig joins, List<Vec2[]> output) =>
        BuildNode(new List<JoinArm> { back, forward }, joins.Corner, null, outerOnly: true, output);

    private static void BuildNode(
        List<JoinArm> arms, JoinContextConfig context, JunctionJoinConfig? junction, bool outerOnly, List<Vec2[]> output)
    {
        int n = arms.Count;
        if (n < 2) return;

        var sorted = arms.OrderBy(a => Math.Atan2(a.Dir.Y, a.Dir.X)).ToList();

        for (int i = 0; i < n; i++)
        {
            var a = sorted[i];
            var b = sorted[(i + 1) % n];

            double gap = i < n - 1
                ? Math.Atan2(b.Dir.Y, b.Dir.X) - Math.Atan2(a.Dir.Y, a.Dir.X)
                : Math.Atan2(b.Dir.Y, b.Dir.X) - Math.Atan2(a.Dir.Y, a.Dir.X) + TwoPi;

            double gapDeg = gap * 180.0 / Math.PI;
            if (gapDeg < 0.5 || Math.Abs(gapDeg - 180.0) < 0.5) continue; // overlapping directions / straight on

            double angle = Math.Min(gapDeg, 360.0 - gapDeg);
            var (outer, inner) = JoinResolver.Resolve(context, angle);

            if (gapDeg > 180.0)
            {
                AddOuter(a, b, gap, outer, output);
            }
            else if (!outerOnly)
            {
                // Two strokes meeting almost straight, with nothing between them on this side: one
                // long chamfer makes the edge straight (the gap is adjacent, so no stroke is in it).
                double off = 180.0 - gapDeg;
                if (junction is { StraightenDegrees: > 0 } && off <= junction.StraightenDegrees)
                    AddInner(a, b, gap, new JoinSideConfig { Style = JoinStyle.Bevel, Size = junction.StraightenLength }, output);
                else
                    AddInner(a, b, gap, inner, output);
            }
        }
    }

    // The gap runs counter-clockwise (increasing angle) from arm a to arm b, so it is bounded by
    // a's +Perp edge and b's -Perp edge.
    private static void AddOuter(JoinArm a, JoinArm b, double gap, JoinSideConfig side, List<Vec2[]> output)
    {
        if (side.Style == JoinStyle.Overlap) return;

        Vec2 p = a.Point;
        Vec2 pa = p + a.Dir.Perp * a.Width.Left;
        Vec2 pb = p - b.Dir.Perp * b.Width.Right;

        switch (side.Style)
        {
            case JoinStyle.Bevel:
                StrokeOutlineBuilder.Emit(output, new[] { p, pa, pb });
                break;

            case JoinStyle.Miter:
            {
                double cross = a.Dir.Cross(b.Dir);
                double baseHalf = Math.Max(a.Width.Left, b.Width.Right);
                if (Math.Abs(cross) > Eps && baseHalf > 1e-9)
                {
                    double t = (pb - pa).Cross(b.Dir) / cross;
                    Vec2 m = pa + a.Dir * t;

                    // The point must lie on the outside (the same side of the node as the two edges)
                    // and within the miter limit; otherwise it falls back to a bevel.
                    bool outside = (m - p).Dot((pa - p) + (pb - p)) > 0;
                    if (outside && (m - p).Length / baseHalf <= side.MiterLimit)
                    {
                        StrokeOutlineBuilder.Emit(output, new[] { p, pa, m, pb });
                        break;
                    }
                }

                StrokeOutlineBuilder.Emit(output, new[] { p, pa, pb });
                break;
            }

            case JoinStyle.Round:
            {
                double a0 = Math.Atan2(pa.Y - p.Y, pa.X - p.X);
                double delta = gap - Math.PI; // the arc sweeps the part of the gap beyond a straight line
                double r0 = (pa - p).Length, r1 = (pb - p).Length;
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 12)));

                var fan = new List<Vec2>(steps + 2) { p };
                for (int j = 0; j <= steps; j++)
                {
                    double t = j / (double)steps;
                    double ang = a0 + delta * t;
                    double r = r0 + (r1 - r0) * t;
                    fan.Add(p + new Vec2(Math.Cos(ang), Math.Sin(ang)) * r);
                }
                StrokeOutlineBuilder.Emit(output, fan.ToArray());
                break;
            }
        }
    }

    private static void AddInner(JoinArm a, JoinArm b, double gap, JoinSideConfig side, List<Vec2[]> output)
    {
        // Miter and Overlap both leave the notch as the two bodies make it.
        if (side.Style is JoinStyle.Overlap or JoinStyle.Miter || side.Size <= 0) return;

        Vec2 p = a.Point;
        Vec2 pa = p + a.Dir.Perp * a.Width.Left;
        Vec2 pb = p - b.Dir.Perp * b.Width.Right;

        // The notch corner M: where a's +Perp edge meets b's -Perp edge.
        double cross = a.Dir.Cross(b.Dir);
        if (Math.Abs(cross) < Eps) return;
        double t = (pb - pa).Cross(b.Dir) / cross;
        Vec2 m = pa + a.Dir * t;
        double s = (m - pb).Dot(b.Dir);
        if (t <= 0 || s <= 0) return;

        double half = Math.Min(a.Width.Left, b.Width.Right);
        if (half <= 1e-9) return;

        double tanHalf = Math.Tan(gap / 2);
        double wanted = side.Style == JoinStyle.Round ? side.Size * half / tanHalf : side.Size * half;

        // Never run past the neighbouring node along either edge.
        double room = Math.Min(a.Reach - t, b.Reach - s);
        double d = Math.Min(wanted, room);
        if (d <= 1e-6) return;

        Vec2 c1 = m + a.Dir * d;
        Vec2 c2 = m + b.Dir * d;

        if (side.Style == JoinStyle.Bevel)
        {
            StrokeOutlineBuilder.Emit(output, new[] { m, c1, c2 });
            return;
        }

        // Round: a fillet of radius d * tan(gap/2), tangent to both edges at c1 and c2.
        double radius = d * tanHalf;
        Vec2 bisector = (a.Dir + b.Dir).Normalized();
        Vec2 center = m + bisector * (radius / Math.Sin(gap / 2));

        double s0 = Math.Atan2(c1.Y - center.Y, c1.X - center.X);
        double s1 = Math.Atan2(c2.Y - center.Y, c2.X - center.X);
        double delta = s1 - s0;
        while (delta > Math.PI) delta -= TwoPi;
        while (delta <= -Math.PI) delta += TwoPi;

        int steps = Math.Max(2, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 12)));
        var poly = new List<Vec2>(steps + 2) { m };
        for (int j = 0; j <= steps; j++)
        {
            double ang = s0 + delta * (j / (double)steps);
            poly.Add(center + new Vec2(Math.Cos(ang), Math.Sin(ang)) * radius);
        }
        StrokeOutlineBuilder.Emit(output, poly.ToArray());
    }
}
