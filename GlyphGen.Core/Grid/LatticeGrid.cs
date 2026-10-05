namespace GlyphGen.Core.Grid;

/// <summary>The fixed-size lattice all glyphs for a run are generated on (default 25x25).</summary>
public sealed class LatticeGrid
{
    public int Width { get; }
    public int Height { get; }

    public LatticeGrid(int width, int height)
    {
        Width = width;
        Height = height;
    }

    public bool Contains(GridPoint p) => p.X >= 0 && p.X < Width && p.Y >= 0 && p.Y < Height;
}
