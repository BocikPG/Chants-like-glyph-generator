namespace GlyphGen.Rendering.Typefaces;

/// <summary>Per-stroke inputs a typeface may need beyond the path itself.</summary>
public sealed class StrokeContext
{
    /// <summary>Multiplier on every width for this glyph (PerGlyphJitter); 1 = none.</summary>
    public double WidthScale { get; init; } = 1.0;

    /// <summary>Deterministic seed unique to this stroke, for noise-based styles.</summary>
    public ulong PathSeed { get; init; }
}
