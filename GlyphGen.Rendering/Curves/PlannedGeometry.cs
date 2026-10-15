using GlyphGen.Core.Geometry;

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

    /// <summary>"shapeId#instance" of the shape that drew the chain (debug colouring); null if unknown.</summary>
    public string? ShapeTag { get; set; }
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

    /// <summary>The same glyph turned clockwise (y points down) by the angle around a centre; beziers turn exactly.</summary>
    public PlannedGlyph Rotated(double degrees, Vec2 center)
    {
        double rad = degrees * Math.PI / 180.0, cos = Math.Cos(rad), sin = Math.Sin(rad);
        Vec2 R(Vec2 v) => new(center.X + cos * (v.X - center.X) - sin * (v.Y - center.Y),
                              center.Y + sin * (v.X - center.X) + cos * (v.Y - center.Y));

        var result = new PlannedGlyph { Name = Name };
        foreach (var layer in Layers)
        {
            var turned = new PlannedLayer { Label = layer.Label };
            foreach (var chain in layer.Chains)
            {
                var copy = new PlannedChain { Kind = chain.Kind, Closed = chain.Closed, MotifId = chain.MotifId, ShapeTag = chain.ShapeTag };
                foreach (var e in chain.Edges)
                    copy.Edges.Add(new PlannedEdge { P0 = R(e.P0), C1 = R(e.C1), C2 = R(e.C2), P3 = R(e.P3), IsLine = e.IsLine });
                turned.Chains.Add(copy);
            }
            result.Layers.Add(turned);
        }

        return result;
    }

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
