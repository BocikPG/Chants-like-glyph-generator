using GlyphGen.Core.Grid;

namespace GlyphGen.Core.Model;

/// <summary>
/// A single generated chain — the atomic unit produced by the chain-growth engine.
/// Always linear (start -> ... -> tail, optionally closing back to start for a loop);
/// never branches. Tree/junction-like appearances only ever emerge from multiple chains
/// sharing grid points, never from branching logic inside one chain.
/// </summary>
public sealed class SubSymbolChain
{
    public List<GridPoint> Nodes { get; } = new();
    public List<GridEdge> Edges { get; } = new();
    public bool IsClosedLoop { get; set; }

    /// <summary>Every node of the shape: the chain's own, plus the extra nodes along its circles and arcs (OutlineNodes). These are what
    /// spacing, startOnExistingNode and NodesOnly crossings see; <see cref="Nodes"/> alone is what the chain walks through.</summary>
    public List<GridPoint> AllNodes()
    {
        var all = new List<GridPoint>(Nodes);
        foreach (var edge in Edges)
            foreach (var node in edge.ExtraNodes())
                if (!all.Contains(node)) all.Add(node);
        return all;
    }

    /// <summary>Set when this chain came from a guaranteedMotifs entry; null for free-form
    /// language-level sub-symbols.</summary>
    public string? MotifId { get; set; }

    /// <summary>Which shape drew this chain and which instance of it: "id#instance" (a copy carries its own shape id and
    /// its original's instance number). Only for debug colouring; null for chains that came from elsewhere.</summary>
    public string? ShapeTag { get; set; }
}
