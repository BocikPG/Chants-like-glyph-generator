using System.Text;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;

namespace GlyphGen.Core.Rendering;

/// <summary>
/// Placeholder console visualizer: draws every chain using straight lines only, best-effort
/// on non-45-degree diagonals (approximated via Bresenham stepping). This is purely for
/// structural sanity-checking while the generation pipeline is being built — real rendering
/// (stroke tapering, line-end shaping, bezier arcs, SkiaSharp/SVG output) is a separate,
/// later concern and does not need to match this visually.
/// </summary>
public static class AsciiGlyphRenderer
{
    public static string Render(Glyph glyph)
    {
        int width = glyph.GridWidth;
        int height = glyph.GridHeight;
        var canvas = new char[height, width];
        for (int y = 0; y < height; y++)
            for (int x = 0; x < width; x++)
                canvas[y, x] = ' ';

        void DrawEdge(GridEdge edge)
        {
            char c = PickChar(edge.From, edge.To);
            foreach (var (x, y) in WalkSegment(edge.From, edge.To))
            {
                if (x < 0 || x >= width || y < 0 || y >= height) continue;
                canvas[y, x] = c;
            }
        }

        foreach (var chain in glyph.Chains)
            foreach (var edge in chain.Edges)
                DrawEdge(edge);

        foreach (var component in glyph.Components)
            foreach (var chain in component.TransformedChains)
                foreach (var edge in chain.Edges)
                    DrawEdge(edge);

        // Nodes drawn last so joints stay visible even where multiple edges meet/overlap.
        void MarkNode(GridPoint p)
        {
            if (p.X >= 0 && p.X < width && p.Y >= 0 && p.Y < height)
                canvas[p.Y, p.X] = 'o';
        }

        foreach (var chain in glyph.Chains)
            foreach (var node in chain.Nodes)
                MarkNode(node);

        foreach (var component in glyph.Components)
            foreach (var chain in component.TransformedChains)
                foreach (var node in chain.Nodes)
                    MarkNode(node);

        var sb = new StringBuilder();
        sb.Append('[').Append(glyph.LanguageName).Append("] ").Append(glyph.Name).Append('\n');
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
                sb.Append(canvas[y, x]);
            sb.Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>Bresenham-style stepping between two lattice points, inclusive of both ends.
    /// Used purely for filling in the visual path of long connections on the ASCII canvas —
    /// has no bearing on which nodes count as "occupied" for generation purposes.</summary>
    private static IEnumerable<(int x, int y)> WalkSegment(GridPoint from, GridPoint to)
    {
        int x0 = from.X, y0 = from.Y, x1 = to.X, y1 = to.Y;
        int dx = Math.Abs(x1 - x0), sx = x0 < x1 ? 1 : -1;
        int dy = -Math.Abs(y1 - y0), sy = y0 < y1 ? 1 : -1;
        int err = dx + dy;

        while (true)
        {
            yield return (x0, y0);
            if (x0 == x1 && y0 == y1) yield break;
            int e2 = 2 * err;
            if (e2 >= dy) { err += dy; x0 += sx; }
            if (e2 <= dx) { err += dx; y0 += sy; }
        }
    }

    private static char PickChar(GridPoint from, GridPoint to)
    {
        int dx = Math.Sign(to.X - from.X);
        int dy = Math.Sign(to.Y - from.Y);

        return (dx, dy) switch
        {
            (0, _) => '|',
            (_, 0) => '-',
            _ when dx == dy => '\\',
            _ => '/',
        };
    }
}
