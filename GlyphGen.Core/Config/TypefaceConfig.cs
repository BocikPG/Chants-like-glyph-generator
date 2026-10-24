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

public enum TypefaceKind
{
    /// <summary>Constant-width strokes with configurable caps and joins (the <c>rectangular</c> block).</summary>
    Rectangular,
    /// <summary>Strokes that flare at their ends (the <c>thickEnds</c> block).</summary>
    ThickEnds,
    /// <summary>Pen-like strokes: Taper, BroadNib or Ink (the <c>brush</c> block).</summary>
    Brush,
}
/// <summary>How a join is drawn. Outer side: Miter = sharp point, Bevel = flat cut, Round = arc,
/// Overlap = nothing added (plain overlapping strokes). Inner side: Bevel = flat chamfer across the
/// notch, Round = fillet; Miter and Overlap both leave the notch as it is. Level (outer only): the miter
/// point cut flat along an axis, at the level of the outer corner that reaches furthest; the inner side leaves the notch as it is.</summary>
public enum JoinStyle
{
    /// <summary>Outer: a sharp point. Inner: leaves the notch as it is.</summary>
    Miter,
    /// <summary>A flat cut across the corner (outer) or chamfer across the notch (inner).</summary>
    Bevel,
    /// <summary>An arc (outer) or fillet (inner).</summary>
    Round,
    /// <summary>Nothing added: plain overlapping strokes.</summary>
    Overlap,
    /// <summary>Outer only: the miter point cut flat along levelAxis.</summary>
    Level,
}
/// <summary>Level: a free end cut flat along an axis (see LevelAxis): the corner that reaches less far is extended to the
/// level of the other. Does nothing on a stroke that already ends square to that axis.</summary>
public enum CapStyle
{
    /// <summary>Flat end, square to the stroke.</summary>
    Butt,
    /// <summary>Flat end extended half a stroke width.</summary>
    Square,
    /// <summary>Semicircular end.</summary>
    Round,
    /// <summary>Flat end cut along levelAxis (see capLevel).</summary>
    Level,
}
/// <summary>The line a Level cap or join is cut along. Auto = across the stroke's dominant direction (a cap) or the
/// join's outward direction (a join), so horizontal and vertical strokes are left alone.
/// Both: cut along the row AND the column, so a diagonal end becomes a point (like a ">") with one horizontal and one vertical face.</summary>
public enum LevelAxis
{
    /// <summary>Across the stroke's dominant direction (a cap) or the join's outward direction (a join).</summary>
    Auto,
    /// <summary>Cut along the row.</summary>
    Horizontal,
    /// <summary>Cut along the column.</summary>
    Vertical,
    /// <summary>Cut along row and column, so a diagonal end becomes a point with one horizontal and one vertical face.</summary>
    Both,
}
/// <summary>What a Level cap does about the level of its end. Keep = leave it. ExtendToNeighbours = every Level end grows until its face lies half a stroke width past its node's row (or column), where a stroke along that line would end, whether or not one is there.</summary>
public enum LevelFlat
{
    /// <summary>Leave the level of the end as it is.</summary>
    Keep,
    /// <summary>Every Level end grows until its face lies half a stroke width past its node's row (or column).</summary>
    ExtendToNeighbours,
}
public enum WidthProfile
{
    /// <summary>Width changes at a constant rate along the stroke.</summary>
    Linear,
    /// <summary>Width eases in and out (smooth curve).</summary>
    Smooth,
    /// <summary>Width stays thin and widens sharply near the end.</summary>
    Sharp,
}
public enum FlareAppliesTo
{
    /// <summary>Only the two free ends of each chain.</summary>
    ChainEnds,
    /// <summary>Both ends of every edge.</summary>
    EveryEdge,
    /// <summary>Chain ends and every corner where two edges meet.</summary>
    ChainEndsAndCorners,
}
public enum TerminalStyle
{
    /// <summary>The flared end is cut flat.</summary>
    Flat,
    /// <summary>The flared end is rounded.</summary>
    Round,
    /// <summary>The flared end finishes in a round ball (see ballScale).</summary>
    Ball,
}
public enum BrushMode
{
    /// <summary>Width runs from the full width to a thin tip (tipWidth, taperSharpness).</summary>
    Taper,
    /// <summary>A flat nib held at a fixed angle: thick and thin strokes by direction (nib* options).</summary>
    BroadNib,
    /// <summary>Pressure and ragged edges like ink on paper (pressureVariation, edgeWobble, raggedness, startBlobScale).</summary>
    Ink,
}

/// <summary>
/// Constant-width strokes. Independent of curve.mode: on a curved edge you simply get a
/// constant-width band following the curve. All widths in this file's configs are fractions of
/// the canvas size, so stroke weight looks the same at any pixel size and in any framing mode.
/// </summary>
public sealed class RectangularTypefaceConfig
{
    /// <summary>Stroke width as a fraction of the canvas size; a value is drawn per glyph from the range. Default 0.05.</summary>
    public FloatRange Width { get; set; } = new() { Min = 0.05, Max = 0.05 };

    /// <summary>How a free stroke end is finished: Butt, Square, Round or Level. Default Butt.</summary>
    public CapStyle Cap { get; set; } = CapStyle.Butt;

    /// <summary>With cap Level: which line the end is cut along. Horizontal = flat on the node's row (like text on a
    /// baseline), Vertical = along its column, Auto = across the stroke's dominant direction, so a stroke steeper than 45
    /// degrees is cut horizontally and a flatter one vertically. Straight horizontal and vertical strokes are never changed.</summary>
    public LevelAxis CapLevel { get; set; } = LevelAxis.Auto;

    /// <summary>With cap Level: what happens to an end that is already flat on the axis. ExtendToNeighbours grows every end to
    /// half a stroke width past its node's row (or column), so verticals, horizontals and diagonals all stop on one line.</summary>
    public LevelFlat CapLevelFlat { get; set; } = LevelFlat.Keep;

}

/// <summary>Strokes of constant width whose ends flare wider (optionally ending in a ball).</summary>
public sealed class ThickEndsTypefaceConfig
{
    /// <summary>Width along the body of the stroke.</summary>
    public FloatRange Width { get; set; } = new() { Min = 0.03, Max = 0.03 };
    /// <summary>Width at flared nodes.</summary>
    public FloatRange EndWidth { get; set; } = new() { Min = 0.06, Max = 0.06 };
    /// <summary>Flare length as a fraction of each edge's length (capped at 0.5).</summary>
    public FloatRange FlareLength { get; set; } = new() { Min = 0.25, Max = 0.25 };

    /// <summary>How the width changes along the flare: Linear, Smooth (default) or Sharp.</summary>
    public WidthProfile Profile { get; set; } = WidthProfile.Smooth;
    /// <summary>Which ends flare: ChainEnds (default), EveryEdge or ChainEndsAndCorners.</summary>
    public FlareAppliesTo AppliesTo { get; set; } = FlareAppliesTo.ChainEnds;
    /// <summary>How the flared end is finished: Flat (default), Round or Ball.</summary>
    public TerminalStyle Terminal { get; set; } = TerminalStyle.Flat;

    /// <summary>Ball diameter = EndWidth * BallScale (Terminal == Ball only).</summary>
    public double BallScale { get; set; } = 1.3;
}

/// <summary>Pen-like strokes. The mode picks the pen (Taper, BroadNib or Ink); each mode has its own options.</summary>
public sealed class BrushTypefaceConfig
{
    /// <summary>The pen: Taper (full width narrowing to a tip), BroadNib (a flat nib at an angle) or Ink (pressure and ragged edges).</summary>
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
    /// <summary>0 = a flat, square-ended nib; 1 = fully rounded ends (a capsule, like a brush pen). In between rounds the corners.</summary>
    public double NibRoundness { get; set; } = 0.0;
    /// <summary>How far (degrees, +-) the hand lets the nib angle drift along a stroke, slowly and seeded per stroke.</summary>
    public double NibAngleWobble { get; set; } = 0.0;
    /// <summary>0-1: how much the nib length (pressure) varies along a stroke.</summary>
    public double NibPressure { get; set; } = 0.0;
    /// <summary>0-0.5: fraction of an open stroke's length, at each end, over which the nib eases in from a light touch
    /// to full length, like a hand setting the pen down and lifting it.</summary>
    public double NibLeadIn { get; set; } = 0.0;

    // Ink
    public double PressureVariation { get; set; } = 0.35;
    public double EdgeWobble { get; set; } = 0.15;
    public double Raggedness { get; set; } = 0.0;
    /// <summary>&gt;1 adds a blob where each stroke starts.</summary>
    public double StartBlobScale { get; set; } = 1.4;
}

/// <summary>
/// One typeface per language, resolved once from the seed (so every glyph in a language reads as
/// the same font). Kind selects which sub-config is used; a null sub-config means defaults.
/// </summary>
public sealed class TypefaceConfig
{
    /// <summary>The stroke style: Rectangular (constant width), ThickEnds (flared ends) or Brush (pen-like). Only the matching block is used.</summary>
    public TypefaceKind Kind { get; set; } = TypefaceKind.Rectangular;
    public RectangularTypefaceConfig? Rectangular { get; set; }
    public ThickEndsTypefaceConfig? ThickEnds { get; set; }
    public BrushTypefaceConfig? Brush { get; set; }

    /// <summary>0 = every glyph uses the identical typeface; 0.1 = each glyph's widths vary by up to +-10%.</summary>
    public double PerGlyphJitter { get; set; } = 0.0;
}
