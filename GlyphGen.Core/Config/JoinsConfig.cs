namespace GlyphGen.Core.Config;

/// <summary>
/// How strokes are joined, for every typeface. A "corner" is a bend within one stroke; a
/// "junction" is any node where strokes meet (tip to tip, a tip on another stroke, a crossing,
/// a stroke revisiting its own node). Each has an OUTER side (the convex outside of the bend, where
/// ink is added to make a point or round) and an INNER side (the notch between two strokes, which can
/// be filled with a chamfer or fillet).
/// </summary>
public sealed class JoinsConfig
{
    /// <summary>A bend within one stroke.</summary>
    public JoinContextConfig Corner { get; set; } = new();

    /// <summary>Strokes meeting at a node.</summary>
    public JunctionJoinConfig Junction { get; set; } = new();
}

/// <summary>The outer and inner join for one kind of node, plus optional per-angle overrides.</summary>
public class JoinContextConfig
{
    /// <summary>The convex outside of the bend.</summary>
    public JoinSideConfig Outer { get; set; } = new() { Style = JoinStyle.Miter };

    /// <summary>The notch between two strokes. Overlap (the default) leaves it as it is.</summary>
    public JoinSideConfig Inner { get; set; } = new() { Style = JoinStyle.Overlap };

    /// <summary>Overrides for particular angles, tried in order; the first rule whose angle range
    /// contains the angle wins, and whatever it leaves out falls back to outer / inner above.
    /// The angle is the one between the two strokes: 180 = straight on, 90 = a right angle,
    /// 45 = a sharp turn. On the grid that is almost always 45, 90 or 135; on curves it varies.</summary>
    public List<JoinAngleRule> ByAngle { get; set; } = new();
}

/// <summary>The join settings for strokes meeting at a node: everything a corner has, plus how the tips
/// that end there are treated.</summary>
public sealed class JunctionJoinConfig : JoinContextConfig
{
    /// <summary>If true, tips that end at a junction (a node where another stroke is also present) are
    /// drawn without their caps, Ball terminals or start blobs, and the joins above fill the node
    /// instead. A tip that stands alone keeps all of them. With outer Overlap nothing fills the
    /// node, so pair this with Miter, Bevel or Round.</summary>
    public bool TrimCaps { get; set; } = false;

    /// <summary>0 = off. Where two strokes meet within this many degrees of straight (180) and the NOTCH between
    /// them (the side that bends inwards, under 180 degrees) has no other stroke in it, that notch is filled
    /// with a long chamfer (see straightenLength) so the edge reads as one straight line instead of a kink.
    /// Other strokes may leave the node on the other side. Replaces the inner join for those notches.</summary>
    public double StraightenInnerDegrees { get; set; } = 0.0;

    /// <summary>0 = off. The same for the OUTSIDE of the bend (the side that bends outwards, over 180 degrees),
    /// e.g. the top of a T whose bar is slightly bent that way. Ink cannot be removed, so instead the edges are
    /// extended along one straight line through the corner and the thin wedges between that line and the
    /// real edges are filled; each wedge ends in a small step whose height grows with the angle and with
    /// straightenLength. Replaces the outer join for those gaps.</summary>
    public double StraightenOuterDegrees { get; set; } = 0.0;

    /// <summary>Length of the straightening fill along each stroke's edge, in half-widths. Limited
    /// automatically so it never reaches past the neighbouring node.</summary>
    public double StraightenLength { get; set; } = 4.0;
}

/// <summary>One side (outer or inner) of a join.</summary>
public sealed class JoinSideConfig
{
    /// <summary>How the join is drawn (Miter, Bevel, Round, Overlap, Level). Outer and inner sides read the styles differently; see the values.</summary>
    public JoinStyle Style { get; set; } = JoinStyle.Miter;

    /// <summary>Outer Miter only: beyond this point-length / half-width ratio a miter falls back to a bevel.</summary>
    public double MiterLimit { get; set; } = 4.0;

    /// <summary>Inner Bevel / Round only: chamfer length or fillet radius, in half-widths of the
    /// stroke (1 = as large as half the stroke width). Limited automatically so it never reaches
    /// past the neighbouring node.</summary>
    public double Size { get; set; } = 0.5;

    /// <summary>Outer Level only: the line the corner is cut along. Auto = across the corner's outward direction, so a
    /// V pointing down is cut flat on the bottom and a corner pointing right is cut along its column.</summary>
    public LevelAxis Axis { get; set; } = LevelAxis.Auto;
}

/// <summary>Replaces the outer and/or inner join for nodes whose angle falls in a range.</summary>
public sealed class JoinAngleRule
{
    /// <summary>Degrees, 0 to 180, inclusive at both ends.</summary>
    public FloatRange Angle { get; set; } = new() { Min = 0, Max = 180 };

    /// <summary>Replacement outer join, or omitted to keep the context's.</summary>
    public JoinSideConfig? Outer { get; set; }

    /// <summary>Replacement inner join, or omitted to keep the context's.</summary>
    public JoinSideConfig? Inner { get; set; }
}
