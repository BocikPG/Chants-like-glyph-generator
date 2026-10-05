namespace GlyphGen.Core.Grid;

/// <summary>A single lattice coordinate on the glyph grid.</summary>
public readonly struct GridPoint : IEquatable<GridPoint>
{
    public int X { get; }
    public int Y { get; }

    public GridPoint(int x, int y)
    {
        X = x;
        Y = y;
    }

    public GridPoint Translate(int dx, int dy) => new(X + dx, Y + dy);

    /// <summary>
    /// Rotates this point around a center by a lattice-preserving angle (0/90/180/270,
    /// and 45 only when the caller has already validated diagonal connections are enabled
    /// for the language — otherwise a 45-degree rotation will not land on integer coordinates).
    /// </summary>
    public GridPoint RotateAround(GridPoint center, int degrees)
    {
        int dx = X - center.X;
        int dy = Y - center.Y;

        (int rx, int ry) = degrees switch
        {
            0 => (dx, dy),
            90 => (-dy, dx),
            180 => (-dx, -dy),
            270 => (dy, -dx),
            _ => throw new NotSupportedException(
                $"Rotation of {degrees} degrees is not lattice-safe for integer-only points. " +
                "45-degree rotation requires the finer-grained handling noted in design (diagonal-enabled languages only)."),
        };

        return new GridPoint(center.X + rx, center.Y + ry);
    }

    public bool Equals(GridPoint other) => X == other.X && Y == other.Y;
    public override bool Equals(object? obj) => obj is GridPoint p && Equals(p);
    public override int GetHashCode() => HashCode.Combine(X, Y);
    public override string ToString() => $"({X},{Y})";

    public static bool operator ==(GridPoint a, GridPoint b) => a.Equals(b);
    public static bool operator !=(GridPoint a, GridPoint b) => !a.Equals(b);
}
