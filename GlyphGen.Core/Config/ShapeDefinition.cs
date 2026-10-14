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

    /// <summary>Chance (0-100) that this entry is drawn in a glyph at all, rolled once per glyph and
    /// covering every instance (a count still applies when it is drawn). Default 100: always drawn. A copy
    /// shape whose original was skipped has nothing to copy and is skipped too.</summary>
    public double Chance { get; set; } = 100.0;

    /// <summary>When set, this shape is drawn only in these glyphs (names from glyphs/baseGlyphs); null = every plain
    /// glyph. A copy of this shape is drawn only where this shape is.</summary>
    public List<string>? OnlyForGlyphs { get; set; }

    /// <summary>Glyphs this shape is never drawn in (applied after OnlyForGlyphs). Empty = none.</summary>
    public List<string> ExcludeGlyphs { get; set; } = new();

    internal bool HasGlyphFilter => OnlyForGlyphs is not null || ExcludeGlyphs.Count > 0;

    internal bool AppliesTo(string glyphName) =>
        (OnlyForGlyphs is null || OnlyForGlyphs.Contains(glyphName)) && !ExcludeGlyphs.Contains(glyphName);

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

    /// <summary>Makes this entry a copy of an earlier shape instead of drawing it (see CopyConfig). Leave
    /// out kind, count, chain, nodes and placement on such an entry.</summary>
    public CopyConfig? CopyOf { get; set; }

    /// <summary>Rolls the chance on a stream of its own, so a shape with the default 100 draws no random
    /// numbers and every other sequence stays as it was.</summary>
    internal bool IsDrawn(GlyphGen.Core.Rng.DeterministicRandom glyphRng) =>
        Chance >= 100.0 || glyphRng.Derive($"chance-{Id}").NextBool(Chance / 100.0);

    internal string StreamLabel(int instanceIndex) =>
        Count is null
            ? SeedLabel ?? $"shape-{Id}"
            : $"{SeedLabel ?? $"shape-{Id}"}-{instanceIndex}-placement";
}
