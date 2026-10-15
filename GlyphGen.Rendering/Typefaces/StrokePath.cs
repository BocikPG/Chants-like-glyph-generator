using GlyphGen.Rendering.Curves;
using GlyphGen.Core.Geometry;

namespace GlyphGen.Rendering.Typefaces;

/// <summary>
/// One chain flattened to a polyline in PIXEL space. NodeIndices are the indices into Points
/// where the original grid nodes sit (edge boundaries). For a closed chain the first point is
/// repeated at the end so the path is explicit; joins are still applied at that seam.
/// </summary>
public sealed class StrokePath
{
    public List<Vec2> Points { get; } = new();
    public List<int> NodeIndices { get; } = new();
    public bool Closed { get; set; }

    public double[] CumulativeLengths()
    {
        var c = new double[Points.Count];
        for (int i = 1; i < Points.Count; i++)
            c[i] = c[i - 1] + (Points[i] - Points[i - 1]).Length;
        return c;
    }
}

public static class StrokePathBuilder
{
    /// <summary>
    /// Flattens a planned chain into pixel space. sampleSpacingPx &gt; 0 also subdivides straight
    /// edges (for typefaces whose width varies along a straight edge); 0 leaves them as a
    /// single segment.
    /// </summary>
    public static StrokePath Build(PlannedChain chain, Func<Vec2, Vec2> toPixel, double sampleSpacingPx)
    {
        var path = new StrokePath { Closed = chain.Closed };

        void AddPoint(Vec2 p)
        {
            if (path.Points.Count == 0 || (p - path.Points[^1]).Length > 1e-6) path.Points.Add(p);
        }

        bool first = true;
        foreach (var edge in chain.Edges)
        {
            Vec2 p0 = toPixel(edge.P0), c1 = toPixel(edge.C1), c2 = toPixel(edge.C2), p3 = toPixel(edge.P3);

            if (first)
            {
                AddPoint(p0);
                path.NodeIndices.Add(path.Points.Count - 1);
                first = false;
            }

            int count;
            if (edge.IsLine)
            {
                double len = (p3 - p0).Length;
                count = sampleSpacingPx > 0 ? Math.Max(1, (int)Math.Ceiling(len / sampleSpacingPx)) : 1;
            }
            else
            {
                double poly = (c1 - p0).Length + (c2 - c1).Length + (p3 - c2).Length;
                double step = sampleSpacingPx > 0 ? Math.Min(4.0, sampleSpacingPx) : 4.0;
                count = Math.Clamp((int)Math.Ceiling(poly / step), 6, 128);
            }

            for (int k = 1; k <= count; k++)
            {
                double t = k / (double)count;
                AddPoint(edge.IsLine ? Vec2.Lerp(p0, p3, t) : Vec2.Bezier(p0, c1, c2, p3, t));
            }

            path.NodeIndices.Add(path.Points.Count - 1);
        }

        return path;
    }
}
