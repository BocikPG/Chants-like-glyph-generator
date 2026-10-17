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

/// <summary>direction Arc: which part of the circle is drawn. The circle is set like a Circle rule's (its diameter runs from the
/// current node to the node 'length' steps away); the arc starts at an angle of the preset, sweeps clockwise by a multiple
/// of the preset's step, and so also ends on an angle of the preset.</summary>
public sealed class ArcConfig
{
    /// <summary>Angles the arc may start (and end) at. Default Cardinal.</summary>
    public AngleSet Angles { get; set; } = AngleSet.Cardinal;

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
/// shape for spacing, startOnExistingNode and NodesOnly crossings; the chain itself still continues from the far node.</summary>
public sealed class OutlineNodesConfig
{
    public OutlineNodeMode Mode { get; set; } = OutlineNodeMode.None;

    /// <summary>AtAngles: which angles get a node. Default Cardinal.</summary>
    public AngleSet Angles { get; set; } = AngleSet.Cardinal;

    /// <summary>AtAngles: how a point between lattice points is handled. Default Within.</summary>
    public AngleNodeSnap Snap { get; set; } = AngleNodeSnap.Within;

    /// <summary>How close (lattice units) the outline must pass to a lattice point for Within and EveryLatticePoint. Default 0.3.</summary>
    public double Tolerance { get; set; } = 0.3;
}
