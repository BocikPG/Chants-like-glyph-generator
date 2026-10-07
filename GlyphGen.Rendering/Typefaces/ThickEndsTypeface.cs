using GlyphGen.Core.Config;
using GlyphGen.Rendering.Geometry;

namespace GlyphGen.Rendering.Typefaces;

/// <summary>
/// Thin body with flared (or, if EndWidth &lt; Width, pinched) ends. Which nodes flare is set by
/// AppliesTo: free chain tips only, every edge end, or tips plus real corners. Closed loops have
/// no tips, so under ChainEnds they render uniform. The flare length is a fraction of each
/// edge's own length, so it scales naturally with long or scaled edges.
/// </summary>
public sealed class ThickEndsTypeface : ITypeface
{
    private const double CornerCosThreshold = 0.985; // ~10 degrees

    private readonly double _baseHalf;
    private readonly double _endHalf;
    private readonly double _flareFraction;
    private readonly ThickEndsTypefaceConfig _cfg;
    private readonly JoinsConfig _joins;

    public ThickEndsTypeface(double baseWidthPx, double endWidthPx, double flareFraction, ThickEndsTypefaceConfig cfg, JoinsConfig joins)
    {
        _joins = joins;
        _baseHalf = Math.Max(0, baseWidthPx) / 2;
        _endHalf = Math.Max(0, endWidthPx) / 2;
        _flareFraction = Math.Clamp(flareFraction, 0.0, 0.5);
        _cfg = cfg;
    }

    public string Name => "ThickEnds";
    public double SampleSpacingPx => 3.0;

    public double MaxInkExtentPx
    {
        get
        {
            double h = Math.Max(_baseHalf, _endHalf);
            if (_cfg.Terminal == TerminalStyle.Ball) h = Math.Max(h, _endHalf * _cfg.BallScale);
            return h * JoinResolver.ExtentFactor(_joins);
        }
    }

    public void AppendOutline(StrokePath path, StrokeContext context, List<Vec2[]> polygons)
    {
        var pts = path.Points;
        int m = pts.Count;
        if (m < 2) return;

        double baseH = _baseHalf * context.WidthScale;
        double endH = _endHalf * context.WidthScale;

        var cum = path.CumulativeLengths();
        var nodes = path.NodeIndices;
        var flagged = ComputeFlags(path);

        var h = new double[m];
        Array.Fill(h, baseH);

        for (int k = 0; k < nodes.Count - 1; k++)
        {
            int ia = nodes[k], ib = nodes[k + 1];
            double edgeLen = cum[ib] - cum[ia];
            if (edgeLen < 1e-9) continue;

            double flare = _flareFraction * edgeLen;
            if (flare < 1e-9) continue;

            for (int i = ia; i <= ib; i++)
            {
                double u = cum[i] - cum[ia];
                double delta = 0;
                if (flagged[k]) delta += FlareDelta(u, flare, baseH, endH);
                if (flagged[k + 1]) delta += FlareDelta(edgeLen - u, flare, baseH, endH);
                h[i] = Math.Max(0, baseH + delta);
            }
        }

        var start = new WidthSample[m - 1];
        var end = new WidthSample[m - 1];
        for (int i = 0; i < m - 1; i++)
        {
            start[i] = WidthSample.Uniform(h[i]);
            end[i] = WidthSample.Uniform(h[i + 1]);
        }

        var cap = _cfg.Terminal == TerminalStyle.Round ? CapStyle.Round : CapStyle.Butt;
        StrokeOutlineBuilder.Build(path, start, end, _joins, cap, polygons, context.JoinSink, context.StrokeId);

        if (_cfg.Terminal == TerminalStyle.Ball && !path.Closed)
        {
            StrokeOutlineBuilder.AddTipDisc(context.JoinSink, pts[0], endH * _cfg.BallScale, polygons);
            StrokeOutlineBuilder.AddTipDisc(context.JoinSink, pts[m - 1], endH * _cfg.BallScale, polygons);
        }
    }

    /// <summary>Width change at distance dist from a flared node (0 beyond the flare).</summary>
    private double FlareDelta(double dist, double flare, double baseH, double endH)
    {
        if (dist >= flare) return 0;
        double t = 1.0 - dist / flare; // 0 where the flare starts, 1 at the node itself
        return (endH - baseH) * WidthProfiles.Apply(_cfg.Profile, t);
    }

    private bool[] ComputeFlags(StrokePath path)
    {
        int c = path.NodeIndices.Count;
        var f = new bool[c];

        switch (_cfg.AppliesTo)
        {
            case FlareAppliesTo.EveryEdge:
                Array.Fill(f, true);
                break;

            case FlareAppliesTo.ChainEnds:
                if (!path.Closed && c > 0) { f[0] = true; f[c - 1] = true; }
                break;

            case FlareAppliesTo.ChainEndsAndCorners:
                if (!path.Closed && c > 0) { f[0] = true; f[c - 1] = true; }
                for (int k = 1; k < c - 1; k++) f[k] = IsCorner(path, path.NodeIndices[k]);
                if (path.Closed && c > 1)
                {
                    bool seam = IsSeamCorner(path);
                    f[0] = seam;
                    f[c - 1] = seam;
                }
                break;
        }

        return f;
    }

    private static bool IsCorner(StrokePath path, int idx)
    {
        var pts = path.Points;
        if (idx <= 0 || idx >= pts.Count - 1) return false;

        Vec2 before = (pts[idx] - pts[idx - 1]).Normalized();
        Vec2 after = (pts[idx + 1] - pts[idx]).Normalized();
        return before.Dot(after) < CornerCosThreshold;
    }

    private static bool IsSeamCorner(StrokePath path)
    {
        var pts = path.Points;
        if (pts.Count < 3) return false;

        Vec2 before = (pts[^1] - pts[^2]).Normalized();
        Vec2 after = (pts[1] - pts[0]).Normalized();
        return before.Dot(after) < CornerCosThreshold;
    }
}
