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

    /// <summary>An extra test every usable node must pass (placement.nodes), null = none. Only nodes are tested; the box limits
    /// still apply to circle outlines.</summary>
    public Func<GridPoint, bool>? NodeFilter { get; }

    public LatticeGrid(int width, int height) : this(width, height, 0, 0, width - 1, height - 1, null) { }

    private LatticeGrid(int width, int height, int minX, int minY, int maxX, int maxY, Func<GridPoint, bool>? filter)
    {
        Width = width;
        Height = height;
        MinX = minX; MinY = minY; MaxX = maxX; MaxY = maxY;
        NodeFilter = filter;
    }

    /// <summary>The same grid where only the nodes that pass <paramref name="filter"/> (as well as the existing filter) are usable.</summary>
    public LatticeGrid WithNodeFilter(Func<GridPoint, bool> filter)
    {
        var before = NodeFilter;
        return new(Width, Height, MinX, MinY, MaxX, MaxY, before is null ? filter : p => before(p) && filter(p));
    }

    private List<GridPoint>? _usable;

    /// <summary>Every usable node, in row order (only worth calling on a grid with a node filter).</summary>
    public IReadOnlyList<GridPoint> UsableNodes()
    {
        if (_usable is not null) return _usable;
        var list = new List<GridPoint>();
        for (int x = MinX; x <= MaxX; x++)
            for (int y = MinY; y <= MaxY; y++)
            {
                var p = new GridPoint(x, y);
                if (Contains(p)) list.Add(p);
            }
        return _usable = list;
    }

    /// <summary>The same grid with a smaller usable box (inclusive node coordinates, clipped to the grid).</summary>
    public LatticeGrid Restricted(int minX, int minY, int maxX, int maxY) =>
        new(Width, Height, Math.Max(MinX, minX), Math.Max(MinY, minY), Math.Min(MaxX, maxX), Math.Min(MaxY, maxY), NodeFilter);

    public bool Contains(GridPoint p) => p.X >= MinX && p.X <= MaxX && p.Y >= MinY && p.Y <= MaxY && (NodeFilter is null || NodeFilter(p));

    /// <summary>True when a box of real coordinates (a circle's extent) lies inside the usable range.</summary>
    public bool ContainsBox(double minX, double minY, double maxX, double maxY) =>
        minX >= MinX - 1e-9 && minY >= MinY - 1e-9 && maxX <= MaxX + 1e-9 && maxY <= MaxY + 1e-9;
}
