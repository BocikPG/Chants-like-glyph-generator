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

    /// <summary>0 = off. Otherwise, where two strokes meet at an angle within this many degrees of
    /// straight (180) and the notch between them has no other stroke in it, that notch is filled with
    /// a long chamfer (see straightenLength) so the edge reads as one straight line instead of a kink.
    /// Other strokes may leave the node elsewhere. It replaces the inner join for those notches and does
    /// nothing on the convex outside of the bend (that would mean cutting ink away).</summary>
    public double StraightenDegrees { get; set; } = 0.0;

    /// <summary>Length of the straightening chamfer along each stroke's edge, in half-widths. Limited
    /// automatically so it never reaches past the neighbouring node.</summary>
    public double StraightenLength { get; set; } = 4.0;
}

/// <summary>One side (outer or inner) of a join.</summary>
public sealed class JoinSideConfig
{
    public JoinStyle Style { get; set; } = JoinStyle.Miter;

    /// <summary>Outer Miter only: beyond this point-length / half-width ratio a miter falls back to a bevel.</summary>
    public double MiterLimit { get; set; } = 4.0;

    /// <summary>Inner Bevel / Round only: chamfer length or fillet radius, in half-widths of the
    /// stroke (1 = as large as half the stroke width). Limited automatically so it never reaches
    /// past the neighbouring node.</summary>
    public double Size { get; set; } = 0.5;
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
