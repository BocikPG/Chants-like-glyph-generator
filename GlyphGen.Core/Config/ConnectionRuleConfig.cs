namespace GlyphGen.Core.Config;

/// <summary>Which moves a connection rule may make from the current node.</summary>
public enum ConnectionDirection
{
    /// <summary>The 4 straight directions (up, down, left, right).</summary>
    Orthogonal,

    /// <summary>The 4 diagonals.</summary>
    Diagonal,

    /// <summary>All 8 of the above.</summary>
    Both,

    /// <summary>Any lattice node, at any angle. Length is the Chebyshev distance max(|dx|, |dy|), so
    /// length 1 is the same 8 neighbours as Both and longer lengths add the knight-move-style jumps
    /// in between. Combine with minAngle/maxAngle/anglePreset to limit the turn.</summary>
    Any,

    /// <summary>Like Any, but meant to be limited by an angle: needs minAngle/maxAngle or an anglePreset.</summary>
    Angle,

    /// <summary>Draws a circle instead of a line. From the current node the circle runs to the node
    /// 'length' steps away in one of the 8 directions, and that pair of points is its DIAMETER; the
    /// chain then carries on from the far node. A diagonal circle of length L has diameter L*sqrt(2).
    /// minLength may be 0: length 0 is a dot on the current node (no circle, the chain continues from
    /// the same node). Angle limits do not apply.</summary>
    Circle,

    /// <summary>Draws part of a circle: the circle is set like a Circle's (diameter from the current node to the node 'length'
    /// steps away, minLength at least 1) but only an arc of it is drawn, picked at random from the 'arc' settings. The chain
    /// carries on from the far node, which the drawn arc may or may not reach. Angle limits do not apply.</summary>
    Arc,
}

/// <summary>Ready-made turn ranges for a rule's angle limit (instead of minAngle/maxAngle).</summary>
public enum AnglePreset
{
    /// <summary>No preset: minAngle/maxAngle are used.</summary>
    None,

    /// <summary>Carry on in a line: 170-180.</summary>
    Straight,

    /// <summary>A gentle bend: 120-170.</summary>
    Gentle,

    /// <summary>About a right angle: 80-100.</summary>
    Right,

    /// <summary>A sharp turn back on itself: 0-60.</summary>
    Sharp,
}

/// <summary>How a rule's Weight is spread over the concrete moves it expands into.</summary>
public enum ConnectionWeighting
{
    /// <summary>Every concrete move (each direction x length a rule allows) carries the rule's full
    /// Weight, so a rule with many valid moves ends up with more total probability than a narrow
    /// one of equal Weight. The original behaviour.</summary>
    PerMove,

    /// <summary>A rule's Weight is split evenly across its valid moves at each step, so the rule's
    /// total probability is Weight relative to the other rules' Weights, however many moves each
    /// has (a rule with no valid move at that step simply drops out).</summary>
    PerRule,
}

/// <summary>
/// One weighted connection template a chain can grow with: what direction, how far
/// (in grid units, 1 = adjacent), how likely it is to be picked relative to the
/// stroke's other templates, and how the edges it makes are drawn. A stroke with only one entry
/// still works fine - it's just used 100% of the time since there's nothing to weigh it against.
/// </summary>
public sealed class ConnectionRuleConfig
{
    /// <summary>The kind of move this rule makes: a line (Orthogonal, Diagonal, Both, Any, Angle), a Circle, or an Arc.</summary>
    public ConnectionDirection Direction { get; set; }
    /// <summary>Shortest move, in lattice units (1 = the neighbouring node). For a Circle it is the diameter, where 0 is a dot. Default 1.</summary>
    public int MinLength { get; set; } = 1;
    /// <summary>Longest move, in lattice units (for a Circle, the diameter). Must not be below minLength. Default 1.</summary>
    public int MaxLength { get; set; } = 1;
    /// <summary>How likely this rule is to be picked relative to the chain's other rules (see chain.connectionWeighting). 0 = never. Default 1.</summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>Smallest allowed turn, in degrees, measured between the previous edge and the new one
    /// at the current node: 180 = carry straight on, 90 = right angle, 0 = fold back. The first edge of
    /// a stroke has nothing to turn from, so it is not limited. Applies to every direction except Circle.</summary>
    public double MinAngle { get; set; } = 0;

    /// <summary>Largest allowed turn, in degrees (see MinAngle).</summary>
    public double MaxAngle { get; set; } = 180;

    /// <summary>Shortcut for a minAngle/maxAngle pair; when set, it replaces them.</summary>
    public AnglePreset AnglePreset { get; set; } = AnglePreset.None;

    /// <summary>Chance (0-100), each time this rule is the one picked, that the stroke instead goes back to
    /// its starting node and closes into a loop. Needs chain.allowClosedLoop and at least 3 nodes so far,
    /// and the closing edge must be free of crossings, otherwise the rule just makes its normal move.
    /// Replaces chain.closedLoopWeight.</summary>
    public double BackToStartChance { get; set; } = 0;

    /// <summary>How much of the chain's length this rule uses up each time it is drawn (default 1, which is one node, as before).
    /// A chain is complete once its used length reaches chain.length, so a rule costing 2 makes a chain 'shorter' by one extra node
    /// every time it is drawn - e.g. a circle with cost 2 leaves room for one stroke fewer. 0 makes the rule free: it never counts
    /// towards the length (a chain is still stopped at 64 nodes). The start node always counts 1. Closing a loop does not cost.</summary>
    public int ChainLengthCost { get; set; } = 1;

    /// <summary>direction Arc only: which arcs may be drawn.</summary>
    public ArcConfig Arc { get; set; } = new();

    /// <summary>direction Circle or Arc: extra nodes along the outline.</summary>
    public OutlineNodesConfig OutlineNodes { get; set; } = new();

    /// <summary>Straight rules only (not Circle or Arc). Present = the edges this rule draws are cut into pieces by extra nodes on them, the
    /// way a SplitEdges derivation step does it. The nodes become real nodes of the chain (spacing, start.onExistingNode, crossings and
    /// later shapes all see them) but cost no chain length. Left out = edges stay in one piece.</summary>
    public EdgeSplitConfig? Split { get; set; }

    /// <summary>How the edges this rule makes are bent when drawn.</summary>
    public EdgeCurveConfig Curve { get; set; } = new();

    /// <summary>The turn range in degrees this rule allows (the preset's, or MinAngle..MaxAngle).</summary>
    public (double Min, double Max) ResolveAngleRange() => AnglePreset switch
    {
        AnglePreset.Straight => (170, 180),
        AnglePreset.Gentle => (120, 170),
        AnglePreset.Right => (80, 100),
        AnglePreset.Sharp => (0, 60),
        _ => (MinAngle, MaxAngle),
    };

    /// <summary>True when the rule restricts the turn at all.</summary>
    public bool LimitsAngle
    {
        get
        {
            var (min, max) = ResolveAngleRange();
            return min > 0 || max < 180;
        }
    }
}

/// <summary>What a connection rule's split does with the edge it cuts.</summary>
public enum SplitMode
{
    /// <summary>The edge becomes several edges that meet at the new nodes.</summary>
    Pieces,

    /// <summary>The edge stays whole; the new nodes are only added on it.</summary>
    ExtraNodes,
}

/// <summary>connectionRules[].split: cutting the edges a rule draws into pieces by adding nodes on them (like the SplitEdges step).</summary>
public sealed class EdgeSplitConfig
{
    /// <summary>Pieces (default): the edge is replaced by several edges meeting at the new nodes, which become nodes of the chain.
    /// ExtraNodes: the edge is left exactly as drawn (one edge, one stroke) and the new nodes are only added on it, like the outline
    /// nodes of a circle: they count for spacing, start.onExistingNode, node masks and NodesOnly crossings, but the chain does not walk
    /// through them and a curve is planned for the whole edge. With snap Nearest the nodes may lie slightly beside the edge (within maxDeviation); the edge itself is not bent.</summary>
    public SplitMode Mode { get; set; } = SplitMode.Pieces;

    /// <summary>Chance (0-100) that an edge this rule draws is cut at all, rolled per edge. Default 100.</summary>
    public double Chance { get; set; } = 100.0;

    /// <summary>Into how many pieces an edge is cut, drawn per edge from the range (2 = one new node). Default 2.</summary>
    public IntRange Parts { get; set; } = new() { Min = 2, Max = 2 };

    /// <summary>Equal (default): as evenly as the lattice allows. Random: at random places along the edge.</summary>
    public SplitDistribution Distribution { get; set; } = SplitDistribution.Equal;

    /// <summary>Exact (default): only lattice points the edge really passes over are used; an edge that passes over none is left whole.
    /// Nearest: the ideal place is moved to the nearest lattice point, so the edge becomes a slightly bent path (see maxDeviation).</summary>
    public SplitSnap Snap { get; set; } = SplitSnap.Exact;

    /// <summary>Snap Nearest only: a node whose nearest lattice point is further than this from the edge is not used. Default 0.75.</summary>
    public double MaxDeviation { get; set; } = 0.75;

    /// <summary>Only edges at least this long (lattice units) are cut. Default 2.</summary>
    public double MinEdgeLength { get; set; } = 2.0;
}
