using GlyphGen.Core.Config;

namespace GlyphGen.Core.Grid;

public enum GridEdgeKind : byte
{
    /// <summary>A straight connection From -> To.</summary>
    Line,

    /// <summary>A circle whose DIAMETER runs From -> To (From == To is a dot).</summary>
    Circle,
}


/// <summary>
/// A straight connection between two lattice points, possibly spanning several units
/// (per a ConnectionRuleConfig's MinLength/MaxLength). Nodes the segment passes over are
/// NOT considered occupied by this edge — per design, pass-through nodes stay free for other
/// sub-symbols to use independently; only actual geometric crossings are disallowed.
/// </summary>
public readonly struct GridEdge
{
    public GridPoint From { get; }
    public GridPoint To { get; }
    public GridEdgeKind Kind { get; }

    /// <summary>How the rule that made this edge wants it bent; null = the language's curve.edge.</summary>
    public EdgeCurveConfig? Curve { get; }

    /// <summary>The planned bend of a straight edge: null = not planned yet, <see cref="EdgeBend.None"/> = planned and
    /// straight. Circles and dots have none.</summary>
    public EdgeBend? Bend { get; }

    public GridEdge(GridPoint from, GridPoint to, GridEdgeKind kind = GridEdgeKind.Line, EdgeCurveConfig? curve = null, EdgeBend? bend = null)
    {
        From = from;
        To = to;
        Kind = kind;
        Curve = curve;
        Bend = kind == GridEdgeKind.Line ? bend : null;
    }

    /// <summary>True when the edge has a planned bend that really bends it.</summary>
    public bool IsCurved => Bend is { IsNone: false };

    public GridEdge WithBend(EdgeBend? bend) => new(From, To, Kind, Curve, bend);

    /// <summary>The mirror image of the edge's bend, for an edge that was reflected (WithPoints keeps the bend as it is).</summary>
    public GridEdge MirroredBend() => Bend is null ? this : WithBend(Bend.Mirrored());

    public bool IsCircle => Kind == GridEdgeKind.Circle;
    public bool IsDot => Kind == GridEdgeKind.Circle && From == To;

    /// <summary>The same edge with new end points (after a scale/rotate/translate), keeping kind, curve and bend. After a
    /// reflection also call <see cref="MirroredBend"/>.</summary>
    public GridEdge WithPoints(GridPoint from, GridPoint to) => new(from, to, Kind, Curve, Bend);

    /// <summary>Circle centre and radius (a Circle edge only).</summary>
    public (double Cx, double Cy, double R) CircleGeometry =>
        ((From.X + To.X) / 2.0, (From.Y + To.Y) / 2.0,
         Math.Sqrt((double)(To.X - From.X) * (To.X - From.X) + (double)(To.Y - From.Y) * (To.Y - From.Y)) / 2.0);

    /// <summary>The box the drawn edge covers: the end points for a line, the whole circle for a circle.</summary>
    public (double MinX, double MinY, double MaxX, double MaxY) Extent()
    {
        if (!IsCircle)
            return (Math.Min(From.X, To.X), Math.Min(From.Y, To.Y), Math.Max(From.X, To.X), Math.Max(From.Y, To.Y));
        var (cx, cy, r) = CircleGeometry;
        return (cx - r, cy - r, cx + r, cy + r);
    }

    /// <summary>
    /// Exact integer-geometry intersection test between two lattice segments. This is exact
    /// (no sampling/tolerance needed) because every endpoint of every edge is always an integer
    /// grid coordinate. Segments that only touch at a shared endpoint (chains connecting
    /// head-to-tail, or a closed loop's final edge back to its start) do NOT count as an
    /// intersection — only a genuine interior crossing, or an overlap extending beyond that
    /// single shared point, counts.
    /// </summary>
    public bool Intersects(GridEdge other)
    {
        if (IsCircle || other.IsCircle) return CircleIntersects(this, other);

        GridPoint p1 = From, q1 = To, p2 = other.From, q2 = other.To;

        bool sharesEndpoint = p1 == p2 || p1 == q2 || q1 == p2 || q1 == q2;

        long o1 = Orientation(p1, q1, p2);
        long o2 = Orientation(p1, q1, q2);
        long o3 = Orientation(p2, q2, p1);
        long o4 = Orientation(p2, q2, q1);

        // General case: the two segments cross at a single point determined by opposite
        // orientations on both sides.
        if (o1 != o2 && o3 != o4)
        {
            // If that single crossing point IS a shared endpoint, it's a touch, not a cross.
            return !sharesEndpoint;
        }

        // Collinear special cases: true only if one segment's endpoint lies on the other.
        bool collinearOverlap =
            (o1 == 0 && OnSegment(p1, p2, q1)) ||
            (o2 == 0 && OnSegment(p1, q2, q1)) ||
            (o3 == 0 && OnSegment(p2, p1, q2)) ||
            (o4 == 0 && OnSegment(p2, q1, q2));

        if (!collinearOverlap) return false;
        if (!sharesEndpoint) return true; // genuine overlap with no shared endpoint to explain it

        // Collinear AND sharing an endpoint: only a real intersection if they overlap by more
        // than that single shared point (e.g. one edge doubling back over another), never for
        // two edges that simply continue in the same direction from a shared node.
        return CollinearOverlapLength(p1, q1, p2, q2) > 0;
    }

    private const double Eps = 1e-7;

    /// <summary>
    /// Circle rules. A circle only "crosses" something when the curve itself is cut: a line with one end
    /// strictly inside and one strictly outside, a line passing through the inside without ending in it,
    /// or two circles whose outlines intersect at two points. Touching (at the lattice nodes the circle
    /// passes through), lines lying wholly inside or outside it, and nested or tangent circles are fine.
    /// A dot blocks nothing.
    /// </summary>
    private static bool CircleIntersects(GridEdge a, GridEdge b)
    {
        if (a.IsDot || b.IsDot) return false;
        if (a.IsCircle && b.IsCircle)
        {
            var (x1, y1, r1) = a.CircleGeometry;
            var (x2, y2, r2) = b.CircleGeometry;
            double d = Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));
            if (d < Eps && Math.Abs(r1 - r2) < Eps) return true; // the same outline twice
            return d < r1 + r2 - Eps && d > Math.Abs(r1 - r2) + Eps;
        }

        var circle = a.IsCircle ? a : b;
        var line = a.IsCircle ? b : a;
        var (cx, cy, r) = circle.CircleGeometry;

        double d1 = Dist(line.From.X, line.From.Y, cx, cy), d2 = Dist(line.To.X, line.To.Y, cx, cy);
        bool in1 = d1 < r - Eps, in2 = d2 < r - Eps, out1 = d1 > r + Eps, out2 = d2 > r + Eps;
        if ((in1 && out2) || (out1 && in2)) return true;
        if (in1 || in2) return false; // ends inside (or on) the circle: stays inside

        // Neither end is inside: does the segment still pass through the inside?
        double vx = line.To.X - line.From.X, vy = line.To.Y - line.From.Y;
        double len2 = vx * vx + vy * vy;
        double t = len2 < Eps ? 0 : Math.Clamp(((cx - line.From.X) * vx + (cy - line.From.Y) * vy) / len2, 0, 1);
        return Dist(line.From.X + t * vx, line.From.Y + t * vy, cx, cy) < r - Eps;
    }

    private static double Dist(double x1, double y1, double x2, double y2) =>
        Math.Sqrt((x1 - x2) * (x1 - x2) + (y1 - y2) * (y1 - y2));

    /// <summary>Twice the signed area of triangle (a, b, c); its sign gives orientation and
    /// zero means collinear. Doubling avoids fractional results while keeping everything in
    /// exact integer arithmetic.</summary>
    private static long Orientation(GridPoint a, GridPoint b, GridPoint c)
    {
        long val = (long)(b.Y - a.Y) * (c.X - b.X) - (long)(b.X - a.X) * (c.Y - b.Y);
        return val == 0 ? 0 : (val > 0 ? 1 : -1);
    }

    /// <summary>Given a, b, c are already known to be collinear, checks whether b lies within
    /// the bounding box of segment a-c (equivalent to lying on it, for collinear points).</summary>
    private static bool OnSegment(GridPoint a, GridPoint b, GridPoint c) =>
        b.X <= Math.Max(a.X, c.X) && b.X >= Math.Min(a.X, c.X) &&
        b.Y <= Math.Max(a.Y, c.Y) && b.Y >= Math.Min(a.Y, c.Y);

    /// <summary>For two collinear segments, the length of their overlap projected onto
    /// whichever axis has the larger spread (avoids a degenerate zero-length projection on
    /// purely horizontal/vertical lines). Zero means they touch at a point at most.</summary>
    private static long CollinearOverlapLength(GridPoint p1, GridPoint q1, GridPoint p2, GridPoint q2)
    {
        bool useX = Math.Abs(q1.X - p1.X) >= Math.Abs(q1.Y - p1.Y);

        long a1 = useX ? p1.X : p1.Y, b1 = useX ? q1.X : q1.Y;
        long a2 = useX ? p2.X : p2.Y, b2 = useX ? q2.X : q2.Y;

        long min1 = Math.Min(a1, b1), max1 = Math.Max(a1, b1);
        long min2 = Math.Min(a2, b2), max2 = Math.Max(a2, b2);

        return Math.Max(0, Math.Min(max1, max2) - Math.Max(min1, min2));
    }

    public override string ToString() => IsCircle ? $"circle {From} .. {To}" : $"{From} -> {To}";
}
