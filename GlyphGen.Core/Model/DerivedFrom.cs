namespace GlyphGen.Core.Model;

/// <summary>
/// Where a derived glyph's edges come from, so the renderer can give an edge that survived the derivation the
/// source's exact curve. Edge keys are the two end points (x1, y1, x2, y2), smaller point first.
/// </summary>
public sealed class DerivedFrom
{
    public string Source { get; set; } = "";

    /// <summary>True when the renderer should draw this glyph at its source's rotation angle (see DerivationConfig.FinalOutputRotation
    /// and the steps that actually ran).</summary>
    public bool FollowsSourceRotation { get; set; }

    /// <summary>The whole-glyph transform applied since the source (scale, mirror, rotate) as x' = M11*x + M12*y + Tx,
    /// y' = M21*x + M22*y + Ty. Used for the curve's control points.</summary>
    public double M11 { get; set; } = 1;
    public double M12 { get; set; }
    public double M21 { get; set; }
    public double M22 { get; set; } = 1;
    public double Tx { get; set; }
    public double Ty { get; set; }

    /// <summary>Edge key in this glyph -> edge key of the same edge in the source glyph (straight edges only).</summary>
    public Dictionary<(int, int, int, int), (int, int, int, int)> Links { get; } = new();

    public static (int, int, int, int) Key(int x1, int y1, int x2, int y2) =>
        (x1 < x2 || (x1 == x2 && y1 <= y2)) ? (x1, y1, x2, y2) : (x2, y2, x1, y1);
}

/// <summary>
/// One chain of a glyph that is a flipped copy of another chain of the same glyph (a shape with copyOf), so the
/// renderer can give it the original's exact curves. Same edge keys as DerivedFrom.Links, and the same affine map
/// (x' = M11*x + M12*y + Tx, y' = M21*x + M22*y + Ty) from the original's coordinates to the copy's.
/// </summary>
public sealed class ChainCopy
{
    public double M11 { get; set; } = 1;
    public double M12 { get; set; }
    public double M21 { get; set; }
    public double M22 { get; set; } = 1;
    public double Tx { get; set; }
    public double Ty { get; set; }

    /// <summary>Edge key of the copy's edge -> edge key of the original's edge (straight edges only).</summary>
    public Dictionary<(int, int, int, int), (int, int, int, int)> Links { get; } = new();
}
