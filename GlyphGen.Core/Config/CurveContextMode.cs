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
    AllCurved,
    AllStraight,
    CurvedMiniStraightCombined,
    StraightMiniCurvedCombined,
}
