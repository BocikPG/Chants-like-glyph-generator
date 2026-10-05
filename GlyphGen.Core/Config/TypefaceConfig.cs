namespace GlyphGen.Core.Config;

public enum FramingMode
{
    /// <summary>The whole grid maps to the canvas; a glyph's size/position on the grid carries through.</summary>
    Grid,
    /// <summary>Crop to the glyph's own bounding box and centre it (relative sizes are lost).</summary>
    Glyph,
    /// <summary>Crop to the combined bounding box of every glyph in the language (same scale for all).</summary>
    Language,
    /// <summary>Grid's scale, but each glyph re-centred on the canvas.</summary>
    GlyphCentered,
}

public enum TypefaceKind { Rectangular, ThickEnds, Brush }
public enum JoinStyle { Miter, Bevel, Round, Overlap }
public enum CapStyle { Butt, Square, Round }
public enum WidthProfile { Linear, Smooth, Sharp }
public enum FlareAppliesTo { ChainEnds, EveryEdge, ChainEndsAndCorners }
public enum TerminalStyle { Flat, Round, Ball }
public enum BrushMode { Taper, BroadNib, Ink }

/// <summary>
/// Constant-width strokes. Independent of curve.mode: on a curved edge you simply get a
/// constant-width band following the curve. All widths in this file's configs are fractions of
/// the canvas size, so stroke weight looks the same at any pixel size and in any framing mode.
/// </summary>
public sealed class RectangularTypefaceConfig
{
    public FloatRange Width { get; set; } = new() { Min = 0.05, Max = 0.05 };

    /// <summary>Miter = sharp pointed corner (the "&gt;" look); Overlap = plain rectangles crossing.</summary>
    public JoinStyle Join { get; set; } = JoinStyle.Miter;
    public CapStyle Cap { get; set; } = CapStyle.Butt;

    /// <summary>Beyond this tip-length / half-width ratio a miter falls back to a bevel.</summary>
    public double MiterLimit { get; set; } = 4.0;
}

public sealed class ThickEndsTypefaceConfig
{
    /// <summary>Width along the body of the stroke.</summary>
    public FloatRange Width { get; set; } = new() { Min = 0.03, Max = 0.03 };
    /// <summary>Width at flared nodes.</summary>
    public FloatRange EndWidth { get; set; } = new() { Min = 0.06, Max = 0.06 };
    /// <summary>Flare length as a fraction of each edge's length (capped at 0.5).</summary>
    public FloatRange FlareLength { get; set; } = new() { Min = 0.25, Max = 0.25 };

    public WidthProfile Profile { get; set; } = WidthProfile.Smooth;
    public FlareAppliesTo AppliesTo { get; set; } = FlareAppliesTo.ChainEnds;
    public TerminalStyle Terminal { get; set; } = TerminalStyle.Flat;

    /// <summary>Ball diameter = EndWidth * BallScale (Terminal == Ball only).</summary>
    public double BallScale { get; set; } = 1.3;

    public JoinStyle Join { get; set; } = JoinStyle.Round;
    public double MiterLimit { get; set; } = 4.0;
}

public sealed class BrushTypefaceConfig
{
    public BrushMode Mode { get; set; } = BrushMode.Taper;

    /// <summary>Maximum stroke width (Taper/Ink) or nib length (BroadNib).</summary>
    public FloatRange Width { get; set; } = new() { Min = 0.06, Max = 0.06 };

    // Taper
    public FloatRange TipWidth { get; set; } = new() { Min = 0.0, Max = 0.0 };
    /// <summary>&gt;1 = pointier, &lt;1 = fuller.</summary>
    public double TaperSharpness { get; set; } = 1.0;

    // BroadNib (angle measured on screen, clockwise from the +x axis)
    public FloatRange NibAngleDegrees { get; set; } = new() { Min = 40, Max = 40 };
    public FloatRange ThinWidth { get; set; } = new() { Min = 0.012, Max = 0.012 };

    // Ink
    public double PressureVariation { get; set; } = 0.35;
    public double EdgeWobble { get; set; } = 0.15;
    public double Raggedness { get; set; } = 0.0;
    /// <summary>&gt;1 adds a blob where each stroke starts.</summary>
    public double StartBlobScale { get; set; } = 1.4;

    public JoinStyle Join { get; set; } = JoinStyle.Round;
    public double MiterLimit { get; set; } = 4.0;
}

/// <summary>
/// One typeface per language, resolved once from the seed (so every glyph in a language reads as
/// the same font). Kind selects which sub-config is used; a null sub-config means defaults.
/// </summary>
public sealed class TypefaceConfig
{
    public TypefaceKind Kind { get; set; } = TypefaceKind.Rectangular;
    public RectangularTypefaceConfig? Rectangular { get; set; }
    public ThickEndsTypefaceConfig? ThickEnds { get; set; }
    public BrushTypefaceConfig? Brush { get; set; }

    /// <summary>0 = every glyph uses the identical typeface; 0.1 = each glyph's widths vary by up to +-10%.</summary>
    public double PerGlyphJitter { get; set; } = 0.0;
}
