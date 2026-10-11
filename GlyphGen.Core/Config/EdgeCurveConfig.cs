namespace GlyphGen.Core.Config;

/// <summary>
/// How ONE edge is bent when drawn. Each connection rule carries its own copy, so a rule can make
/// long edges gentle curves and short ticks dead straight within the same stroke. Edges that
/// come from no rule (Literal shapes) use curve.edge instead. Only affects drawing - never
/// whether a connection is valid.
/// </summary>
public sealed class EdgeCurveConfig
{
    /// <summary>Chance (0-100) that an edge made by this rule is drawn as a curve; otherwise it is a
    /// straight line. Rolled per edge. 100 = always curved (a 0..0 tension still looks straight).
    /// This replaces the old language-wide straightLineBias (chanceToBeCurved = 100 - straightLineBias).</summary>
    public double ChanceToBeCurved { get; set; } = 100.0;

    /// <summary>How the edge's end tangents are chosen. Derived: from the neighbouring nodes (smooth
    /// spline feel). Explicit: each end gets its own randomized tangent. Hybrid: Derived or Explicit
    /// per edge, at random.</summary>
    public CurveMode Mode { get; set; } = CurveMode.Derived;

    /// <summary>Bezier handle length as a fraction of the edge length; each handle draws its own value.</summary>
    public TensionConfig Tension { get; set; } = new();

    /// <summary>Explicit/Hybrid: max angle (degrees) an end tangent may deviate from the derived one.</summary>
    public FloatRange ExplicitJitterDegrees { get; set; } = new() { Min = 20, Max = 60 };
}
