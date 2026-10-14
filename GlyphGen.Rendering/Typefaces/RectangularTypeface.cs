using GlyphGen.Core.Config;
using GlyphGen.Rendering.Geometry;

namespace GlyphGen.Rendering.Typefaces;

/// <summary>
/// Constant width along the whole stroke. Independent of curve.mode: straight edges are plain
/// rectangles, curved edges are constant-width bands following the curve. Corner shape comes
/// from render.joins, end shape from Cap.
/// </summary>
public sealed class RectangularTypeface : ITypeface
{
    private readonly double _halfWidth;
    private readonly CapStyle _cap;
    private readonly LevelAxis _capLevel;
    private readonly JoinsConfig _joins;

    public RectangularTypeface(double widthPx, CapStyle cap, JoinsConfig joins, LevelAxis capLevel = LevelAxis.Auto)
    {
        _capLevel = capLevel;
        _halfWidth = Math.Max(0, widthPx) / 2;
        _cap = cap;
        _joins = joins;
    }

    public string Name => "Rectangular";
    public double DotRadiusPx => _halfWidth;
    public double SampleSpacingPx => 0.0; // width never varies, so straight edges stay one segment
    public double MaxInkExtentPx =>
        _halfWidth * Math.Max(JoinResolver.ExtentFactor(_joins), _cap == CapStyle.Square ? 1.5 : _cap == CapStyle.Level ? 2.3 : 1.0);

    public void AppendOutline(StrokePath path, StrokeContext context, List<Vec2[]> polygons)
    {
        int segCount = path.Points.Count - 1;
        if (segCount < 1) return;

        var w = WidthSample.Uniform(_halfWidth * context.WidthScale);
        var start = new WidthSample[segCount];
        var end = new WidthSample[segCount];
        Array.Fill(start, w);
        Array.Fill(end, w);

        StrokeOutlineBuilder.Build(path, start, end, _joins, _cap, polygons, context.JoinSink, context.StrokeId, _capLevel);
    }
}
