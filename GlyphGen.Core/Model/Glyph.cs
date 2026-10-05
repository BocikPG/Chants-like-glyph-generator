using GlyphGen.Core.Rendering;

namespace GlyphGen.Core.Model;

/// <summary>
/// One fully generated glyph: either a standalone set of chains (free-form sub-symbols plus
/// any guaranteed motifs), or a composite built entirely from PlacedComponents via overlay
/// composition — never both at once for the same glyph.
/// </summary>
public sealed class Glyph
{
    public string Name { get; set; } = "";
    public string LanguageName { get; set; } = "";

    public int GridWidth { get; set; } = 25;
    public int GridHeight { get; set; } = 25;

    /// <summary>Free-form + motif chains generated directly for this glyph. Empty for
    /// composite glyphs.</summary>
    public List<SubSymbolChain> Chains { get; } = new();

    /// <summary>Populated only for composite glyphs built via combination.recipes.</summary>
    public List<PlacedComponent> Components { get; } = new();

    public bool IsComposite => Components.Count > 0;

    /// <summary>
    /// Straight-line-only ASCII preview for console sanity checking during development.
    /// Real stroke/curve rendering (PNG/SVG, tapered line ends, bezier arcs) is a separate,
    /// later concern — this exists purely to eyeball generated structure right now.
    /// </summary>
    public override string ToString() => AsciiGlyphRenderer.Render(this);
}
