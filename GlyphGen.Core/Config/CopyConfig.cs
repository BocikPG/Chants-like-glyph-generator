namespace GlyphGen.Core.Config;

/// <summary>How a copy is turned over, around the origin chain's start node.</summary>
public enum CopyFlip
{
    /// <summary>No flip: the copy is only moved (gap / shift / anchor).</summary>
    None,

    /// <summary>Mirrored left-right (across a vertical line through the start node).</summary>
    Horizontal,

    /// <summary>Mirrored top-bottom (across a horizontal line through the start node).</summary>
    Vertical,

    /// <summary>Turned half a revolution around the start node (both mirrors at once).</summary>
    Rotate180,

    /// <summary>Mirrored across the diagonal running top-left to bottom-right (x and y swap).</summary>
    Diagonal,

    /// <summary>Mirrored across the diagonal running bottom-left to top-right.</summary>
    AntiDiagonal,

    /// <summary>One of the flips listed in 'flips', drawn at random.</summary>
    Random,

    /// <summary>Chosen from the origin's own shape by the rule in 'auto'; ties are broken at random.</summary>
    Auto,
}

/// <summary>What an Auto flip looks at.</summary>
public enum CopyAutoBy
{
    /// <summary>Finds the longest run of consecutive edges that continue in one straight line
    /// (horizontal, vertical or diagonal) and flips ACROSS it: a horizontal run gives a left-right
    /// mirror, a vertical run a top-bottom one, a diagonal run the matching diagonal mirror.</summary>
    LongestStraight,

    /// <summary>Compares the average of the origin's nodes with the middle of the board and mirrors along
    /// the axis where they differ most; the copy is then pushed toward the middle (see 'toward').</summary>
    CenterWeight,
}

/// <summary>Where the mirror line runs relative to the longest straight run (LongestStraight).</summary>
public enum CopyAxis
{
    /// <summary>The mirror line is perpendicular to the run: a horizontal run gives a left-right mirror, so the copy continues the run backwards.</summary>
    Perpendicular,

    /// <summary>The mirror line is parallel to the run: a horizontal run gives a top-bottom mirror, so the copy lies beside the run (give it a gap, or it lies on top of it).</summary>
    Parallel,

    /// <summary>Either of the two, drawn at random for each copy.</summary>
    Random,
}

/// <summary>Which way a CenterWeight copy is pushed.</summary>
public enum CopyToward
{
    /// <summary>Toward the middle of the board.</summary>
    Middle,

    /// <summary>Away from the middle, toward the nearest edge.</summary>
    Edge,
}

/// <summary>Which point the copy's start node is placed on, before gap and shift are applied.</summary>
public enum CopyAnchor
{
    /// <summary>The start node of the origin chain itself (the copy grows from the same point).</summary>
    OriginStart,

    /// <summary>A node that is already on the glyph, picked at random from the ones that work.</summary>
    ExistingNode,

    /// <summary>A lattice point on a line that is already on the glyph (its ends included), picked at random from the ones that work.</summary>
    ExistingEdge,
}

/// <summary>The rule behind flip: Auto.</summary>
public sealed class CopyAutoConfig
{
    /// <summary>What to look at.</summary>
    public CopyAutoBy By { get; set; } = CopyAutoBy.LongestStraight;

    /// <summary>LongestStraight only: whether the mirror line runs perpendicular to the longest run (default), parallel
    /// to it, or either at random. With Parallel the copy lies beside the run, so it needs a gap of 1 or more to clear it.</summary>
    public CopyAxis Axis { get; set; } = CopyAxis.Perpendicular;

    /// <summary>CenterWeight only: push the copy toward the middle (default) or toward the edge.</summary>
    public CopyToward Toward { get; set; } = CopyToward.Middle;

    /// <summary>Ids of earlier shapes the rule looks at (all their instances in the glyph) instead of only the chain being
    /// copied. Longest straight run: the longest one among those shapes' strokes. CenterWeight: the average of all their
    /// nodes. Leave out (or empty) to look at the chain being copied alone. A listed shape that was not drawn adds nothing;
    /// if none was drawn, the chain being copied is used.</summary>
    public List<string>? Shapes { get; set; }
}

/// <summary>
/// Turns a shape into a copy of another shape (put it on the destination entry in the shapes list; the
/// origin entry must come earlier in the list). Instead of being drawn, the destination becomes one copy
/// of every instance of the origin, flipped around the origin chain's start node and then moved by the
/// anchor, gap and shift. A copy can itself be copied. The destination takes no chain, nodes, placement
/// or count of its own; its arrangement (allowCrossing, spacing, bias) decides which copy positions are
/// acceptable.
/// </summary>
public sealed class CopyConfig
{
    /// <summary>Id of the shape to copy. It must be listed before this one.</summary>
    public string Shape { get; set; } = "";

    /// <summary>How the copy is turned over; see CopyFlip.</summary>
    public CopyFlip Flip { get; set; } = CopyFlip.Auto;

    /// <summary>Flip: Random only - the flips to draw from. Default: Horizontal, Vertical, Rotate180.</summary>
    public List<CopyFlip>? Flips { get; set; }

    /// <summary>Flip: Auto only - the rule that picks the flip.</summary>
    public CopyAutoConfig Auto { get; set; } = new();

    /// <summary>Steps the copy is moved away from the origin, along the direction it was flipped toward:
    /// 0 puts the copy's start on the anchor itself (the two share that node), 1 or more moves them apart,
    /// a negative number pushes the copy back over the origin so the two overlap or cross. Drawn from this
    /// range for each copy. Default 0 to 0.</summary>
    public IntRange Gap { get; set; } = new();

    /// <summary>Steps the copy is slid sideways (across the gap direction), up or down at random.
    /// Drawn from this range, then given a random sign. Default 0 to 0.</summary>
    public IntRange Shift { get; set; } = new();

    /// <summary>Where the copy's start node goes before gap and shift.</summary>
    public CopyAnchor Anchor { get; set; } = CopyAnchor.OriginStart;

    /// <summary>Try every flip position the config allows in random order and take one that crosses nothing
    /// already drawn. If none exists the best attempt is kept, but only when the destination's
    /// arrangement.allowCrossing is true; otherwise the copy fails like any shape that cannot be placed.
    /// Turn off to take the first position that obeys the hard rules.</summary>
    public bool PreferNoCrossing { get; set; } = true;

    /// <summary>When no position keeps the copy on the grid (but one would, if the strokes already drawn
    /// sat somewhere else), the whole glyph so far is moved over, together with the copy, to where it fits.
    /// Turn off to fail instead. Not available to copies added by a derivation step, whose glyph keeps its
    /// source's position.</summary>
    public bool MoveGlyphToFit { get; set; } = true;

    /// <summary>First retry: when no position works for the copy, redraw the base shape (the first shape in this chain of
    /// copies that is not itself a copy) with fresh random numbers and try again, up to this many times. Everything placed
    /// after the base is rebuilt around the new draw. 0 turns it off. Only helps when the base is generated, not a literal.</summary>
    public int RegenerateBase { get; set; } = 3;

    /// <summary>Second retry, after the base redraws are spent: redraw the whole glyph from a fresh seed, up to this many
    /// times, before giving up with the error. 0 turns it off. Neither retry applies to copies added by a derivation step.</summary>
    public int RedrawGlyph { get; set; } = 3;

    /// <summary>The copy's curves are the origin's curves, flipped with it, so the two look like mirror
    /// images. Turn off to give the copy curves of its own. (Copies added by a derivation step always draw
    /// their own curves.)</summary>
    public bool MirrorCurves { get; set; } = true;

    /// <summary>Apply the original shape's chain.selfSpacing rules between the copy and the original, as if the copy were
    /// more nodes of the same stroke: every node of the copy is measured against every node of the chain it copies (a
    /// node the two share is exempt, except in AllNodes mode). Existing and Adjacent both mean "every node of the
    /// original" here. The copy's own nodes need no check, a flip keeps their distances. Default false.</summary>
    public bool RespectSelfSpacing { get; set; }

    /// <summary>With respectSelfSpacing: for Existing and AllNodes rules, leave the original chain out and measure the
    /// copy against every OTHER node already in the glyph instead (the original's own nodes do not count, so the copy
    /// may sit as close to it as arrangement allows). Adjacent rules still compare with the original. Default false.</summary>
    public bool SelfSpacingIgnoresOriginal { get; set; }
}
