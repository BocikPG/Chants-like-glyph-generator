namespace GlyphGen.Core.Config;

/// <summary>How an axis pattern picks coordinates.</summary>
public enum AxisPatternMode
{
    /// <summary>Every <c>step</c>-th coordinate, starting at <c>offset</c> (step 3, offset 1: 1, 4, 7, ...).</summary>
    Every,
    /// <summary>Odd coordinates (1, 3, 5, ...).</summary>
    Odd,
    /// <summary>Even coordinates (0, 2, 4, ...).</summary>
    Even,
}

/// <summary>Which values of one coordinate (x or y) match.</summary>
public sealed class AxisPattern
{
    public AxisPatternMode Mode { get; set; } = AxisPatternMode.Every;

    /// <summary>Every: the spacing between matching coordinates (1 = all). Default 1.</summary>
    public int Step { get; set; } = 1;

    /// <summary>Every: the first matching coordinate (counted from 0; 1 with step 3 = 1, 4, 7, ...). Default 0.</summary>
    public int Offset { get; set; } = 0;

    public bool Matches(int value) => Mode switch
    {
        AxisPatternMode.Odd => value % 2 != 0,
        AxisPatternMode.Even => value % 2 == 0,
        _ => (((value - Offset) % Math.Max(1, Step)) + Math.Max(1, Step)) % Math.Max(1, Step) == 0,
    };
}

/// <summary>Nodes picked by their coordinates: a node matches when its x matches <c>x</c> AND its y matches <c>y</c>
/// (an axis left out matches everything).</summary>
public sealed class NodePattern
{
    public AxisPattern? X { get; set; }
    public AxisPattern? Y { get; set; }
}

/// <summary>The box around the nodes of other shapes, grown by <c>margin</c> on every side.</summary>
public sealed class ShapesBox
{
    /// <summary>Ids of the shapes (already placed in the glyph when this shape is placed).</summary>
    public List<string> Shapes { get; set; } = new();

    /// <summary>Lattice units added on every side. Default 0.</summary>
    public int Margin { get; set; } = 0;
}

/// <summary>One set of nodes. Exactly one of the fields is set.</summary>
public sealed class NodeRegion
{
    /// <summary>These nodes, as [x, y] pairs.</summary>
    public List<GlyphGen.Core.Grid.GridPoint>? Points { get; set; }

    /// <summary>Every node inside this inclusive box.</summary>
    public PlacementBounds? Box { get; set; }

    /// <summary>Nodes picked by pattern (odd / even / every n-th x and y).</summary>
    public NodePattern? Pattern { get; set; }

    /// <summary>The nodes of these shapes (the node pool): only nodes that already exist in the glyph.</summary>
    public List<string>? NodesOfShapes { get; set; }

    /// <summary>Everything inside the box around the nodes of these shapes.</summary>
    public ShapesBox? BoundsOfShapes { get; set; }

    internal int KindCount =>
        (Points is null ? 0 : 1) + (Box is null ? 0 : 1) + (Pattern is null ? 0 : 1) + (NodesOfShapes is null ? 0 : 1) + (BoundsOfShapes is null ? 0 : 1);
}

/// <summary>placement.nodes: which lattice nodes a shape may use (every node of its strokes, and where it starts). A node is usable
/// when it is in at least one <c>allow</c> region (or <c>allow</c> is empty) and in no <c>forbid</c> region, and inside
/// <c>placement.bounds</c> and the grid.</summary>
public sealed class NodeMaskConfig
{
    public List<NodeRegion>? Allow { get; set; }
    public List<NodeRegion>? Forbid { get; set; }
}
