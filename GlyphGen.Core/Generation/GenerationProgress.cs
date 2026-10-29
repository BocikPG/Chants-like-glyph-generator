namespace GlyphGen.Core.Generation;

/// <summary>What a generation step reports to the host (the CLI shows it as progress).</summary>
public enum GenerationStage
{
    /// <summary>Generation of a language began; <c>Total</c> glyphs will be drawn.</summary>
    Started,

    /// <summary>A glyph is being drawn now.</summary>
    GlyphStarted,

    /// <summary>A glyph is finished (<c>Done</c> counts it).</summary>
    GlyphDone,

    /// <summary>A glyph needed another attempt; <c>Detail</c> says which attempt and why the last one was rejected.</summary>
    Retry,

    /// <summary>A partner glyph is being drawn again for a glyph that could not be drawn against it.</summary>
    PartnerRedraw,

    /// <summary>The language is finished.</summary>
    Finished,
}

/// <summary>One progress report. May arrive from any thread.</summary>
/// <param name="Language">The language being generated.</param>
/// <param name="Stage">What happened.</param>
/// <param name="Glyph">The glyph it is about (null for Started / Finished).</param>
/// <param name="Kind">"base glyph", "glyph" or "composite" (null when not about a glyph).</param>
/// <param name="Done">Glyphs finished so far.</param>
/// <param name="Total">Glyphs to draw in this language.</param>
/// <param name="Detail">Extra text for Retry / PartnerRedraw.</param>
public sealed record GenerationProgress(string Language, GenerationStage Stage, string? Glyph, string? Kind, int Done, int Total, string? Detail = null);
