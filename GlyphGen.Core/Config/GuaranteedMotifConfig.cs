namespace GlyphGen.Core.Config;

public enum MotifType
{
    /// <summary>Fixed relative-coordinate shape, identical every time it appears.</summary>
    Literal,

    /// <summary>Generated via the same chain engine as free-form sub-symbols, just run first
    /// and with its own constrained rule set — still varied, but guaranteed-present.</summary>
    Generative,
}

/// <summary>Fixed shape, always identical. Coordinates are relative to wherever Placement
/// puts the motif's origin.</summary>
public sealed class LiteralMotifConfig
{
    public List<int[]> Nodes { get; set; } = new();
}

/// <summary>Constrained generation, reusing the normal chain-growth engine with its own
/// length range / connection rules / loop settings, independent of the language's regular
/// free-form sub-symbol settings.</summary>
public sealed class GenerativeMotifConfig
{
    public IntRange LengthRange { get; set; } = new() { Min = 3, Max = 6 };
    public List<ConnectionRuleConfig> ConnectionRules { get; set; } = new();
    public bool AllowClosedLoop { get; set; } = false;
    public double ClosedLoopWeight { get; set; } = 0.0;
}

/// <summary>
/// One guaranteed motif for a language (e.g. Bard's "bar"). Exactly one of Literal/Generative
/// should be populated, matching Type — kept as an explicit enum flag rather than relying on
/// null-inference alone, so a config with both or neither populated is a validation error
/// rather than a silent bug.
/// </summary>
public sealed class GuaranteedMotifConfig
{
    public string Id { get; set; } = "";
    public MotifType Type { get; set; }
    public LiteralMotifConfig? Literal { get; set; }
    public GenerativeMotifConfig? Generative { get; set; }
    public PlacementConfig Placement { get; set; } = new();
}
