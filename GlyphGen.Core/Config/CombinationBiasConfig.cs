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
                errors.Add($"{nameof(Modes)} contains an unknown value ({(int)mode}).");
        }

        if (modes.Contains(CombinationBiasMode.Tallest) && modes.Contains(CombinationBiasMode.Shortest))
            errors.Add($"{nameof(Modes)} lists both Tallest and Shortest - they cancel each other out.");
        if (modes.Contains(CombinationBiasMode.Widest) && modes.Contains(CombinationBiasMode.Narrowest))
            errors.Add($"{nameof(Modes)} lists both Widest and Narrowest - they cancel each other out.");

        return errors;
    }
}
