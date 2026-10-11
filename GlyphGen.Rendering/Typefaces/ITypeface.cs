using GlyphGen.Core.Config;
using GlyphGen.Rendering.Geometry;

namespace GlyphGen.Rendering.Typefaces;

/// <summary>
/// Turns a stroke skeleton into filled ink: a list of CONVEX polygons (so they union cleanly
/// when filled together under a non-zero winding rule). One instance per language, with its
/// style parameters already resolved from the seed.
/// </summary>
public interface ITypeface
{
    string Name { get; }

    /// <summary>Furthest the ink can extend from the centre-line, in px (before per-glyph jitter).
    /// Used to pad the framing so strokes at the edge don't clip.</summary>
    double MaxInkExtentPx { get; }

    /// <summary>If &gt; 0, straight edges are subdivided to at most this length (needed by styles
    /// whose width varies along an edge). 0 = leave straight edges as one segment.</summary>
    double SampleSpacingPx { get; }

    /// <summary>Radius of a dot (a Circle rule with length 0), before per-glyph width jitter: half the
    /// stroke width, so a dot is as thick as the line it sits among.</summary>
    double DotRadiusPx { get; }

    void AppendOutline(StrokePath path, StrokeContext context, List<Vec2[]> polygons);
}
