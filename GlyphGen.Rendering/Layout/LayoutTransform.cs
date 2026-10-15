using GlyphGen.Core.Config;
using GlyphGen.Core.Geometry;

namespace GlyphGen.Rendering.Layout;

/// <summary>
/// Uniform grid-to-pixel mapping (same scale on both axes, so shapes are never distorted).
/// Stroke widths are already in pixels, so they're unaffected by the framing mode - a
/// zoomed-in crop doesn't get chunkier strokes than a full-grid one.
/// </summary>
public readonly struct LayoutTransform
{
    public double Scale { get; }
    public double OffsetX { get; }
    public double OffsetY { get; }

    public LayoutTransform(double scale, double offsetX, double offsetY)
    {
        Scale = scale;
        OffsetX = offsetX;
        OffsetY = offsetY;
    }

    public Vec2 Apply(Vec2 grid) => new(grid.X * Scale + OffsetX, grid.Y * Scale + OffsetY);

    /// <summary>
    /// pad is the space kept clear on every side (margin plus the typeface's ink extent), so
    /// strokes at the edge of the framed area don't clip.
    /// </summary>
    public static LayoutTransform Compute(
        FramingMode mode, Bounds grid, Bounds glyph, Bounds language, double sizePx, double pad)
    {
        double available = Math.Max(1.0, sizePx - 2 * pad);

        Bounds scaleFrom, centerOn;
        switch (mode)
        {
            case FramingMode.Glyph: scaleFrom = glyph; centerOn = glyph; break;
            case FramingMode.Language: scaleFrom = language; centerOn = language; break;
            case FramingMode.GlyphCentered: scaleFrom = grid; centerOn = glyph; break;
            default: scaleFrom = grid; centerOn = grid; break;
        }

        // A minimum extent of one grid unit stops a lone straight line (zero width or height)
        // from being scaled up without limit.
        double extent = Math.Max(Math.Max(scaleFrom.Width, scaleFrom.Height), 1.0);
        double scale = available / extent;

        return new LayoutTransform(
            scale,
            sizePx / 2 - scale * centerOn.CenterX,
            sizePx / 2 - scale * centerOn.CenterY);
    }
}
