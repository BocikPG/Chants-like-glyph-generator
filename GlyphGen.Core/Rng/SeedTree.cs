namespace GlyphGen.Core.Rng;

/// <summary>
/// Convenience wrapper expressing the seed hierarchy from design:
/// Root -> Language -> Glyph -> Attempt -> SubSymbol (and Motif, off the Glyph level).
/// Every derivation is a pure function of (parent seed, stable label). This is what makes
/// regenerating a single glyph in isolation produce byte-identical output to generating
/// the whole batch, and makes retry attempt #N always resolve to the same sub-seed
/// regardless of how earlier attempts behaved.
/// </summary>
public static class SeedTree
{
    public static ulong ForLanguage(ulong rootSeed, string languageName) =>
        SplitMix64.DeriveSeed(rootSeed, languageName);

    public static ulong ForGlyph(ulong languageSeed, string glyphName) =>
        SplitMix64.DeriveSeed(languageSeed, glyphName);

    public static ulong ForAttempt(ulong glyphSeed, int attemptIndex) =>
        SplitMix64.DeriveSeed(glyphSeed, attemptIndex);

    public static ulong ForSubSymbol(ulong attemptSeed, int subSymbolIndex) =>
        SplitMix64.DeriveSeed(attemptSeed, subSymbolIndex);

    public static ulong ForMotif(ulong glyphSeed, string motifId) =>
        SplitMix64.DeriveSeed(glyphSeed, motifId);
}
