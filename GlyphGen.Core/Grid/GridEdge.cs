using GlyphGen.Core.Config;

namespace GlyphGen.Core.Grid;

public enum GridEdgeKind : byte
{
    /// <summary>A straight connection From -> To.</summary>
    Line,

    /// <summary>A circle whose DIAMETER runs From -> To (From == To is a dot). With an <see cref="ArcSpan"/> only that part of
    /// the circle is drawn.</summary>
    Circle,
}

/// <summary>The drawn part of a circle edge: it starts RelStart degrees clockwise (on screen, y down) from the direction of the
/// diameter From -> To and runs Sweep degrees clockwise. Relative to the diameter, so rotating the edge keeps it right; a
/// reflection flips it (see <see cref="GridEdge.MirroredArc"/>).</summary>
public readonly record struct ArcSpan(int RelStart, int Sweep);


/// <summary>An extra node of a straight edge: T = fraction of the way from From to To, Offset = sideways distance (lattice units, positive
/// to the left of the From-to-To direction).</summary>
public readonly record struct LineNode(double T, double Offset);

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

    /// <summary>Circle edges only: the part of the circle that is drawn; null = all of it.</summary>
    public ArcSpan? Arc { get; }

    /// <summary>Circle edges only: the rule's extra-node settings (null = none).</summary>
    public OutlineNodesConfig? Outline { get; }

    /// <summary>Straight edges only: extra nodes lying on the edge (connection rule <c>split</c> with mode ExtraNodes), as fractions of the way
    /// from From to To plus a sideways offset (0 = exactly on the edge; split snap Nearest uses small offsets). The edge itself stays one edge; the nodes count for spacing, start.onExistingNode and NodesOnly crossings like a
    /// circle's outline nodes. Kept as fractions so that moving, rotating, mirroring or scaling the edge carries them along.</summary>
    public IReadOnlyList<LineNode>? LineNodes { get; }

    public GridEdge(GridPoint from, GridPoint to, GridEdgeKind kind = GridEdgeKind.Line, EdgeCurveConfig? curve = null, EdgeBend? bend = null,
        ArcSpan? arc = null, OutlineNodesConfig? outline = null, IReadOnlyList<LineNode>? lineNodes = null)
    {
        LineNodes = kind == GridEdgeKind.Line && from != to && lineNodes is { Count: > 0 } ? lineNodes : null;
        Arc = kind == GridEdgeKind.Circle && from != to ? arc : null;
        Outline = kind == GridEdgeKind.Circle && from != to ? outline : null;
        From = from;
        To = to;
        Kind = kind;
        Curve = curve;
        Bend = kind == GridEdgeKind.Line ? bend : null;
    }

    /// <summary>True when the edge has a planned bend that really bends it.</summary>
    public bool IsCurved => Bend is { IsNone: false };

    public GridEdge WithBend(EdgeBend? bend) => new(From, To, Kind, Curve, bend, Arc, Outline, LineNodes);

    /// <summary>The mirror image of the edge's bend, for an edge that was reflected (WithPoints keeps the bend as it is).</summary>
    public GridEdge MirroredBend() => Bend is null ? this : WithBend(Bend.Mirrored());

    public bool IsCircle => Kind == GridEdgeKind.Circle;

    /// <summary>True for a circle edge that draws only part of the circle.</summary>
    public bool IsArc => Arc is not null;

    /// <summary>The same arc after a reflection: it covers the mirrored angles, measured the other way round.</summary>
    public GridEdge MirroredArc() => Arc is not { } a
        ? (LineNodes is { } marks ? WithLineNodes(marks.Select(m => new LineNode(m.T, -m.Offset)).ToList()) : this) // a reflection puts sideways offsets on the other side
        : new GridEdge(From, To, Kind, Curve, Bend, new ArcSpan(((-(a.RelStart + a.Sweep)) % 360 + 360) % 360, a.Sweep), Outline, LineNodes);

    /// <summary>Direction of the diameter From -> To in degrees (0 = right, clockwise on screen).</summary>
    private double DiameterAngle => Math.Atan2(To.Y - From.Y, To.X - From.X) * 180.0 / Math.PI;

    /// <summary>Where the drawn arc starts, in degrees on the circle (0 = right, clockwise on screen).</summary>
    public double ArcStartDegrees => DiameterAngle + (Arc?.RelStart ?? 0);

    /// <summary>The point of the circle at an angle in degrees.</summary>
    public (double X, double Y) CirclePoint(double degrees)
    {
        var (cx, cy, r) = CircleGeometry;
        double t = degrees * Math.PI / 180.0;
        return (cx + r * Math.Cos(t), cy + r * Math.Sin(t));
    }

    /// <summary>True when the angle (degrees) is on the drawn part (always for a full circle).</summary>
    public bool CoversAngle(double degrees)
    {
        if (Arc is not { } a) return true;
        double offset = (((degrees - ArcStartDegrees) % 360) + 360) % 360;
        return offset <= a.Sweep + 1e-6;
    }

    /// <summary>The ends of the edge that are really drawn. For a line or a full circle that is From and To; an arc draws only part of the
    /// circle, so the far end of its diameter is left out unless the arc runs over it, and the arc's own two ends are added when they
    /// fall exactly on lattice points.</summary>
    public IEnumerable<GridPoint> DrawnEnds()
    {
        if (!IsArc)
        {
            yield return From;
            if (To != From) yield return To;
            yield break;
        }

        double along = DiameterAngle;
        // Seen from the middle of the circle, To lies in the direction of the diameter and From opposite to it.
        if (CoversAngle(along + 180.0)) yield return From;
        if (CoversAngle(along)) yield return To;
        foreach (double angle in new[] { ArcStartDegrees, ArcStartDegrees + Arc!.Value.Sweep })
        {
            var (px, py) = CirclePoint(angle);
            var nearest = new GridPoint((int)Math.Round(px), (int)Math.Round(py));
            if (Math.Abs(px - nearest.X) < 1e-6 && Math.Abs(py - nearest.Y) < 1e-6 && nearest != From && nearest != To) yield return nearest;
        }
    }

    /// <summary>The lattice points that count as extra nodes of a circle or arc (see OutlineNodesConfig); empty for anything else.
    /// Nodes of the edge itself (From, To) are not repeated.</summary>
    public List<GridPoint> ExtraNodes()
    {
        var result = new List<GridPoint>();
        if (LineNodes is { } marks)
        {
            // Only points that really are lattice points count (an edge that was nudged or scaled afterwards may no longer reach them).
            double dx = To.X - From.X, dy = To.Y - From.Y, len = Math.Sqrt(dx * dx + dy * dy);
            foreach (var mark in marks)
            {
                double px = From.X + mark.T * dx - mark.Offset * dy / len, py = From.Y + mark.T * dy + mark.Offset * dx / len;
                var p = new GridPoint((int)Math.Round(px), (int)Math.Round(py));
                if (Math.Abs(px - p.X) < 1e-6 && Math.Abs(py - p.Y) < 1e-6 && p != From && p != To && !result.Contains(p)) result.Add(p);
            }
            return result;
        }

        if (!IsCircle || IsDot || Outline is not { Mode: not OutlineNodeMode.None } spec) return result;

        var from = From; var to = To;
        void Add(GridPoint p) { if (p != from && p != to && !result.Contains(p)) result.Add(p); }
        var (cx, cy, r) = CircleGeometry;

        if (spec.Mode == OutlineNodeMode.AtAngles)
        {
            foreach (int angle in AngleSets.Angles(spec.Angles))
            {
                if (!CoversAngle(angle)) continue;
                var (px, py) = CirclePoint(angle);
                var nearest = new GridPoint((int)Math.Round(px), (int)Math.Round(py));
                double d = Math.Sqrt((px - nearest.X) * (px - nearest.X) + (py - nearest.Y) * (py - nearest.Y));
                bool take = spec.Snap switch
                {
                    AngleNodeSnap.Nearest => true,
                    AngleNodeSnap.Exact => d <= 1e-6,
                    _ => d <= spec.Tolerance + 1e-9,
                };
                if (take) Add(nearest);
            }
            return result;
        }

        var (minX, minY, maxX, maxY) = Extent();
        int x0 = (int)Math.Floor(minX - spec.Tolerance), x1 = (int)Math.Ceiling(maxX + spec.Tolerance);
        int y0 = (int)Math.Floor(minY - spec.Tolerance), y1 = (int)Math.Ceiling(maxY + spec.Tolerance);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                double dist = Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy));
                double angle = Math.Atan2(y - cy, x - cx) * 180.0 / Math.PI;
                double away = CoversAngle(angle)
                    ? Math.Abs(dist - r)
                    : Math.Min(Dist(x, y, CirclePoint(ArcStartDegrees)), Dist(x, y, CirclePoint(ArcStartDegrees + Arc!.Value.Sweep)));
                if (away <= spec.Tolerance + 1e-9) Add(new GridPoint(x, y));
            }
        return result;

        static double Dist(double x, double y, (double X, double Y) q) => Math.Sqrt((x - q.X) * (x - q.X) + (y - q.Y) * (y - q.Y));
    }
    public bool IsDot => Kind == GridEdgeKind.Circle && From == To;

    /// <summary>The same edge with new end points (after a scale/rotate/translate), keeping kind, curve and bend. After a
    /// reflection also call <see cref="MirroredBend"/>.</summary>
    public GridEdge WithPoints(GridPoint from, GridPoint to) => new(from, to, Kind, Curve, Bend, Arc, Outline, LineNodes);

    /// <summary>The same edge with extra nodes at these fractions of its way (see <see cref="LineNodes"/>); null or empty clears them.</summary>
    public GridEdge WithLineNodes(IReadOnlyList<LineNode>? nodes) => new(From, To, Kind, Curve, Bend, Arc, Outline, nodes);

    /// <summary>The same edge without extra nodes.</summary>
    public GridEdge WithoutLineNodes() => WithLineNodes((IReadOnlyList<LineNode>?)null);

    /// <summary>The same, for nodes exactly on the edge (offset 0), given as fractions of its way.</summary>
    public GridEdge WithLineFractions(IReadOnlyList<double> fractions) => WithLineNodes(fractions.Select(t => new LineNode(t, 0)).ToList());

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
        if (Arc is not { } a) return (cx - r, cy - r, cx + r, cy + r);

        // The ends of the arc, plus the extreme points (right, down, left, up) it passes.
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Take((double X, double Y) p) { minX = Math.Min(minX, p.X); maxX = Math.Max(maxX, p.X); minY = Math.Min(minY, p.Y); maxY = Math.Max(maxY, p.Y); }
        Take(CirclePoint(ArcStartDegrees));
        Take(CirclePoint(ArcStartDegrees + a.Sweep));
        for (int angle = 0; angle < 360; angle += 90)
            if (CoversAngle(angle)) Take(CirclePoint(angle));
        return (minX, minY, maxX, maxY);
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
