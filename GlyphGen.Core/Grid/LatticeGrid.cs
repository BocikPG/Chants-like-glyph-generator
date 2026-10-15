namespace GlyphGen.Core.Grid;

/// <summary>The fixed-size lattice all glyphs for a run are generated on (default 25x25). A restricted view (see
/// <see cref="Restricted"/>) is a smaller box inside it that a shape must stay in: it behaves as the edge of the grid
/// for that shape's generation.</summary>
public sealed class LatticeGrid
{
    public int Width { get; }
    public int Height { get; }

    /// <summary>Inclusive node range a shape may use (the whole grid unless restricted).</summary>
    public int MinX { get; }
    public int MinY { get; }
    public int MaxX { get; }
    public int MaxY { get; }

    public LatticeGrid(int width, int height) : this(width, height, 0, 0, width - 1, height - 1) { }

    private LatticeGrid(int width, int height, int minX, int minY, int maxX, int maxY)
    {
        Width = width;
        Height = height;
        MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
    }

    /// <summary>The same grid with a smaller usable box (inclusive node coordinates, clipped to the grid).</summary>
    public LatticeGrid Restricted(int minX, int minY, int maxX, int maxY) =>
        new(Width, Height, Math.Max(MinX, minX), Math.Max(MinY, minY), Math.Min(MaxX, maxX), Math.Min(MaxY, maxY));

    public bool Contains(GridPoint p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY;

    /// <summary>True when a box of real coordinates (a circle's extent) lies inside the usable range.</summary>
    public bool ContainsBox(double minX, double minY, double maxX, double maxY) =>
        minX >= MinX - 1e-9 && minY >= MinY - 1e-9 && maxX <= MaxX + 1e-9 && maxY <= MaxY + 1e-9;
}
