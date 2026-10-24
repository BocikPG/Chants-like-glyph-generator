namespace GlyphGen.Core.Config;

/// <summary>What one derivation step does.</summary>
public enum DerivedRotation
{
    /// <summary>Always draws its own angle.</summary>
    Own,

    /// <summary>Follows the source when the steps that ran were only Scale, Mirror, Rotate or Nudge (default).</summary>
    FollowMoves,

    /// <summary>Also follows after DeleteNodes, DeleteEdges and Keep: what is left is a part of the source.</summary>
    FollowRemovals,

    /// <summary>Follows whatever the steps did, Add included.</summary>
    FollowAlways,
}

public enum DerivationOp
{
    /// <summary>Removes nodes, and the edges that touch them (see DerivationStep.Mode).</summary>
    DeleteNodes,

    /// <summary>Removes edges.</summary>
    DeleteEdges,

    /// <summary>The opposite of DeleteEdges: keeps the given amount of edges and removes all the others.</summary>
    Keep,

    /// <summary>Adds strokes from entries of the language's shapes, placed against what is left.</summary>
    Add,

    /// <summary>Scales the whole glyph about its middle (lattice-safe ratios, like combination scale).</summary>
    Scale,

    /// <summary>Flips the whole glyph.</summary>
    Mirror,

    /// <summary>Rotates the whole glyph by a multiple of 90 degrees.</summary>
    Rotate,

    /// <summary>Moves some nodes by a step or two.</summary>
    Nudge,

    /// <summary>Cuts straight edges into smaller pieces by adding nodes on them (see DerivationStep.Parts, Distribution, Snap).
    /// The glyph looks the same afterwards when the nodes are exact; the extra nodes can then be deleted, nudged or used by later steps.</summary>
    SplitEdges,
}

/// <summary>Where the new nodes of a SplitEdges step go along an edge.</summary>
public enum SplitDistribution
{
    /// <summary>As evenly as the lattice allows: the pieces are as close to the same length as possible.</summary>
    Equal,

    /// <summary>At random places along the edge.</summary>
    Random,
}

/// <summary>What a SplitEdges step does when the ideal place for a node is not a lattice point.</summary>
public enum SplitSnap
{
    /// <summary>Only nodes that lie exactly on the edge are used (the lattice points the edge passes over). The edge keeps its
    /// shape; an edge that passes over no free lattice point is left alone. This is the default.</summary>
    Exact,

    /// <summary>The ideal place is moved to the nearest lattice point, so the edge becomes a slightly bent path through these
    /// points (an approximation). See maxDeviation.</summary>
    Nearest,
}

/// <summary>Which nodes a node step may pick. A node's degree is the number of edge ends on it.</summary>
public enum DerivationTarget
{
    /// <summary>Any node.</summary>
    Any,

    /// <summary>Free tips: nodes with one edge end.</summary>
    Ends,

    /// <summary>Nodes with exactly two edge ends, i.e. the middle of a stroke.</summary>
    Inner,

    /// <summary>Nodes where three or more edge ends meet (strokes sharing a node).</summary>
    Junction,
}

/// <summary>What deleting a node does to the stroke through it.</summary>
public enum DeleteNodeMode
{
    /// <summary>The node and its edges are removed; a stroke through it splits in two.</summary>
    Cut,

    /// <summary>For a node in the middle of a stroke, the two neighbours are reconnected with a new path grown
    /// from the step's connectionRules (at most bridgeMaxMoves moves, crossing nothing). If no path exists the
    /// node is cut instead. Tips and junctions are always cut.</summary>
    Bridge,
}

public enum MirrorAxis
{
    /// <summary>Left becomes right.</summary>
    Horizontal,

    /// <summary>Top becomes bottom.</summary>
    Vertical,

    /// <summary>One of the two, at random.</summary>
    Random,
}

/// <summary>How much a step affects: a percentage of what exists, or a fixed number, each as a range.
/// Set exactly one of Percent and Count.</summary>
public sealed class DerivationAmount
{
    /// <summary>Percentage (0-100) of all nodes (node steps) or all edges (edge steps) that exist when the
    /// step runs, rounded to a whole number. Resolved uniformly in the range.</summary>
    public PercentRange? Percent { get; set; }

    /// <summary>A fixed number of nodes/edges, drawn uniformly (inclusive) from the range.</summary>
    public IntRange? Count { get; set; }
}

/// <summary>A gate on a step: it only runs when the glyph, as it is when the step comes up (after the earlier
/// steps), has a number of edges and nodes within these limits. Every limit that is set must hold; none set = no
/// gate. Nodes are the distinct points, including lone ones; a circle or dot is one edge.</summary>
public sealed class DerivationCondition
{
    /// <summary>The step runs only if the glyph has at least this many edges at that point.</summary>
    public int? MinEdges { get; set; }
    /// <summary>The step runs only if the glyph has at most this many edges at that point.</summary>
    public int? MaxEdges { get; set; }
    /// <summary>The step runs only if the glyph has at least this many nodes at that point.</summary>
    public int? MinNodes { get; set; }
    /// <summary>The step runs only if the glyph has at most this many nodes at that point.</summary>
    public int? MaxNodes { get; set; }
}

/// <summary>
/// One step of a derivation. A single class for every op, so only the options that belong to the op are
/// allowed (the loader rejects the others).
/// </summary>
public sealed class DerivationStep
{
    /// <summary>What the step does. Each op accepts only its own options.</summary>
    public DerivationOp Op { get; set; }

    /// <summary>Only run the step if the glyph has enough / not too many edges and nodes at that point (see
    /// the limits of `onlyIf`). Checked before chance. A skipped step changes nothing and uses no random numbers.</summary>
    public DerivationCondition? OnlyIf { get; set; }

    /// <summary>Chance (0-100) that the step runs at all. Default 100.</summary>
    public double Chance { get; set; } = 100.0;

    /// <summary>DeleteNodes, DeleteEdges, Keep, Nudge: how many. For Keep it is how many edges stay. SplitEdges: how many edges are
    /// split (left out = every edge that can be).</summary>
    public DerivationAmount? Amount { get; set; }

    /// <summary>DeleteNodes, Nudge: which nodes may be picked. Default Any.</summary>
    public DerivationTarget? Target { get; set; }

    /// <summary>DeleteNodes: Cut (default) or Bridge.</summary>
    public DeleteNodeMode? Mode { get; set; }

    /// <summary>DeleteNodes with Mode Bridge: the moves a replacement path may be built from. Same options as
    /// chain.connectionRules (Circle rules are ignored).</summary>
    public List<ConnectionRuleConfig>? ConnectionRules { get; set; }

    /// <summary>DeleteNodes with Mode Bridge: most moves a replacement path may have. Default 3.</summary>
    public int? BridgeMaxMoves { get; set; }

    /// <summary>DeleteNodes, DeleteEdges, Keep: also remove nodes left with no edge. Default true. If false they
    /// stay as lone nodes (invisible when drawn, but they still count as nodes for later steps, for
    /// similarity, and in the debug view).</summary>
    public bool? RemoveOrphanNodes { get; set; }

    /// <summary>DeleteNodes, DeleteEdges, Keep: never end with fewer edges than this (picks that would are
    /// skipped). Default 1.</summary>
    public int? KeepAtLeast { get; set; }

    /// <summary>DeleteNodes, DeleteEdges, Keep: skip any pick that would split the glyph into more connected
    /// pieces than it had before the step. Default false.</summary>
    public bool? KeepConnected { get; set; }

    /// <summary>Add: ids of entries of the language's shapes, each placed as it would be in a plain glyph
    /// (its count, chain, placement and arrangement), against the strokes that are left. Added strokes may cross
    /// the existing ones only if the shape's arrangement.crossing says so.</summary>
    public List<string>? Shapes { get; set; }

    /// <summary>Add: if a shape cannot be placed, skip it instead of failing. Default false.</summary>
    public bool? Optional { get; set; }

    /// <summary>Scale: weighted ratios, one is picked. A ratio that would not fit the grid is not picked.</summary>
    public List<ScaleFactorConfig>? Factors { get; set; }

    /// <summary>Mirror: default Horizontal.</summary>
    public MirrorAxis? Axis { get; set; }

    /// <summary>Rotate: the allowed rotations (90, 180, 270); one is picked. Default all three.</summary>
    public List<int>? Degrees { get; set; }

    /// <summary>Nudge: how far a node may move on each axis. Default 1.</summary>
    public int? Distance { get; set; }

    /// <summary>SplitEdges: into how many pieces each chosen edge is cut, drawn per edge from the range (2 = one new node). Default 2.</summary>
    public IntRange? Parts { get; set; }

    /// <summary>SplitEdges: Equal (default) or Random places along the edge.</summary>
    public SplitDistribution? Distribution { get; set; }

    /// <summary>SplitEdges: Exact (default, only nodes lying on the edge) or Nearest (approximate, see maxDeviation).</summary>
    public SplitSnap? Snap { get; set; }

    /// <summary>SplitEdges with Snap Nearest: a node whose nearest lattice point is further than this from the original edge is not
    /// used. Default 0.75 (about the most a rounded point can be off).</summary>
    public double? MaxDeviation { get; set; }

    /// <summary>SplitEdges: only edges at least this long (in lattice units) are split. Default 2.</summary>
    public double? MinEdgeLength { get; set; }
}

/// <summary>
/// A glyph made from another glyph: the source is generated first, then the steps edit a copy of its strokes
/// (a composite source is flattened to plain strokes). The source itself is left as it was. Edges that survive
/// untouched keep the source's exact curves.
/// </summary>
public sealed class DerivationConfig
{
    /// <summary>When the derived glyph is drawn at its source's render.rotateOptions angle instead of drawing its own.
    /// Only steps that actually ran count (one skipped by chance or onlyIf does not).</summary>
    public DerivedRotation FinalOutputRotation { get; set; } = DerivedRotation.FollowMoves;

    /// <summary>The glyph (a name from glyphs or baseGlyphs; plain, composite or itself derived) to start from.</summary>
    public string From { get; set; } = "";

    /// <summary>The edits, run in order.</summary>
    public List<DerivationStep> Steps { get; set; } = new();

    /// <summary>Quality gate against the source only: the derived glyph may resemble it by at most this much
    /// (0-1, same measure as quality.maxSimilarity), so it can't be a copy. 1 turns it off. Against every OTHER glyph the
    /// normal quality.maxSimilarity applies. Default 0.9.</summary>
    public double MaxSimilarityToSource { get; set; } = 0.9;
}
