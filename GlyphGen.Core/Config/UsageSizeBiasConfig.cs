namespace GlyphGen.Core.Config;

/// <summary>
/// Counteracts a size skew towards heavily-used component glyphs: the more recipes a plain glyph
/// appears in, the more likely it is to roll LOW values for its sub-symbol count and sub-symbol
/// lengths. (Components used rarely are the ones regenerated most while composites are being
/// fitted, and what survives that is biased towards smaller, easier-to-place shapes - so the
/// untouched, frequently-used ones end up bigger on average.)
///
/// Mechanism: each use adds Strength "extra draws" - the glyph rolls its count/length that many
/// extra times (the usual range, the usual rules) and keeps the lowest result. A NEGATIVE strength
/// does the opposite: it rolls as many extra times as the same positive strength would, but keeps the
/// HIGHEST result (-1 pushes frequently-used glyphs towards big, as 1 pushes them towards small). A fractional
/// number of extra draws is honoured probabilistically. The result is always inside the
/// configured min..max, so this only reshapes the odds; it never overrides a range or any other
/// constraint. "Uses" = how many times the glyph's name appears across Combination.Recipes.
/// A glyph used in no recipe is unaffected, as is everything when Strength is 0 (the default),
/// in which case generation is bit-for-bit what it was.
///
/// Rough feel, for a 3-value range (e.g. 1..3) and a glyph used twice: Strength 0.5 = 1 extra
/// draw (lowest value ~55% of the time instead of 33%), Strength 1 = 2 extra draws (~70%).
/// Guaranteed motifs have their own length range and are not affected.
/// </summary>
public sealed class UsageSizeBiasConfig
{
    /// <summary>Extra draws added per use. 0 = off. Positive: keep the lowest of the draws (smaller counts and lengths); negative: the same
    /// number of draws (its absolute value) but keep the highest (larger counts and lengths).</summary>
    public double Strength { get; set; } = 0.0;

    /// <summary>Whether the bias is applied to the number of strokes (the shape's count). Default true.</summary>
    public bool AffectSubSymbolCount { get; set; } = true;
    /// <summary>Whether the bias is applied to the chain length. Default true.</summary>
    public bool AffectSubSymbolLength { get; set; } = true;

    public double ExtraDraws(int usageCount) =>
        Strength != 0 && usageCount > 0 ? Strength * usageCount : 0.0;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        if (double.IsNaN(Strength) || double.IsInfinity(Strength))
            errors.Add($"{JsonNames.Of(nameof(Strength))} must be a finite number (got {Strength}); negative values favour larger counts and lengths.");
        return errors;
    }
}
