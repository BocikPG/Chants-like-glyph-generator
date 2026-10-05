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

    /// <summary>
    /// Approximate half-width of the stroke right at a free chain end (a tip, not a join).
    /// Joins are only ever computed between two segments of the SAME chain - if two DIFFERENT
    /// chains happen to end at the same grid node (two sub-symbols meeting, or two combination
    /// components touching), there is no join between them at all, and each is capped
    /// independently. With Round or Square caps that gap is usually masked by the caps'
    /// overlap; with Butt it's fully exposed as a visible notch. The renderer uses this value
    /// to fill that specific gap with a disc wherever it detects two open chains sharing an
    /// endpoint, regardless of cap style. It doesn't need to be exact - it only has to cover
    /// the real per-typeface tip width closely enough that the patch is invisible.
    /// </summary>
    double TipHalfWidthPx { get; }

    /// <summary>The typeface's own corner style, exposed so the renderer can build a REAL join
    /// (matching this typeface's Join/MiterLimit) at a shared-tip junction between two
    /// different chains, rather than a generic round patch - so Miter still gives the sharp
    /// "&gt;" look even at a junction the per-chain join logic never directly sees.</summary>
    JoinStyle Join { get; }
    double MiterLimit { get; }

    /// <summary>If &gt; 0, straight edges are subdivided to at most this length (needed by styles
    /// whose width varies along an edge). 0 = leave straight edges as one segment.</summary>
    double SampleSpacingPx { get; }

    void AppendOutline(StrokePath path, StrokeContext context, List<Vec2[]> polygons);
}
