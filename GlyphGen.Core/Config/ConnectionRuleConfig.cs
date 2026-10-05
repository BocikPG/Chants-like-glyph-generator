namespace GlyphGen.Core.Config;

public enum ConnectionDirection
{
    Orthogonal,
    Diagonal,
    Both,
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
/// (in grid units, 1 = adjacent), and how likely it is to be picked relative to the
/// language's other templates. A language with only one entry still works fine — it's
/// just used 100% of the time since there's nothing to weigh it against.
/// </summary>
public sealed class ConnectionRuleConfig
{
    public ConnectionDirection Direction { get; set; }
    public int MinLength { get; set; } = 1;
    public int MaxLength { get; set; } = 1;
    public double Weight { get; set; } = 1.0;
}
