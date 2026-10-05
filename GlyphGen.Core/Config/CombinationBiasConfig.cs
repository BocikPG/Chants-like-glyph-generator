namespace GlyphGen.Core.Config;

/// <summary>One thing a composite glyph can be nudged towards when components are combined.
/// "Size" is the bounding box of the combined nodes, in grid units.</summary>
public enum CombinationBiasMode
{
    Tallest,
    Shortest,
    Widest,
    Narrowest,
    /// <summary>Width and height as close to equal as possible (by ratio, so it holds at any size).</summary>
    Squarest,
}

/// <summary>
/// A preference, never a requirement: the bias only chooses between placements that already
/// satisfy every other constraint (grid bounds, no crossing, sub-symbol spacing, allowed
/// scales/rotations). If only one placement is legal, that one is used whatever it does to the
/// shape. Components are placed one after another, so with three or more components the bias is
/// applied greedily - each is placed to best serve the shape of what is already there.
///
/// Several modes can be active at once; they are blended with equal weight (each is scored on a
/// 0..1 scale relative to the grid, then the scores are added). E.g. Shortest + Narrowest asks
/// for the smallest overall box, Tallest + Narrowest for tall and thin, Tallest + Widest for the
/// biggest, Squarest + Shortest for a compact square. Opposites (Tallest + Shortest, Widest +
/// Narrowest) cancel out and are rejected by validation.
/// </summary>
public sealed class CombinationBiasConfig
{
    /// <summary>Empty = no bias: the first legal placement found is used (original behaviour).</summary>
    public List<CombinationBiasMode> Modes { get; set; } = new();

    public bool IsActive => Modes is { Count: > 0 };

    /// <summary>
    /// Optional weight per listed mode (default 1 for any mode not mentioned), e.g.
    /// { "Tallest": 3, "Squarest": 1 } to care three times as much about height as about
    /// squareness. A mode's score is multiplied by its weight before the scores are added; only
    /// the ratios matter. Weights for modes that aren't in Modes are rejected, as is giving
    /// every listed mode weight 0.
    /// </summary>
    public Dictionary<CombinationBiasMode, double> Weights { get; set; } = new();

    public double WeightOf(CombinationBiasMode mode) =>
        Weights is not null && Weights.TryGetValue(mode, out var weight) ? weight : 1.0;

    /// <summary>false: bias only picks among TRANSLATIONS, at the first scale/rotation that has
    /// any legal placement (the existing "translate first, scale/rotate only as a fallback"
    /// order is kept). true: every allowed scale/rotation is also in the running, so e.g.
    /// Tallest can pick a larger scale or a 90-degree turn - strongest effect, but it overrides
    /// the translate-first preference.</summary>
    public bool IncludeScaleAndRotation { get; set; } = false;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var modes = Modes ?? new List<CombinationBiasMode>();

        foreach (var mode in modes)
        {
            if (!Enum.IsDefined(mode))
                errors.Add($"{JsonNames.Of(nameof(Modes))} contains an unknown value ({(int)mode}).");
        }

        if (modes.Contains(CombinationBiasMode.Tallest) && modes.Contains(CombinationBiasMode.Shortest))
            errors.Add($"{JsonNames.Of(nameof(Modes))} lists both Tallest and Shortest - they cancel each other out.");
        if (modes.Contains(CombinationBiasMode.Widest) && modes.Contains(CombinationBiasMode.Narrowest))
            errors.Add($"{JsonNames.Of(nameof(Modes))} lists both Widest and Narrowest - they cancel each other out.");

        foreach (var (mode, weight) in Weights ?? new Dictionary<CombinationBiasMode, double>())
        {
            if (!modes.Contains(mode))
                errors.Add($"{JsonNames.Of(nameof(Weights))} has an entry for {mode}, which is not in Modes, so it would be ignored.");
            if (!double.IsFinite(weight) || weight < 0)
                errors.Add($"{JsonNames.Of(nameof(Weights))}[{mode}] must be a finite number >= 0 (got {weight}).");
        }

        if (modes.Count > 0 && modes.All(m => WeightOf(m) == 0))
            errors.Add($"Every listed mode has weight 0, so the bias would do nothing.");

        return errors;
    }
}
