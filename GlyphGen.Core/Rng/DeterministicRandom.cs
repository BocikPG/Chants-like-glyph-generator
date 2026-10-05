namespace GlyphGen.Core.Rng;

/// <summary>
/// Xoshiro256** — the actual per-node PRNG, used once a deterministic seed has been derived
/// via SplitMix64/SeedTree. Deliberately NOT System.Random: its internal algorithm is not
/// contractually guaranteed to stay fixed across .NET versions/platforms, which would silently
/// break reproducibility of generated glyphs across environments or SDK upgrades. This
/// implementation is small, fixed, and owned outright, so it never changes under us.
/// </summary>
public sealed class DeterministicRandom
{
    private ulong _s0, _s1, _s2, _s3;

    public DeterministicRandom(ulong seed)
    {
        // Expand a single 64-bit seed into four well-mixed, non-zero state words.
        _s0 = SplitMix64.Mix(seed);
        _s1 = SplitMix64.Mix(_s0);
        _s2 = SplitMix64.Mix(_s1);
        _s3 = SplitMix64.Mix(_s2);
    }

    private static ulong Rotl(ulong x, int k) => (x << k) | (x >> (64 - k));

    private ulong NextUInt64()
    {
        ulong result = Rotl(_s1 * 5, 7) * 9;
        ulong t = _s1 << 17;

        _s2 ^= _s0;
        _s3 ^= _s1;
        _s1 ^= _s2;
        _s0 ^= _s3;
        _s2 ^= t;
        _s3 = Rotl(_s3, 45);

        return result;
    }

    /// <summary>Uniform double in [0, 1).</summary>
    public double NextDouble() => (NextUInt64() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform integer in [minInclusive, maxExclusive).</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive) return minInclusive;
        return minInclusive + (int)(NextDouble() * (maxExclusive - minInclusive));
    }

    /// <summary>True with the given probability (0.0-1.0).</summary>
    public bool NextBool(double probability) => NextDouble() < probability;

    /// <summary>
    /// Weighted random index into a list of weights. Weights don't need to sum to 1 or to
    /// any particular total — normalized internally. Used for connectionRules, allowedScales,
    /// rotation.allowed, etc.
    /// </summary>
    public int WeightedIndex(IReadOnlyList<double> weights)
    {
        double total = 0;
        foreach (var w in weights) total += w;

        if (total <= 0) return 0;

        double roll = NextDouble() * total;
        double cumulative = 0;
        for (int i = 0; i < weights.Count; i++)
        {
            cumulative += weights[i];
            if (roll < cumulative) return i;
        }
        return weights.Count - 1;
    }

    /// <summary>
    /// Spawns an independent child generator deterministically derived from this one's current
    /// state plus a stable label. Prefer SeedTree + a fresh DeterministicRandom for anything
    /// that needs to be independently reproducible (glyphs, attempts, sub-symbols); this exists
    /// for finer-grained forks within a single generation step.
    /// </summary>
    public DeterministicRandom Derive(string label) =>
        new(SplitMix64.DeriveSeed(_s0, label));
}
