namespace GlyphGen.Core.Grid;

/// <summary>
/// How one straight lattice edge is bent: a cubic bezier whose two handles are stored in the edge's own frame, so the
/// shape survives every similarity transform of the edge's end points for free. The frame has the edge's direction
/// (from -> to, unit) as its first axis and that direction turned +90 degrees as its second, and handle vectors are
/// measured in edge lengths: handle 1 = A1 * u + B1 * v starting at the first point, handle 2 = A2 * u + B2 * v
/// starting at the second. Translating, turning or uniformly scaling an edge keeps the bend as it is; a reflection
/// needs <see cref="Mirrored"/>; swapping the two end points needs <see cref="Reversed"/>.
/// Values are rounded to 1e-6 so that two machines agree on them.
/// </summary>
public sealed class EdgeBend : IEquatable<EdgeBend>
{
    /// <summary>Planned, and straight: no curve at all.</summary>
    public static readonly EdgeBend None = new(0, 0, 0, 0);

    public double A1 { get; }
    public double B1 { get; }
    public double A2 { get; }
    public double B2 { get; }

    public EdgeBend(double a1, double b1, double a2, double b2)
    {
        A1 = Round(a1); B1 = Round(b1); A2 = Round(a2); B2 = Round(b2);
    }

    private static double Round(double v) => Math.Round(v, 6) + 0.0; // + 0.0 turns -0 into 0

    /// <summary>True when both handles are zero: the bezier is the straight segment itself.</summary>
    public bool IsNone => A1 == 0 && B1 == 0 && A2 == 0 && B2 == 0;

    /// <summary>The bend of the mirror image of the edge (any reflection of the plane).</summary>
    public EdgeBend Mirrored() => new(A1, -B1, A2, -B2);

    /// <summary>The same curve described from the other end (the edge's two end points swapped).</summary>
    public EdgeBend Reversed() => new(-A2, -B2, -A1, -B1);

    /// <summary>The bend that gives the control points c1, c2 for an edge from p0 to p3 (grid coordinates).</summary>
    public static EdgeBend FromControlPoints(double p0x, double p0y, double p3x, double p3y, double c1x, double c1y, double c2x, double c2y)
    {
        double dx = p3x - p0x, dy = p3y - p0y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-12) return None;
        double ux = dx / len, uy = dy / len, vx = -uy, vy = ux;
        return new EdgeBend(
            ((c1x - p0x) * ux + (c1y - p0y) * uy) / len, ((c1x - p0x) * vx + (c1y - p0y) * vy) / len,
            ((c2x - p3x) * ux + (c2y - p3y) * uy) / len, ((c2x - p3x) * vx + (c2y - p3y) * vy) / len);
    }

    /// <summary>The two control points, in grid coordinates, of an edge between these end points.</summary>
    public (double C1X, double C1Y, double C2X, double C2Y) Controls(GridPoint from, GridPoint to)
    {
        double dx = to.X - from.X, dy = to.Y - from.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        if (len < 1e-12) return (from.X, from.Y, to.X, to.Y);
        double ux = dx / len, uy = dy / len, vx = -uy, vy = ux;
        return (
            from.X + len * (A1 * ux + B1 * vx), from.Y + len * (A1 * uy + B1 * vy),
            to.X + len * (A2 * ux + B2 * vx), to.Y + len * (A2 * uy + B2 * vy));
    }

    /// <summary>The curve as points from the first end point to the second (both included), segments + 1 of them.</summary>
    public (double X, double Y)[] Flatten(GridPoint from, GridPoint to, int segments)
    {
        var (c1x, c1y, c2x, c2y) = Controls(from, to);
        var points = new (double X, double Y)[segments + 1];
        for (int i = 0; i <= segments; i++)
        {
            double t = i / (double)segments, u = 1 - t;
            double w0 = u * u * u, w1 = 3 * u * u * t, w2 = 3 * u * t * t, w3 = t * t * t;
            points[i] = (w0 * from.X + w1 * c1x + w2 * c2x + w3 * to.X, w0 * from.Y + w1 * c1y + w2 * c2y + w3 * to.Y);
        }
        return points;
    }

    public bool Equals(EdgeBend? other) => other is not null && A1 == other.A1 && B1 == other.B1 && A2 == other.A2 && B2 == other.B2;
    public override bool Equals(object? obj) => Equals(obj as EdgeBend);
    public override int GetHashCode() => HashCode.Combine(A1, B1, A2, B2);
    public override string ToString() => IsNone ? "straight" : $"bend({A1},{B1} | {A2},{B2})";
}
