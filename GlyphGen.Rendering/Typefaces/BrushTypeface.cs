using GlyphGen.Core.Config;
using GlyphGen.Rendering.Geometry;

namespace GlyphGen.Rendering.Typefaces;

/// <summary>
/// Brush-like strokes in three modes:
///   Taper    - pointed at both ends, swelling in the middle (over the whole chain's length).
///   BroadNib - calligraphy pen: width depends on stroke direction relative to a fixed nib angle.
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
        _ => 0.0, // BroadNib: width is constant along a straight edge
    };

    public double DotRadiusPx => _halfWidth;
    public double MaxInkExtentPx
    {
        get
        {
            double h = _halfWidth;
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

    private void AppendBroadNib(StrokePath path, StrokeContext ctx, List<Vec2[]> output)
    {
        var pts = path.Points;
        int segCount = pts.Count - 1;

        var start = new WidthSample[segCount];
        var end = new WidthSample[segCount];

        for (int i = 0; i < segCount; i++)
        {
            Vec2 d = pts[i + 1] - pts[i];
            double angle = Math.Atan2(d.Y, d.X);

            // A flat nib of length W laid at the nib angle leaves a stroke whose width is
            // W * |sin(direction - nibAngle)|; never thinner than the nib's thin edge.
            double full = Math.Max(_thinHalf * 2, _halfWidth * 2 * ctx.WidthScale * Math.Abs(Math.Sin(angle - _nibRadians)));
            var w = WidthSample.Uniform(full / 2);
            start[i] = w;
            end[i] = w;
        }

        StrokeOutlineBuilder.Build(path, start, end, _joins, CapStyle.Butt, output, ctx.JoinSink, ctx.StrokeId);
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
