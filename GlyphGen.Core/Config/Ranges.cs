using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Config;

/// <summary>
/// An inclusive integer range, resolved to one concrete value per generation via the
/// deterministic per-glyph random source (so re-running the same config+seed always
/// resolves to the same value).
/// </summary>
public sealed class IntRange
{
    /// <summary>Lowest value (inclusive).</summary>
    public int Min { get; set; }
    /// <summary>Highest value (inclusive). Must not be below min.</summary>
    public int Max { get; set; }

    public int Resolve(DeterministicRandom rng) => rng.NextInt(Min, Max + 1);
}

/// <summary>A percentage hint (0-100), resolved the same way as an integer range. Used as a soft
/// bias (e.g. a chance), not a hard constraint.</summary>
public sealed class PercentRange
{
    /// <summary>Lowest percentage (0-100).</summary>
    public double Min { get; set; }
    /// <summary>Highest percentage (0-100). Must not be below min.</summary>
    public double Max { get; set; }

    public double Resolve(DeterministicRandom rng) => Min + rng.NextDouble() * (Max - Min);
}

/// <summary>A plain floating-point range (no percent semantics), resolved the same way as
/// the integer and percent ranges. Used for render settings expressed as canvas fractions, degrees,
/// tension, etc.</summary>
public sealed class FloatRange
{
    /// <summary>Lowest value (inclusive).</summary>
    public double Min { get; set; }
    /// <summary>Highest value (inclusive). Must not be below min.</summary>
    public double Max { get; set; }

    public double Resolve(DeterministicRandom rng) => Min + rng.NextDouble() * (Max - Min);
}
