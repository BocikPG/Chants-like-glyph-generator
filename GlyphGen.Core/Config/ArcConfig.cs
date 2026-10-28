namespace GlyphGen.Core.Config;

/// <summary>Which angles (degrees, 0 = right, growing clockwise on screen) a preset stands for.</summary>
public enum AngleSet
{
    /// <summary>0, 90, 180, 270.</summary>
    Cardinal,

    /// <summary>45, 135, 225, 315.</summary>
    Diagonal,

    /// <summary>All eight of the above.</summary>
    Both,
}

public static class AngleSets
{
    public static int[] Angles(AngleSet set) => set switch
    {
        AngleSet.Cardinal => new[] { 0, 90, 180, 270 },
        AngleSet.Diagonal => new[] { 45, 135, 225, 315 },
        AngleSet.Both => new[] { 0, 45, 90, 135, 180, 225, 270, 315 },
        _ => throw new NotSupportedException($"Unknown angle set: {set}"),
    };

    /// <summary>The smallest turn between two angles of the set that keeps both in it (90, or 45 for Both).</summary>
    public static int Step(AngleSet set) => set == AngleSet.Both ? 45 : 90;
}

/// <summary>Where an arc sits on its circle relative to the nodes it is drawn between.</summary>
public enum ArcAnchor
{
    /// <summary>The arc starts at any angle of the preset, so it need not touch the current node (the original behaviour).</summary>
    None,

    /// <summary>One end of the arc is the current node: it either starts there or ends there (either way round), and the sweep is
    /// drawn from minSweep..maxSweep. The sweep goes in steps of 90 degrees (45 when angles is Both); the preset's angles themselves are not used. The far node is still where the chain carries on from.</summary>
    Node,

    /// <summary>A half circle between the current node and the far node, bulging to either side. minSweep, maxSweep and the angles are
    /// not used. The arc always connects the two nodes.</summary>
    Diameter,
}

/// <summary>Where the chain carries on after an arc.</summary>
public enum ArcContinueFrom
{
    /// <summary>From the far end of the circle's diameter (the node 'length' steps away). With anchor Node that node need not lie on the
    /// arc. This is the default.</summary>
    FarNode,

    /// <summary>From the node the arc itself ends at (the nearest lattice node to its free end, within endTolerance). Needs anchor Node:
    /// the arc starts or ends on the current node, so the chain really continues along the arc. Arcs whose end is not close enough to a
    /// node are not offered.</summary>
    ArcEnd,
}

/// <summary>direction Arc: which part of the circle is drawn. The circle is set like a Circle rule's (its diameter runs from the
/// current node to the node 'length' steps away); the arc starts at an angle of the preset, sweeps clockwise by a multiple
/// of the preset's step, and so also ends on an angle of the preset.</summary>
public sealed class ArcConfig
{
    /// <summary>Angles the arc may start (and end) at. Default Cardinal.</summary>
    public AngleSet Angles { get; set; } = AngleSet.Cardinal;

    /// <summary>Whether the arc has to touch the current node (see the values of `anchor`). Default None.</summary>
    public ArcAnchor Anchor { get; set; } = ArcAnchor.None;

    /// <summary>Where the chain carries on after the arc (see the values). Needs anchor Node. Default FarNode.</summary>
    public ArcContinueFrom ContinueFrom { get; set; } = ArcContinueFrom.FarNode;

    /// <summary>continueFrom ArcEnd: how far (lattice units) the arc's end may be from the node the chain continues from. 0 = the arc must
    /// end exactly on a node. A larger value admits more arcs; the next stroke then starts from the node, up to this far from where the arc
    /// stops. Default 0.</summary>
    public double EndTolerance { get; set; } = 0.0;

    /// <summary>Smallest sweep in degrees (a multiple of the preset's step: 90, or 45 for Both). 180 = a half circle. Default 90.</summary>
    public int MinSweep { get; set; } = 90;

    /// <summary>Largest sweep in degrees, below 360. Default 270.</summary>
    public int MaxSweep { get; set; } = 270;
}

public enum OutlineNodeMode
{
    /// <summary>No extra nodes (the default).</summary>
    None,

    /// <summary>A node at each angle of the preset that the circle or arc covers.</summary>
    AtAngles,

    /// <summary>A node on every lattice point the outline passes within the tolerance of.</summary>
    EveryLatticePoint,
}

/// <summary>For AtAngles: nodes are lattice points, so a point of the outline that lies between lattice points is handled by the snap.</summary>
public enum AngleNodeSnap
{
    /// <summary>Put the node on the nearest lattice point if it is within the tolerance; otherwise skip that angle.</summary>
    Within,

    /// <summary>Always put the node on the nearest lattice point, however far it is.</summary>
    Nearest,

    /// <summary>Only where the point is exactly on a lattice point.</summary>
    Exact,
}

/// <summary>Extra nodes along a circle or arc (on a connection rule with direction Circle or Arc). They count as nodes of the
/// shape for spacing, start.onExistingNode and NodesOnly crossings; the chain itself still continues from the far node.</summary>
public sealed class OutlineNodesConfig
{
    /// <summary>Which extra lattice points on a circle or arc count as its nodes: None, AtAngles or EveryLatticePoint.</summary>
    public OutlineNodeMode Mode { get; set; } = OutlineNodeMode.None;

    /// <summary>AtAngles: which angles get a node. Default Cardinal.</summary>
    public AngleSet Angles { get; set; } = AngleSet.Cardinal;

    /// <summary>AtAngles: how a point between lattice points is handled. Default Within.</summary>
    public AngleNodeSnap Snap { get; set; } = AngleNodeSnap.Within;

    /// <summary>How close (lattice units) the outline must pass to a lattice point for Within and EveryLatticePoint. Default 0.3.</summary>
    public double Tolerance { get; set; } = 0.3;
}
