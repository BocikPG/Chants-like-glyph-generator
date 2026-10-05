using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

public sealed class ChainGenerationRequest
{
    public GridPoint StartNode { get; init; }
    public int TargetLength { get; init; }
    public List<ConnectionRuleConfig> ConnectionRules { get; init; } = new();
    public bool AllowNodeRevisit { get; init; }
    public bool AllowEdgeRevisit { get; init; }
    public bool AllowClosedLoop { get; init; }
    public double ClosedLoopWeight { get; init; }
    public ConnectionWeighting ConnectionWeighting { get; init; } = ConnectionWeighting.PerMove;
    /// <summary>Node-to-node spacing rules within this chain; a new node must satisfy all.</summary>
    public IReadOnlyList<SelfSpacingRule> SelfSpacingRules { get; init; } = Array.Empty<SelfSpacingRule>();
}

/// <summary>
/// Grows a single self-avoiding chain per the agreed design: a linear tail only, no
/// frontier/branching. At each step a candidate connection is drawn from the weighted
/// ConnectionRules; if no candidate from the current tail is valid, the chain simply ends
/// early rather than backtracking past already-committed nodes (that's the caller's job,
/// via a fresh attempt seed and a full retry).
/// </summary>
public interface IChainGenerator
{
    /// <summary>Returns null if the chain ended before reaching TargetLength. The caller
    /// (glyph generator) is responsible for deriving the next attempt seed via SeedTree and
    /// retrying the whole chain from its start node.</summary>
    SubSymbolChain? TryGenerateChain(
        LatticeGrid grid,
        ChainGenerationRequest request,
        DeterministicRandom rng,
        IIntersectionContext existingGeometry);
}
