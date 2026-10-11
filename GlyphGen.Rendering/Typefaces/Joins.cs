using GlyphGen.Core.Config;
using GlyphGen.Rendering.Geometry;

namespace GlyphGen.Rendering.Typefaces;

/// <summary>
/// One stroke leaving a node: where the node is, the direction the stroke runs AWAY from it, and the
/// half-widths of the stroke right there (Left is the +Perp side of Dir). For arms at grid nodes it also
/// carries the stroke's two real edge outlines from the node to the next node - Plus on the +Perp side,
/// Minus on the other - so an inner join can follow the edges even where the stroke curves away.
/// </summary>
public readonly record struct JoinArm(
    Vec2 Point, Vec2 Dir, WidthSample Width, int Stroke, Vec2[]? Plus = null, Vec2[]? Minus = null)
{
    /// <summary>An arm that runs perfectly straight for the given length (used for tests and simple cases).</summary>
    public static JoinArm Straight(Vec2 point, Vec2 dir, WidthSample width, double length, int stroke)
    {
        var perp = dir.Perp;
        return new JoinArm(point, dir, width, stroke,
            new[] { point + perp * width.Left, point + dir * length + perp * width.Left },
            new[] { point - perp * width.Right, point + dir * length - perp * width.Right });
    }
}

/// <summary>What a polygon is, for the debug "parts" view. Anything not tagged is a stroke body.</summary>
public enum InkKind { Cap, Corner, Junction }

/// <summary>
/// Collects the arms of every stroke in one layer, so joins can be built per NODE once all strokes are
/// known (a bend inside one stroke and a meeting of several strokes are the same problem).
/// </summary>
public sealed class JoinSink
{
    public List<JoinArm> Arms { get; } = new();

    /// <summary>Which polygons are caps / corner joins / junction joins (by reference). Used only by the
    /// debug parts view; polygons that are not in here are stroke bodies.</summary>
    public Dictionary<Vec2[], InkKind> Kinds { get; } = new(ReferenceEqualityComparer.Instance);

    internal void Tag(List<Vec2[]> output, int from, InkKind kind)
    {
        for (int i = from; i < output.Count; i++) Kinds[output[i]] = kind;
    }

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
            int before = output.Count;
            BuildNode(group, corner ? joins.Corner : joins.Junction, corner ? null : joins.Junction, outerOnly: false, output);
            sink.Tag(output, before, corner ? InkKind.Corner : InkKind.Junction);
        }

        foreach (var (point, polygons) in sink.Tips)
        {
            bool atJunction = nodes.TryGetValue(KeyOf(point), out var here) && here.Count >= 2;
            if (atJunction && joins.Junction.TrimCaps) continue;
            int before = output.Count;
            output.AddRange(polygons);
            sink.Tag(output, before, InkKind.Cap);
        }
    }

    /// <summary>A bend at a point inside a stroke that is not a grid node (a smooth curve sample):
    /// outer side only, as only real nodes have notches worth filling.</summary>
    public static void BuildSmoothCorner(JoinArm back, JoinArm forward, JoinsConfig joins, List<Vec2[]> output, JoinSink? sink = null)
    {
        int before = output.Count;
        BuildNode(new List<JoinArm> { back, forward }, joins.Corner, null, outerOnly: true, output);
        sink?.Tag(output, before, InkKind.Corner);
    }

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
                // Nearly straight on the convex side: extend the edges along one line instead of a join.
                bool straightened = junction is { StraightenOuterDegrees: > 0 }
                    && gapDeg - 180.0 <= junction.StraightenOuterDegrees
                    && AddStraightOuter(a, b, junction.StraightenLength, output);
                if (!straightened) AddOuter(a, b, gap, outer, output);
            }
            else if (!outerOnly)
            {
                // Two strokes meeting almost straight, with nothing between them on this side: one
                // long chamfer makes the edge straight (the gap is adjacent, so no stroke is in it).
                double off = 180.0 - gapDeg;
                if (junction is { StraightenInnerDegrees: > 0 } && off <= junction.StraightenInnerDegrees)
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

    /// <summary>
    /// Straightens the convex side of a nearly straight pair. The two outer edges meet at a corner K; a line
    /// through K along the average of the two directions lies just outside both edges, so the thin wedges
    /// between it and each edge are filled (up to `length` half-widths from K). Returns false if the arms
    /// do not form such a corner, so the normal outer join is used instead.
    /// </summary>
    private static bool AddStraightOuter(JoinArm a, JoinArm b, double length, List<Vec2[]> output)
    {
        var ea = a.Plus;
        var eb = b.Minus;
        if (ea is null || eb is null || ea.Length < 2 || eb.Length < 2 || length <= 0) return false;

        double cross = a.Dir.Cross(b.Dir);
        if (Math.Abs(cross) < 1e-9) return false;

        // K: where the lines of the two outer edges meet, behind the start of both outlines.
        double ta = (eb[0] - ea[0]).Cross(b.Dir) / cross;
        Vec2 k = ea[0] + a.Dir * ta;
        double tb = (k - eb[0]).Dot(b.Dir);
        if (ta > 1e-9 || tb > 1e-9) return false;

        double half = Math.Max(a.Width.Left, b.Width.Right);
        double d = length * half;
        if (d <= 1e-6) return false;

        Vec2 u = (a.Dir - b.Dir).Normalized(); // the straight line, pointing towards arm a
        var ca = Cumulative(ea);
        var cb = Cumulative(eb);

        bool any = false;
        any |= AddStraightWedge(k, u, ea, ca, d + ta, output);
        any |= AddStraightWedge(k, -u, eb, cb, d + tb, output);
        return any;
    }

    // The wedge between the line from K (direction u) and one edge, out to arc length s along that edge.
    private static bool AddStraightWedge(Vec2 k, Vec2 u, Vec2[] edge, double[] cum, double s, List<Vec2[]> output)
    {
        s = Math.Min(s, cum[^1]);
        if (s <= 1e-6) return false;

        Vec2 far = PointAt(edge, cum, s);
        double along = (far - k).Dot(u);
        if (along <= 1e-6) return false;

        Vec2 onLine = k + u * along; // foot of the edge point on the line: the closing step is square to it

        var poly = new List<Vec2> { k, onLine, far };
        AddVerticesBetween(poly, edge, cum, 0, s, forward: false);
        poly.Add(edge[0]);
        StrokeOutlineBuilder.Emit(output, poly.ToArray());
        return true;
    }

    private static void AddInner(JoinArm a, JoinArm b, double gap, JoinSideConfig side, List<Vec2[]> output)
    {
        // Miter and Overlap both leave the notch as the two bodies make it.
        if (side.Style is JoinStyle.Overlap or JoinStyle.Miter || side.Size <= 0) return;

        // The notch is bounded by a's +Perp edge and b's -Perp edge. Both are followed as the real
        // outlines, so the fill meets the borders even where a stroke curves away from its tangent.
        var ea = a.Plus;
        var eb = b.Minus;
        if (ea is null || eb is null || ea.Length < 2 || eb.Length < 2) return;

        var ca = Cumulative(ea);
        var cb = Cumulative(eb);
        if (!FirstCrossing(ea, ca, eb, cb, out double sa, out double sb, out Vec2 m)) return;

        double half = Math.Min(a.Width.Left, b.Width.Right);
        if (half <= 1e-9) return;

        // The opening of the notch where the edges actually meet.
        Vec2 ta = TangentAt(ea, ca, sa);
        Vec2 tb = TangentAt(eb, cb, sb);
        double local = Math.Acos(Math.Clamp(ta.Dot(tb), -1.0, 1.0));
        if (local > Math.PI - 0.02) return;
        local = Math.Max(local, 0.02);

        double wanted = side.Style == JoinStyle.Round ? side.Size * half / Math.Tan(local / 2) : side.Size * half;

        // Never run past the neighbouring node along either edge (the outlines end there).
        double room = Math.Min(ca[^1] - sa, cb[^1] - sb);
        double d = Math.Min(wanted, room);
        if (d <= 1e-6) return;

        Vec2 c1 = PointAt(ea, ca, sa + d);
        Vec2 c2 = PointAt(eb, cb, sb + d);

        // The filled region: M, along a's edge to c1, across to c2, back along b's edge to M.
        var poly = new List<Vec2> { m };
        AddVerticesBetween(poly, ea, ca, sa, sa + d, forward: true);
        poly.Add(c1);

        double phi = 0;
        if (side.Style == JoinStyle.Round)
        {
            // A fillet leaves c1 heading back towards M and arrives at c2 heading away from it: a cubic
            // with those tangents, sized like a circular arc through both points. For straight edges this
            // is the exact fillet of radius size x half-width.
            Vec2 t1 = TangentAt(ea, ca, sa + d);
            Vec2 t2 = TangentAt(eb, cb, sb + d);
            phi = Math.Acos(Math.Clamp((-t1).Dot(t2), -1.0, 1.0));
            double chord = (c2 - c1).Length;

            if (phi > 0.05 && chord > 1e-6)
            {
                double r = chord / (2 * Math.Sin(phi / 2));
                double k = 4.0 / 3.0 * Math.Tan(phi / 4) * r;
                Vec2 p1 = c1 - t1 * k;
                Vec2 p2 = c2 - t2 * k;

                int steps = Math.Max(3, (int)Math.Ceiling(phi / (Math.PI / 12)));
                for (int j = 1; j < steps; j++)
                {
                    double t = j / (double)steps, u = 1 - t;
                    poly.Add(c1 * (u * u * u) + p1 * (3 * u * u * t) + p2 * (3 * u * t * t) + c2 * (t * t * t));
                }
            }
        }

        poly.Add(c2);
        AddVerticesBetween(poly, eb, cb, sb, sb + d, forward: false);
        StrokeOutlineBuilder.Emit(output, poly.ToArray());
    }

    // ---- polyline helpers (edge outlines) ---------------------------------------------------------

    private static double[] Cumulative(Vec2[] poly)
    {
        var cum = new double[poly.Length];
        for (int i = 1; i < poly.Length; i++) cum[i] = cum[i - 1] + (poly[i] - poly[i - 1]).Length;
        return cum;
    }

    private static int SegmentAt(double[] cum, double s)
    {
        int last = cum.Length - 2;
        for (int i = 0; i <= last; i++)
            if (s < cum[i + 1]) return i;
        return last;
    }

    private static Vec2 PointAt(Vec2[] poly, double[] cum, double s)
    {
        s = Math.Clamp(s, 0, cum[^1]);
        int i = SegmentAt(cum, s);
        double len = cum[i + 1] - cum[i];
        double t = len > 1e-12 ? (s - cum[i]) / len : 0;
        return poly[i] + (poly[i + 1] - poly[i]) * t;
    }

    private static Vec2 TangentAt(Vec2[] poly, double[] cum, double s)
    {
        s = Math.Clamp(s, 0, cum[^1]);
        int i = SegmentAt(cum, s);
        var d = poly[i + 1] - poly[i];
        return d.Length > 1e-12 ? d.Normalized() : new Vec2(1, 0);
    }

    /// <summary>The polyline's own vertices strictly between arc lengths s0 and s1, in either direction.</summary>
    private static void AddVerticesBetween(List<Vec2> output, Vec2[] poly, double[] cum, double s0, double s1, bool forward)
    {
        if (forward)
        {
            for (int i = 0; i < poly.Length; i++)
                if (cum[i] > s0 + 1e-9 && cum[i] < s1 - 1e-9) output.Add(poly[i]);
        }
        else
        {
            for (int i = poly.Length - 1; i >= 0; i--)
                if (cum[i] > s0 + 1e-9 && cum[i] < s1 - 1e-9) output.Add(poly[i]);
        }
    }

    /// <summary>The crossing of the two outlines nearest the node (smallest combined distance along both).</summary>
    private static bool FirstCrossing(
        Vec2[] pa, double[] ca, Vec2[] pb, double[] cb, out double sa, out double sb, out Vec2 point)
    {
        sa = sb = 0;
        point = default;
        double best = double.MaxValue;
        bool found = false;

        for (int i = 0; i < pa.Length - 1; i++)
        {
            Vec2 r = pa[i + 1] - pa[i];
            for (int j = 0; j < pb.Length - 1; j++)
            {
                Vec2 u = pb[j + 1] - pb[j];
                double denom = r.Cross(u);
                if (Math.Abs(denom) < 1e-12) continue;

                Vec2 qp = pb[j] - pa[i];
                double t = qp.Cross(u) / denom;
                double w = qp.Cross(r) / denom;
                if (t < -1e-9 || t > 1 + 1e-9 || w < -1e-9 || w > 1 + 1e-9) continue;

                double x = ca[i] + t * r.Length;
                double y = cb[j] + w * u.Length;
                if (x + y < best)
                {
                    best = x + y;
                    sa = x;
                    sb = y;
                    point = pa[i] + r * t;
                    found = true;
                }
            }
        }

        return found;
    }
}
