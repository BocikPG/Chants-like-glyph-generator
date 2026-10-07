namespace GlyphGen.Rendering.Typefaces;

/// <summary>Per-stroke inputs a typeface may need beyond the path itself.</summary>
public sealed class StrokeContext
{
    /// <summary>Multiplier on every width for this glyph (PerGlyphJitter); 1 = none.</summary>
    public double WidthScale { get; init; } = 1.0;

    /// <summary>Deterministic seed unique to this stroke, for noise-based styles.</summary>
    public ulong PathSeed { get; init; }

    /// <summary>Where this stroke records its nodes so joins can be built per node afterwards.
    /// Null = joins are built on the spot (outer side only).</summary>
    public JoinSink? JoinSink { get; init; }

    /// <summary>Identifies this stroke among those sharing the same JoinSink.</summary>
    public int StrokeId { get; init; }
}
