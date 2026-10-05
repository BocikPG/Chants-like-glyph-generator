namespace GlyphGen.Core.Config;

public enum ConnectionDirection
{
    Orthogonal,
    Diagonal,
    Both,
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
