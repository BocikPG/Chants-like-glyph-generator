using GlyphGen.Core.Config;
using GlyphGen.Rendering.Geometry;

namespace GlyphGen.Rendering.Typefaces;

/// <summary>
/// Constant width along the whole stroke. Independent of curve.mode: straight edges are plain
/// rectangles, curved edges are constant-width bands following the curve. Corner shape comes
/// from Join (Miter gives the sharp "&gt;" look), end shape from Cap.
/// </summary>
public sealed class RectangularTypeface : ITypeface
{
    private readonly double _halfWidth;
    private readonly JoinStyle _join;
    private readonly CapStyle _cap;
    private readonly double _miterLimit;

    public RectangularTypeface(double widthPx, JoinStyle join, CapStyle cap, double miterLimit)
    {
        _halfWidth = Math.Max(0, widthPx) / 2;
        _join = join;
        _cap = cap;
        _miterLimit = miterLimit;
    }

    public string Name => "Rectangular";
    public double SampleSpacingPx => 0.0; // width never varies, so straight edges stay one segment
    public double MaxInkExtentPx =>
        _halfWidth * Math.Max(StrokeOutlineBuilder.JoinExtentFactor(_join, _miterLimit), _cap == CapStyle.Square ? 1.5 : 1.0);
    public double TipHalfWidthPx => _halfWidth;
    public JoinStyle Join => _join;
    public double MiterLimit => _miterLimit;

    public void AppendOutline(StrokePath path, StrokeContext context, List<Vec2[]> polygons)
    {
        int segCount = path.Points.Count - 1;
        if (segCount < 1) return;

        var w = WidthSample.Uniform(_halfWidth * context.WidthScale);
        var start = new WidthSample[segCount];
        var end = new WidthSample[segCount];
        Array.Fill(start, w);
        Array.Fill(end, w);

        StrokeOutlineBuilder.Build(path, start, end, _join, _miterLimit, _cap, polygons);
    }
}
