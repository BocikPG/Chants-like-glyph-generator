using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Config;

public enum CurveMode
{
    /// <summary>Tangents inferred from neighboring node angles (smooth-spline feel).</summary>
    Derived,

    /// <summary>Each node stores its own randomized in/out tangent.</summary>
    Explicit,

    /// <summary>Mix of both, per-node.</summary>
    Hybrid,
}

/// <summary>One weighted tension range to pick from - see `tension`.</summary>
public sealed class TensionOption
{
    /// <summary>Lowest tension of this band (a fraction of the edge length, at most 0.5).</summary>
    public double Min { get; set; }
    /// <summary>Highest tension of this band. Must not be below min.</summary>
    public double Max { get; set; }
    /// <summary>How likely this band is relative to the others. Default 1.</summary>
    public double Weight { get; set; } = 1.0;
}

/// <summary>
/// Bezier handle length as a fraction of the edge's length (capped at 0.5 downstream). Default
/// behavior (Options empty) is a single continuous range, uniformly sampled - the same as a
/// plain FloatRange used to be. Populating Options switches to a weighted pick among several
/// ranges instead: this is what lets a language express an extreme/bimodal distribution like
/// "70% dead straight (0-0), 30% strongly curved (0.4-0.5)" rather than one smooth spread
/// across the whole range - a look a single continuous range can't produce, since it always
/// fills in the middle values too. Not restricted to two options; any number of weighted bands
/// works, following the same {value/range, weight} pattern as connection rules and
/// scale ratios elsewhere in this schema.
/// </summary>
public sealed class TensionConfig
{
    /// <summary>Used only when Options is empty.</summary>
    public FloatRange Range { get; set; } = new() { Min = 0.25, Max = 0.35 };

    /// <summary>Weighted ranges to pick from (for a bimodal look: mostly straight, sometimes strongly curved). Empty = use range.</summary>
    public List<TensionOption> Options { get; set; } = new();

    public double Resolve(DeterministicRandom rng)
    {
        if (Options.Count == 0) return Range.Resolve(rng);

        var weights = Options.Select(o => o.Weight).ToList();
        var chosen = Options[rng.WeightedIndex(weights)];
        return chosen.Min + rng.NextDouble() * (chosen.Max - chosen.Min);
    }
}

/// <summary>
/// Forces an edge straight based on geometry, independent of the edge's curve settings - a
/// safety net against a curve bulging into nearby ink (a curve on a short edge swings out more).
/// Disabled by default since it changes how many edges end up curved vs. the language's other
/// settings alone would produce.
/// </summary>
public sealed class CurveProximityGuardConfig
{
    /// <summary>Turns the guard on. Default false.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>Edges shorter than this (grid units) are always forced straight - a curve on a
    /// very short edge exaggerates its bulge relative to the edge's own length, making it more
    /// likely to swing into nearby geometry.</summary>
    public double MinEdgeLength { get; set; } = 1.5;
}

/// <summary>
/// Rendering-time curve behavior that is NOT per edge. How an edge bends (chance, mode, tension,
/// jitter) is set on the connection rule that made it - see the `curve` block of a connection rule. Kept independent of the
/// underlying straight-edge grid topology (which is what intersection checks always operate on):
/// this only affects how the final glyph is drawn, never whether a connection is valid.
/// </summary>
public sealed class CurveConfig
{
    /// <summary>Curve settings for edges that no connection rule made (the edges of Literal shapes).</summary>
    public EdgeCurveConfig Edge { get; set; } = new();

    /// <summary>A safety net that draws edges straight when a curve could swing into nearby ink. Off by default.</summary>
    public CurveProximityGuardConfig ProximityGuard { get; set; } = new();

    /// <summary>
    /// Plans EVERY edge as a curve: ignores every chanceToBeCurved and the proximity guard (both would
    /// otherwise force edges straight), so "curved" really means curved everywhere. Whether an
    /// edge visibly bends still depends on tension: a 0..0 tension band gives a bezier that looks
    /// straight. Must not be combined with a chanceToBeCurved below 100 or an enabled guard
    /// (the loader rejects that, since they would be silently overridden). Default false.
    /// </summary>
    public bool ForceAllEdgesCurved { get; set; } = false;

    /// <summary>Whether glyphs are curved when drawn alone and when used as a component of a composite. Default AllCurved.</summary>
    public CurveContextMode ContextMode { get; set; } = CurveContextMode.AllCurved;
}
