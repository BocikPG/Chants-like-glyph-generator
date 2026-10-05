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

    /// <summary>Set when this chain came from a guaranteedMotifs entry; null for free-form
    /// language-level sub-symbols.</summary>
    public string? MotifId { get; set; }
}
