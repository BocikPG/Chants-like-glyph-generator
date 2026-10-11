namespace GlyphGen.Core.Config;

/// <summary>
/// How ONE stroke (chain) grows: its length, the weighted connection rules, how those weights
/// become probabilities, whether it may revisit nodes/edges or close into a loop, and the
/// spacing between its own nodes. Used by every Generative shape - there is no language-wide copy,
/// so two shapes can follow different rules (long strokes and short ticks, say) and nothing is
/// inherited silently. Ignored for Literal shapes.
/// </summary>
public sealed class ChainRules
{
    /// <summary>How many nodes the chain should have (including the start node).</summary>
    public IntRange Length { get; set; } = new() { Min = 3, Max = 6 };

    /// <summary>Weighted connection rules driving the chain's growth at each step. At least one is
    /// required, otherwise no chain can grow.</summary>
    public List<ConnectionRuleConfig> ConnectionRules { get; set; } = new();

    /// <summary>How connection-rule weights turn into move probabilities. See ConnectionWeighting.</summary>
    public ConnectionWeighting ConnectionWeighting { get; set; } = ConnectionWeighting.PerMove;

    /// <summary>Whether the chain may revisit nodes already in it.</summary>
    public bool AllowNodeRevisit { get; set; } = false;

    /// <summary>Whether the chain may revisit edges already in it.</summary>
    public bool AllowEdgeRevisit { get; set; } = false;

    /// <summary>Whether the chain may close into a loop back to its start node. How likely that is
    /// is set per rule (connectionRules[].backToStartChance).</summary>
    public bool AllowClosedLoop { get; set; } = false;

    /// <summary>Node-to-node spacing rules inside this chain, all enforced together as each node
    /// is added, on top of the ordinary intersection check. Empty = none.</summary>
    public List<SelfSpacingRule> SelfSpacing { get; set; } = new();
}
