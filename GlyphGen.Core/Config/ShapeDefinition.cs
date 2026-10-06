namespace GlyphGen.Core.Config;

/// <summary>
/// One entry in a language's shapes list: a kind of stroke every plain glyph is built from. The
/// list is processed in order, each entry placed against everything before it. Omit count for a
/// guaranteed shape (exactly one, always present); give a count range for free-form strokes
/// (rolled per glyph, and the only kind affected by usageSizeBias).
/// </summary>
public sealed class ShapeDefinition
{
    /// <summary>Unique within the language. Names the shape in error messages and seeds its
    /// random streams, so renaming it changes the glyphs.</summary>
    public string Id { get; set; } = "";

    public ShapeKind Kind { get; set; } = ShapeKind.Generative;

    /// <summary>How many instances per glyph, rolled once per glyph. Omit for exactly one
    /// guaranteed instance (no roll).</summary>
    public IntRange? Count { get; set; }

    /// <summary>Chain rules - Generative shapes only.</summary>
    public ChainRules Chain { get; set; } = new();

    /// <summary>[x, y] points relative to the placement origin - Literal shapes only.</summary>
    public List<int[]>? Nodes { get; set; }

    /// <summary>Where the shape goes and how it is rotated.</summary>
    public PlacementConfig Placement { get; set; } = new();

    /// <summary>How the shape relates to the shapes already placed.</summary>
    public ArrangementRules Arrangement { get; set; } = new();

    /// <summary>Advanced: prefix of this shape's random streams, which defaults to
    /// "shape-" plus the id. Only needed to make a migrated config reproduce its old glyphs
    /// exactly (the migration tool sets it); leave it out otherwise.</summary>
    public string? SeedLabel { get; set; }

    internal string StreamLabel(int instanceIndex) =>
        Count is null
            ? SeedLabel ?? $"shape-{Id}"
            : $"{SeedLabel ?? $"shape-{Id}"}-{instanceIndex}-placement";
}
