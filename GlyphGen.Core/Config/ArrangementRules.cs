namespace GlyphGen.Core.Config;

/// <summary>
/// How a shape relates to the shapes already placed in the glyph: whether it may cross them,
/// how far from them it must (or must not) sit, and which of several legal placements is
/// preferred. Used by every entry in a language's shapes list and by the combination block
/// (for the components of a composite).
/// </summary>
public class ArrangementRules
{
    /// <summary>Whether and where this shape's edges may cross edges already placed (curves count: a curved edge is
    /// checked as the curve it is drawn as). A crossing needs both shapes to accept it - the one being placed and the
    /// one that owns the edge it would cross - each by its own crossing settings.</summary>
    public CrossingConfig Crossing { get; set; } = new();

    /// <summary>Distances to keep from the shapes already placed (centroid and closest-node
    /// minimums/maximums). The rules of the shape being placed are checked against everything
    /// already there, so the outcome depends on placement order.</summary>
    public SpacingConfig Spacing { get; set; } = new();

    /// <summary>Preference among placements that already satisfy every hard rule (grid bounds,
    /// crossing, spacing). Never relaxes a rule. Empty modes = no bias.</summary>
    public CombinationBiasConfig Bias { get; set; } = new();

    /// <summary>Shapes only: how many valid candidates to generate when a bias is active; the
    /// one whose glyph bounding box scores best is kept ("best of N"). Ignored without a bias,
    /// and by combination components (they search every placement exhaustively).</summary>
    public int Candidates { get; set; } = 3;
}

public enum CrossingMode
{
    /// <summary>No crossing: edges may only meet at shared nodes (a junction on a node is fine; a stroke passing through a
    /// dot or through another stroke's middle is not).</summary>
    None,

    /// <summary>A crossing is accepted only where it lies within nodeTolerance of a node (see CrossingConfig.NodesOfShapes).</summary>
    NodesOnly,

    /// <summary>Edges may cross anywhere.</summary>
    Anywhere,

    /// <summary>No contact at all: no crossing, and not even a shared node or an edge ending on another. Nothing of this shape
    /// may touch any other shape (a pair is judged by the stricter of the two, so a Strict shape also keeps later shapes off it).
    /// Own consecutive edges still meet at their nodes. Cannot be combined with placement.startOnExistingNode.</summary>
    Strict,

    /// <summary>For <c>combination.arrangement</c> (and a recipe's override) only. Components may cross anywhere, as with
    /// Anywhere, EXCEPT that strokes drawn by a shape whose own arrangement.crossing.mode is Strict stay Strict inside the
    /// combined glyph: nothing of another component may touch them, and the shape's <c>spacing.minEdgeDistance</c> (when
    /// enabled) is kept too. Use it to keep dots clear of the other component's strokes. Not allowed on a shape.</summary>
    AnywhereButRespectStrict,
}

/// <summary>Crossing rules of one shape (arrangement.crossing). Curves are taken into account.</summary>
public sealed class CrossingConfig
{
    public CrossingMode Mode { get; set; } = CrossingMode.None;

    /// <summary>NodesOnly: how far (lattice units) from a node a crossing may lie and still count as being at it.
    /// A stroke passing near a node but not through it is fine. Default 0.3.</summary>
    public double NodeTolerance { get; set; } = 0.3;

    /// <summary>NodesOnly: only the nodes of these shapes (ids) count. Empty/missing = every node (the shape's own included). With a list, the
    /// shape's own nodes count only if its own id is listed.</summary>
    public List<string>? NodesOfShapes { get; set; }
}
