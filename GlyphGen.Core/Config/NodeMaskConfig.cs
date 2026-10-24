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
    /// <summary>Which coordinates match: Every (every step-th, from offset), Odd or Even.</summary>
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
    /// <summary>Which x coordinates match. Left out = all.</summary>
    public AxisPattern? X { get; set; }
    /// <summary>Which y coordinates match. Left out = all.</summary>
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

/// <summary>The nodes of components already placed in the composite (combination placements only).</summary>
public sealed class ComponentNodes
{
    /// <summary>Glyph names of the components whose nodes count. Left out = every component placed so far.</summary>
    public List<string>? Components { get; set; }

    /// <summary>Only the nodes of these shapes (ids from <c>shapes</c>) inside those components. Left out = all their nodes.</summary>
    public List<string>? Shapes { get; set; }
}

/// <summary>The nodes of another glyph, moved by an offset (shape masks only).</summary>
public sealed class GlyphNodes
{
    /// <summary>The glyph (from glyphs or baseGlyphs) whose nodes are used. It is drawn before the glyph this shape is drawn in, and drawn
    /// again (with this glyph after it) whenever it is redrawn. "*" = every other glyph that is not a composite and comes earlier in
    /// baseGlyphs / glyphs.</summary>
    public string Glyph { get; set; } = "";

    /// <summary>Added to every node of that glyph, as [x, y]: where the glyph sits relative to this one. Default [0, 0].</summary>
    public GlyphGen.Core.Grid.GridPoint? Offset { get; set; }

    /// <summary>"*" for every earlier glyph.</summary>
    public const string AllGlyphs = "*";
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

    /// <summary>Combination placements only: the nodes of components placed earlier, optionally only those of some shapes inside them.</summary>
    public ComponentNodes? NodesOfComponents { get; set; }

    /// <summary>Shape masks only: the nodes of another glyph (see glyph), moved by offset - for a shape that should start or end where
    /// another glyph has a node. The other glyph is drawn first.</summary>
    public GlyphNodes? NodesOfGlyph { get; set; }

    internal int KindCount =>
        (Points is null ? 0 : 1) + (Box is null ? 0 : 1) + (Pattern is null ? 0 : 1) + (NodesOfShapes is null ? 0 : 1) + (BoundsOfShapes is null ? 0 : 1)
        + (NodesOfComponents is null ? 0 : 1) + (NodesOfGlyph is null ? 0 : 1);
}

/// <summary>placement.nodes: which lattice nodes a shape may use (every node of its strokes, and where it starts). A node is usable
/// when it is in at least one <c>allow</c> region (or <c>allow</c> is empty) and in no <c>forbid</c> region, and inside
/// <c>placement.bounds</c> and the grid.</summary>
public sealed class NodeMaskConfig
{
    /// <summary>Regions a node may be in. A node is usable when it is in at least one of them (no list = everywhere).</summary>
    public List<NodeRegion>? Allow { get; set; }
    /// <summary>Regions a node may not be in. A node in any of them is not usable.</summary>
    public List<NodeRegion>? Forbid { get; set; }
}
