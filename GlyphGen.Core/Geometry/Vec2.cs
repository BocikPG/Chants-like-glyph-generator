namespace GlyphGen.Core.Geometry;

/// <summary>Minimal 2D vector used by curve planning and stroke construction (Skia-free).</summary>
public readonly struct Vec2
{
    public double X { get; }
    public double Y { get; }

    public Vec2(double x, double y)
    {
        X = x;
        Y = y;
    }

    public static Vec2 operator +(Vec2 a, Vec2 b) => new(a.X + b.X, a.Y + b.Y);
    public static Vec2 operator -(Vec2 a, Vec2 b) => new(a.X - b.X, a.Y - b.Y);
    public static Vec2 operator -(Vec2 a) => new(-a.X, -a.Y);
    public static Vec2 operator *(Vec2 a, double s) => new(a.X * s, a.Y * s);
    public static Vec2 operator *(double s, Vec2 a) => new(a.X * s, a.Y * s);

    public double Length => Math.Sqrt(X * X + Y * Y);
    public double Dot(Vec2 o) => X * o.X + Y * o.Y;
    public double Cross(Vec2 o) => X * o.Y - Y * o.X;

    /// <summary>Left-hand normal (rotated +90 degrees in maths orientation).</summary>
    public Vec2 Perp => new(-Y, X);

    public Vec2 Normalized()
    {
        double l = Length;
        return l < 1e-12 ? new Vec2(0, 0) : new Vec2(X / l, Y / l);
    }

    public Vec2 Rotated(double radians)
    {
        double c = Math.Cos(radians), s = Math.Sin(radians);
        return new Vec2(X * c - Y * s, X * s + Y * c);
    }

    public static Vec2 Lerp(Vec2 a, Vec2 b, double t) => a + (b - a) * t;

    public static Vec2 Bezier(Vec2 p0, Vec2 c1, Vec2 c2, Vec2 p3, double t)
    {
        double u = 1 - t;
        return p0 * (u * u * u) + c1 * (3 * u * u * t) + c2 * (3 * u * t * t) + p3 * (t * t * t);
    }

    public override string ToString() => $"({X:0.###},{Y:0.###})";
}
