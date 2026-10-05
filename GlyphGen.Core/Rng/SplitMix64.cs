namespace GlyphGen.Core.Rng;

/// <summary>
/// SplitMix64 — used purely as a seed mixer/expander, not as the main PRNG.
/// Deterministic, public-domain algorithm (Vigna, 2014). These constants must never change:
/// doing so would silently change every derived seed, and therefore every generated glyph,
/// even for runs that previously used the same config and root seed.
/// </summary>
public static class SplitMix64
{
    public static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        ulong z = x;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>
    /// Combines a parent seed with a stable string label (glyph name, language name, motif id...)
    /// into a new deterministic child seed. The label must be something that never changes just
    /// because unrelated entries were added/reordered elsewhere in the config — never a raw
    /// positional index into a list that could grow or shuffle.
    /// </summary>
    public static ulong DeriveSeed(ulong parentSeed, string label)
    {
        ulong hash = parentSeed;
        foreach (char c in label)
        {
            hash = Mix(hash ^ c);
        }
        return hash;
    }

    /// <summary>
    /// Combines a parent seed with a stable integer label (attempt index, sub-symbol index).
    /// Safe here because these indices are always semantically stable within their own scope
    /// (e.g. "attempt #3 of this glyph" always means the same thing regardless of other glyphs).
    /// </summary>
    public static ulong DeriveSeed(ulong parentSeed, int index)
    {
        return Mix(parentSeed ^ (ulong)(uint)index);
    }
}
