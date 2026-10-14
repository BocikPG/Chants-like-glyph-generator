using GlyphGen.Core.Config;
using GlyphGen.Rendering.Geometry;

namespace GlyphGen.Rendering.Typefaces;

/// <summary>
/// Brush-like strokes in three modes:
///   Taper    - pointed at both ends, swelling in the middle (over the whole chain's length).
///   BroadNib - calligraphy pen: a flat nib held at a fixed angle is swept along the stroke (see AppendBroadNib).
///   Ink      - pressure profile, seeded edge wobble, optional dry-brush ragged edges and a blob
///              where each stroke starts.
/// </summary>
public sealed class BrushTypeface : ITypeface
{
    private readonly BrushTypefaceConfig _cfg;
    private readonly double _halfWidth;
    private readonly double _tipHalf;
    private readonly double _thinHalf;
    private readonly double _nibRadians;
    private readonly JoinsConfig _joins;

    public BrushTypeface(BrushTypefaceConfig cfg, double widthPx, double tipWidthPx, double thinWidthPx, double nibAngleDegrees, JoinsConfig joins)
    {
        _joins = joins;
        _cfg = cfg;
        _halfWidth = Math.Max(0, widthPx) / 2;
        _tipHalf = Math.Max(0, tipWidthPx) / 2;
        _thinHalf = Math.Max(0, thinWidthPx) / 2;
        _nibRadians = nibAngleDegrees * Math.PI / 180.0;
    }

    public string Name => "Brush";

    public double SampleSpacingPx => _cfg.Mode switch
    {
        BrushMode.Ink => 2.0,
        BrushMode.Taper => 3.0,
        _ => BroadNibVaries ? 3.0 : 0.0, // BroadNib: constant along a straight edge unless the hand varies it
    };

    private bool BroadNibVaries => _cfg.NibPressure > 0 || _cfg.NibAngleWobble > 0 || _cfg.NibLeadIn > 0;

    public double DotRadiusPx => _halfWidth;
    public double MaxInkExtentPx
    {
        get
        {
            double h = _halfWidth;
            if (_cfg.Mode == BrushMode.BroadNib)
                return Math.Sqrt(_halfWidth * _halfWidth + _thinHalf * _thinHalf); // the nib's far corner, whatever its angle; a sweep has no joins
            if (_cfg.Mode == BrushMode.Ink)
            {
                h *= 1.0 + Math.Max(0, _cfg.EdgeWobble) + Math.Max(0, _cfg.Raggedness) * 0.5;
                h = Math.Max(h, _halfWidth * Math.Max(1.0, _cfg.StartBlobScale));
            }
            return h * JoinResolver.ExtentFactor(_joins);
        }
    }

    public void AppendOutline(StrokePath path, StrokeContext context, List<Vec2[]> polygons)
    {
        if (path.Points.Count < 2) return;

        switch (_cfg.Mode)
        {
            case BrushMode.Taper: AppendTaper(path, context, polygons); break;
            case BrushMode.BroadNib: AppendBroadNib(path, context, polygons); break;
            default: AppendInk(path, context, polygons); break;
        }
    }

    private void AppendTaper(StrokePath path, StrokeContext ctx, List<Vec2[]> output)
    {
        int m = path.Points.Count;
        double maxH = _halfWidth * ctx.WidthScale;
        double tipH = Math.Min(_tipHalf * ctx.WidthScale, maxH);

        var cum = path.CumulativeLengths();
        double total = cum[m - 1];
        double sharp = Math.Max(0.05, _cfg.TaperSharpness);

        var h = new double[m];
        for (int i = 0; i < m; i++)
        {
            if (path.Closed || total < 1e-9)
            {
                h[i] = maxH; // a loop has no ends to taper
                continue;
            }

            double s = cum[i] / total;
            double envelope = Math.Pow(Math.Max(0.0, Math.Sin(Math.PI * s)), sharp);
            h[i] = tipH + (maxH - tipH) * envelope;
        }

        var start = new WidthSample[m - 1];
        var end = new WidthSample[m - 1];
        for (int i = 0; i < m - 1; i++)
        {
            start[i] = WidthSample.Uniform(h[i]);
            end[i] = WidthSample.Uniform(h[i + 1]);
        }

        var cap = tipH > 0.5 ? CapStyle.Round : CapStyle.Butt;
        StrokeOutlineBuilder.Build(path, start, end, _joins, cap, output, ctx.JoinSink, ctx.StrokeId);
    }

    /// <summary>
    /// A flat nib (length = width, thickness = thinWidth) held at the nib angle and SWEPT along the path: every segment
    /// contributes the convex hull of the nib at its two ends, so the stroke is exactly what a real pen leaves. Width
    /// follows the direction continuously (thick across the nib, hairline along it), curves change thickness smoothly
    /// and corners need no joins: the nib simply turns without turning. The hand adds a slow drift of the nib angle
    /// (nibAngleWobble), pressure (nibPressure) and an eased start and end (nibLeadIn). Every piece is convex and
    /// oriented the same way, so the pieces union cleanly under a non-zero fill.
    /// </summary>
    private void AppendBroadNib(StrokePath path, StrokeContext ctx, List<Vec2[]> output)
    {
        var pts = path.Points;
        int m = pts.Count;

        var cum = path.CumulativeLengths();
        double total = cum[m - 1];
        var pressureNoise = new ValueNoise(ctx.PathSeed ^ 0xA1A1A1A1UL);
        var angleNoise = new ValueNoise(ctx.PathSeed ^ 0xE5E5E5E5UL);
        double wobble = Math.Clamp(_cfg.NibAngleWobble, 0, 90) * Math.PI / 180.0;
        double pressure = Math.Clamp(_cfg.NibPressure, 0, 1);
        double leadIn = Math.Clamp(_cfg.NibLeadIn, 0, 0.5);
        double roundness = Math.Clamp(_cfg.NibRoundness, 0, 1);
        double halfLength = _halfWidth * ctx.WidthScale;
        double halfThick = Math.Min(_thinHalf, halfLength);

        // The nib at every path point, as offsets from that point.
        var nibs = new Vec2[m][];
        for (int i = 0; i < m; i++)
        {
            double s = cum[i];
            double scale = 1.0 - pressure * Drift(pressureNoise, s, 70.0, total, path.Closed);

            if (leadIn > 0 && !path.Closed && total > 1e-9)
            {
                double edge = Math.Min(s, total - s) / (total * leadIn);
                double eased = WidthProfiles.Apply(WidthProfile.Smooth, edge);
                scale *= 0.3 + 0.7 * eased;
            }

            double angle = _nibRadians + wobble * (Drift(angleNoise, s, 110.0, total, path.Closed) * 2 - 1);
            nibs[i] = NibOffsets(halfLength * scale, halfThick, roundness, angle);
        }

        for (int i = 0; i < m - 1; i++)
        {
            var cloud = new List<Vec2>(nibs[i].Length * 2);
            foreach (var o in nibs[i]) cloud.Add(pts[i] + o);
            foreach (var o in nibs[i + 1]) cloud.Add(pts[i + 1] + o);

            var hull = ConvexHull(cloud);
            if (hull.Length >= 3) StrokeOutlineBuilder.Emit(output, hull);
        }
    }

    /// <summary>Slow noise along a stroke in [0,1]. On a closed stroke it is blended so the end meets the start exactly (no step at the seam).</summary>
    private static double Drift(ValueNoise noise, double s, double wavelength, double total, bool closed)
    {
        double v = noise.Sample(s / wavelength);
        if (!closed || total < 1e-9) return v;
        double t = s / total;
        return v * (1 - t) + noise.Sample((s - total) / wavelength) * t;
    }

    /// <summary>The nib's outline around its centre: a rectangle half-length x half-thickness turned to the angle, its
    /// corners rounded by roundness (1 = a capsule). Convexity is restored by the hull of the sweep.</summary>
    private static Vec2[] NibOffsets(double halfLength, double halfThick, double roundness, double angle)
    {
        double radius = Math.Min(halfThick, halfLength) * roundness;
        double cx = halfLength - radius, cy = halfThick - radius;
        double cos = Math.Cos(angle), sin = Math.Sin(angle);

        Vec2 Turn(double x, double y) => new(x * cos - y * sin, x * sin + y * cos);

        if (radius < 1e-6)
            return new[] { Turn(-cx, -cy), Turn(cx, -cy), Turn(cx, cy), Turn(-cx, cy) };

        const int arc = 6; // points per quarter turn
        var list = new List<Vec2>();
        var corners = new (double X, double Y, double From)[]
        {
            (cx, cy, 0.0), (-cx, cy, Math.PI / 2), (-cx, -cy, Math.PI), (cx, -cy, 3 * Math.PI / 2),
        };
        foreach (var (x, y, from) in corners)
            for (int k = 0; k <= arc; k++)
            {
                double a = from + (Math.PI / 2) * k / arc;
                list.Add(Turn(x + radius * Math.Cos(a), y + radius * Math.Sin(a)));
            }
        return list.ToArray();
    }

    /// <summary>Convex hull (monotone chain).</summary>
    private static Vec2[] ConvexHull(List<Vec2> points)
    {
        points.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
        int n = points.Count;
        if (n < 3) return points.ToArray();

        var hull = new Vec2[2 * n];
        int k = 0;
        static double Cross(Vec2 o, Vec2 a, Vec2 b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);

        for (int i = 0; i < n; i++)
        {
            while (k >= 2 && Cross(hull[k - 2], hull[k - 1], points[i]) <= 1e-9) k--;
            hull[k++] = points[i];
        }
        for (int i = n - 2, lower = k + 1; i >= 0; i--)
        {
            while (k >= lower && Cross(hull[k - 2], hull[k - 1], points[i]) <= 1e-9) k--;
            hull[k++] = points[i];
        }
        return hull.AsSpan(0, Math.Max(0, k - 1)).ToArray();
    }

    private void AppendInk(StrokePath path, StrokeContext ctx, List<Vec2[]> output)
    {
        var pts = path.Points;
        int m = pts.Count;

        var pressureNoise = new ValueNoise(ctx.PathSeed ^ 0xA1A1A1A1UL);
        var leftNoise = new ValueNoise(ctx.PathSeed ^ 0xB2B2B2B2UL);
        var rightNoise = new ValueNoise(ctx.PathSeed ^ 0xC3C3C3C3UL);
        var raggedNoise = new ValueNoise(ctx.PathSeed ^ 0xD4D4D4D4UL);

        var cum = path.CumulativeLengths();
        double half = _halfWidth * ctx.WidthScale;

        var samples = new WidthSample[m];
        for (int i = 0; i < m; i++)
        {
            double s = cum[i];

            double pressure = 1.0 - Math.Clamp(_cfg.PressureVariation, 0, 1) * pressureNoise.Sample(s / 60.0);
            double baseH = half * pressure;

            double wobbleL = 1.0 + _cfg.EdgeWobble * (leftNoise.Sample(s / 22.0) * 2 - 1);
            double wobbleR = 1.0 + _cfg.EdgeWobble * (rightNoise.Sample(s / 22.0) * 2 - 1);

            double raggedL = _cfg.Raggedness * (raggedNoise.Hash(i * 2L) * 2 - 1) * 0.5;
            double raggedR = _cfg.Raggedness * (raggedNoise.Hash(i * 2L + 1) * 2 - 1) * 0.5;

            samples[i] = new WidthSample(
                Math.Max(0, baseH * (wobbleL + raggedL)),
                Math.Max(0, baseH * (wobbleR + raggedR)));
        }

        var start = new WidthSample[m - 1];
        var end = new WidthSample[m - 1];
        for (int i = 0; i < m - 1; i++)
        {
            start[i] = samples[i];
            end[i] = samples[i + 1];
        }

        StrokeOutlineBuilder.Build(path, start, end, _joins, CapStyle.Round, output, ctx.JoinSink, ctx.StrokeId);

        if (_cfg.StartBlobScale > 1.0 && !path.Closed)
            StrokeOutlineBuilder.AddTipDisc(ctx.JoinSink, pts[0], half * _cfg.StartBlobScale, output);
    }
}
