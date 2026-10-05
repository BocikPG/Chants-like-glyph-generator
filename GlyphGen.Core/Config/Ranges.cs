using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Config;

/// <summary>
/// An inclusive integer range, resolved to one concrete value per generation via the
/// deterministic per-glyph random source (so re-running the same config+seed always
/// resolves to the same value).
/// </summary>
public sealed class IntRange
{
    public int Min { get; set; }
    public int Max { get; set; }

    public int Resolve(DeterministicRandom rng) => rng.NextInt(Min, Max + 1);
}

/// <summary>A percentage hint (0-100), resolved the same way as IntRange. Used as a soft
/// bias (e.g. StraightLineBias), not a hard constraint.</summary>
public sealed class PercentRange
{
    public double Min { get; set; }
    public double Max { get; set; }

    public double Resolve(DeterministicRandom rng) => Min + rng.NextDouble() * (Max - Min);
}

/// <summary>A plain floating-point range (no percent semantics), resolved the same way as
/// IntRange/PercentRange. Used for render settings expressed as canvas fractions, degrees,
/// tension, etc.</summary>
public sealed class FloatRange
{
    public double Min { get; set; }
    public double Max { get; set; }

    public double Resolve(DeterministicRandom rng) => Min + rng.NextDouble() * (Max - Min);
}
