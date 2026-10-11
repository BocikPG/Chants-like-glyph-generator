using GlyphGen.Rendering.Geometry;

namespace GlyphGen.Rendering.Curves;

/// <summary>One drawable edge in GRID coordinates: a straight line or a cubic bezier.</summary>
public sealed class PlannedEdge
{
    public Vec2 P0 { get; init; }
    public Vec2 C1 { get; init; }
    public Vec2 C2 { get; init; }
    public Vec2 P3 { get; init; }
    public bool IsLine { get; init; }

    public Vec2 At(double t) => IsLine ? Vec2.Lerp(P0, P3, t) : Vec2.Bezier(P0, C1, C2, P3, t);
}

public enum PlannedChainKind
{
    /// <summary>An ordinary stroke.</summary>
    Stroke,

    /// <summary>A circle (4 beziers, closed). Never flattened to lines.</summary>
    Circle,

    /// <summary>A dot: one degenerate edge at the point, drawn as a disc.</summary>
    Dot,
}

public sealed class PlannedChain
{
    public PlannedChainKind Kind { get; set; } = PlannedChainKind.Stroke;
    public List<PlannedEdge> Edges { get; } = new();
    public bool Closed { get; set; }
    public string? MotifId { get; set; }
}

/// <summary>A group of chains drawn in one colour: the glyph's own chains, or one combination component.</summary>
public sealed class PlannedLayer
{
    public string Label { get; init; } = "";
    public List<PlannedChain> Chains { get; } = new();
}

public sealed class PlannedGlyph
{
    public string Name { get; init; } = "";
    public List<PlannedLayer> Layers { get; } = new();

    /// <summary>Bounding box of the actual curves (sampled), not just the nodes.</summary>
    public bool TryGetBounds(out Bounds bounds)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        bool any = false;

        foreach (var layer in Layers)
            foreach (var chain in layer.Chains)
                foreach (var edge in chain.Edges)
                    for (int k = 0; k <= 16; k++)
                    {
                        var p = edge.At(k / 16.0);
                        minX = Math.Min(minX, p.X);
                        minY = Math.Min(minY, p.Y);
                        maxX = Math.Max(maxX, p.X);
                        maxY = Math.Max(maxY, p.Y);
                        any = true;
                    }

        bounds = any ? new Bounds(minX, minY, maxX, maxY) : default;
        return any;
    }
}
