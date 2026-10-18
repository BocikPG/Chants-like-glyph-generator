using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;

namespace GlyphGen.Core.Generation;

/// <summary>Turns a shape's placement.nodes into a node test, using what is already placed in the glyph (the node pool and the
/// box around other shapes are read from it when the shape is placed).</summary>
public static class NodeMaskBuilder
{
    public static Func<GridPoint, bool>? Build(NodeMaskConfig? config, GeometryContext geometry)
    {
        if (config is null) return null;
        var allow = (config.Allow ?? new()).Select(r => Region(r, geometry)).ToList();
        var forbid = (config.Forbid ?? new()).Select(r => Region(r, geometry)).ToList();
        if (allow.Count == 0 && forbid.Count == 0) return null;

        return p =>
        {
            if (allow.Count > 0 && !allow.Any(r => r(p))) return false;
            foreach (var r in forbid) if (r(p)) return false;
            return true;
        };
    }

    private static Func<GridPoint, bool> Region(NodeRegion region, GeometryContext geometry)
    {
        if (region.Points is { } points)
        {
            var set = new HashSet<GridPoint>(points);
            return set.Contains;
        }

        if (region.Box is { } box)
            return p => p.X >= box.MinX && p.X <= box.MaxX && p.Y >= box.MinY && p.Y <= box.MaxY;

        if (region.Pattern is { } pattern)
            return p => (pattern.X is null || pattern.X.Matches(p.X)) && (pattern.Y is null || pattern.Y.Matches(p.Y));

        if (region.NodesOfShapes is { } shapes)
        {
            var set = new HashSet<GridPoint>(geometry.NodesOfShapes(shapes));
            return set.Contains;
        }

        if (region.BoundsOfShapes is { } around)
        {
            var nodes = geometry.NodesOfShapes(around.Shapes).ToList();
            if (nodes.Count == 0) return _ => false;
            int minX = nodes.Min(n => n.X) - around.Margin, maxX = nodes.Max(n => n.X) + around.Margin;
            int minY = nodes.Min(n => n.Y) - around.Margin, maxY = nodes.Max(n => n.Y) + around.Margin;
            return p => p.X >= minX && p.X <= maxX && p.Y >= minY && p.Y <= maxY;
        }

        return _ => true;
    }
}
