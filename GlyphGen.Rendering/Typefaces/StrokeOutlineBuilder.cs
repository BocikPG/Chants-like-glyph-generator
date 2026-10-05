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
    /// <summary>How far past the centre-line a join can reach, as a multiple of the half-width
    /// (used only for framing padding). Real grid corners are at most ~2.6 for a 45 degree turn.</summary>
    public static double JoinExtentFactor(JoinStyle join, double miterLimit) =>
        join == JoinStyle.Miter ? Math.Min(Math.Max(1.0, miterLimit), 2.62) : 1.0;

    public static void Build(
        StrokePath path,
        IReadOnlyList<WidthSample> segStart,
        IReadOnlyList<WidthSample> segEnd,
        JoinStyle join,
        double miterLimit,
        CapStyle cap,
        List<Vec2[]> output)
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

        for (int i = 1; i < segCount; i++)
            AddJoin(pts[i], dirs[i - 1], dirs[i], segEnd[i - 1], segStart[i], join, miterLimit, output);

        if (path.Closed)
        {
            if (segCount >= 2)
                AddJoin(pts[0], dirs[segCount - 1], dirs[0], segEnd[segCount - 1], segStart[0], join, miterLimit, output);
        }
        else
        {
            // Start cap: outward is -dir, which swaps which side counts as "left".
            var s = segStart[0];
            AddCap(pts[0], -dirs[0], new WidthSample(s.Right, s.Left), cap, output);
            AddCap(pts[^1], dirs[segCount - 1], segEnd[segCount - 1], cap, output);
        }
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

    /// <summary>
    /// Builds a real join (Miter/Bevel/Round/Overlap, per the typeface's own settings) between
    /// two directions meeting at a point - used by the renderer to patch a junction between two
    /// DIFFERENT chains sharing an endpoint, the same way an interior corner within one chain
    /// is already handled. dIn/dOut follow the same convention as an interior join: dIn points
    /// INTO p (the direction of travel arriving there), dOut points AWAY from p (the direction
    /// of travel leaving). For two chains that both simply END at p, treat one as "arriving"
    /// (its own tip direction) and the other as "leaving" by negating its tip direction - which
    /// reads as one continuous stroke bending from one chain into the other at that point.
    /// </summary>
    public static void AddExternalJoin(
        Vec2 p, Vec2 dIn, Vec2 dOut, double halfWidthA, double halfWidthB,
        JoinStyle join, double miterLimit, List<Vec2[]> output)
    {
        AddJoin(p, dIn, dOut, WidthSample.Uniform(halfWidthA), WidthSample.Uniform(halfWidthB), join, miterLimit, output);
    }

    private static void AddJoin(
        Vec2 p, Vec2 d0, Vec2 d1, WidthSample w0, WidthSample w1,
        JoinStyle join, double miterLimit, List<Vec2[]> output)
    {
        double cross = d0.Cross(d1);
        double dot = d0.Dot(d1);

        if (Math.Abs(cross) < 1e-6)
        {
            // Collinear continuation needs nothing; a full reversal only gets a cap for Round.
            if (dot < 0 && join == JoinStyle.Round)
                AddDisc(p, Math.Max(Math.Max(w0.Left, w0.Right), Math.Max(w1.Left, w1.Right)), output);
            return;
        }

        if (join == JoinStyle.Overlap) return; // plain overlapping rectangles, notch and all

        // The turn goes toward one side; the join fills the OTHER (outer) side.
        double s = cross > 0 ? -1.0 : 1.0;
        double h0 = s > 0 ? w0.Left : w0.Right;
        double h1 = s > 0 ? w1.Left : w1.Right;
        Vec2 a = p + d0.Perp * (s * h0);
        Vec2 b = p + d1.Perp * (s * h1);

        switch (join)
        {
            case JoinStyle.Bevel:
                Emit(output, new[] { p, a, b });
                break;

            case JoinStyle.Miter:
            {
                // Intersection of the two outer offset lines: a + d0*u == b + d1*v.
                double u = (b - a).Cross(d1) / cross;
                Vec2 m = a + d0 * u;
                double baseHalf = Math.Max(h0, h1);

                if (baseHalf > 1e-9 && (m - p).Length / baseHalf <= miterLimit)
                    Emit(output, new[] { p, a, m, b });
                else
                    Emit(output, new[] { p, a, b }); // too sharp: fall back to a bevel
                break;
            }

            case JoinStyle.Round:
            {
                double a0 = Math.Atan2(a.Y - p.Y, a.X - p.X);
                double a1 = Math.Atan2(b.Y - p.Y, b.X - p.X);
                double delta = a1 - a0;
                while (delta > Math.PI) delta -= 2 * Math.PI;
                while (delta <= -Math.PI) delta += 2 * Math.PI;

                double r0 = (a - p).Length, r1 = (b - p).Length;
                int steps = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 12)));

                var fan = new List<Vec2>(steps + 2) { p };
                for (int j = 0; j <= steps; j++)
                {
                    double t = j / (double)steps;
                    double ang = a0 + delta * t;
                    double r = r0 + (r1 - r0) * t;
                    fan.Add(p + new Vec2(Math.Cos(ang), Math.Sin(ang)) * r);
                }
                Emit(output, fan.ToArray());
                break;
            }
        }
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
    private static void Emit(List<Vec2[]> output, Vec2[] poly)
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
