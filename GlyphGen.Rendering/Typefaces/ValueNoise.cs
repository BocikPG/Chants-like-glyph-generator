using GlyphGen.Core.Rng;

namespace GlyphGen.Rendering.Typefaces;

/// <summary>Seeded 1D value noise in [0,1], smooth between integer lattice points.</summary>
public sealed class ValueNoise
{
    private readonly ulong _seed;

    public ValueNoise(ulong seed) => _seed = seed;

    public double Sample(double x)
    {
        long i = (long)Math.Floor(x);
        double f = x - i;
        double a = Lattice(i), b = Lattice(i + 1);
        double t = f * f * (3 - 2 * f);
        return a + (b - a) * t;
    }

    private double Lattice(long i) => (SplitMix64.Mix(_seed ^ (ulong)i) >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uncorrelated per-index value in [0,1] (no smoothing).</summary>
    public double Hash(long i) => Lattice(i * 7919 + 13);
}
