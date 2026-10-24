namespace GlyphGen.Core.Config;

/// <summary>
/// How curves are handled for a glyph rendered ON ITS OWN ("mini") versus the same glyph
/// appearing as a component inside a composite ("combined"). "Curved" means the glyph's normal
/// curve plan (CurveConfig still decides edge by edge which edges are lines and which bulge -
/// it does not force every edge to bend); "straight" means every edge is a straight line.
/// Wherever a glyph is curved, its curves are the SAME in both contexts: a component reuses its
/// source glyph's plan, mapped through the component's scale/rotation/translation.
/// </summary>
public enum CurveContextMode
{
    /// <summary>The normal curve plan is used both alone and as a component.</summary>
    AllCurved,
    /// <summary>Every edge is a straight line, alone and as a component.</summary>
    AllStraight,
    /// <summary>Curve plan when drawn alone, straight lines as a component of a composite.</summary>
    CurvedMiniStraightCombined,
    /// <summary>Straight lines when drawn alone, curve plan as a component of a composite.</summary>
    StraightMiniCurvedCombined,
}
