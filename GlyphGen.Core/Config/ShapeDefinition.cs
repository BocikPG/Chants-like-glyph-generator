namespace GlyphGen.Core.Config;

/// <summary>Whether a shape's geometry is fixed (Literal) or grown by the chain engine (Generative).</summary>
public enum ShapeKind
{
    /// <summary>Fixed relative-coordinate shape (nodes), identical every time except for rotation.</summary>
    Literal,

    /// <summary>Grown by the chain engine from its own chain rules.</summary>
    Generative,
}

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

    /// <summary>Draw this shape only depending on which shapes are already in the glyph (ones listed earlier that were actually drawn:
    /// a shape skipped by its chance, its onlyForGlyphs/excludeGlyphs or its own requires is not "there"). Rolled in addition to <c>chance</c>,
    /// never instead of it: both have to pass. null = no condition.</summary>
    public ShapeRequirement? Requires { get; set; }

    /// <summary>When set, this shape is drawn only in these glyphs (names from glyphs/baseGlyphs); null = every plain
    /// glyph. A copy of this shape is drawn only where this shape is.</summary>
    public List<string>? OnlyForGlyphs { get; set; }

    /// <summary>Glyphs this shape is never drawn in (applied after OnlyForGlyphs). Empty = none.</summary>
    public List<string> ExcludeGlyphs { get; set; } = new();

    internal bool HasGlyphFilter => OnlyForGlyphs is not null || ExcludeGlyphs.Count > 0;

    internal bool AppliesTo(string glyphName) =>
        (OnlyForGlyphs is null || OnlyForGlyphs.Contains(glyphName)) && !ExcludeGlyphs.Contains(glyphName);

    /// <summary>false: the shape is generated and placed like any other - it blocks other shapes, counts for crossing and spacing, and
    /// can be copied, derived and combined - but it is left out of the rendered glyph (and out of its framing). Use it as an invisible
    /// blockade or spacer. Shown only in the extra debug images, in their own colour, when render.debug.showUndrawn is on. Default true.</summary>
    public bool IsDrawn { get; set; } = true;

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

    /// <summary>Id of another generative shape whose whole `chain` block (length, connectionRules, weighting, selfSpacing, loops,
    /// revisits) this shape uses instead of one of its own - the shape keeps its own count, placement, arrangement, glyph filters and
    /// chance, and draws from its own random stream, so it comes out different. The source may itself use chainFrom (no loops). The
    /// source can be anywhere in the list. Leave out `chain` on a shape that uses this; it would be ignored. Generative shapes only.</summary>
    public string? ChainFrom { get; set; }

    /// <summary>Makes this entry a copy of an earlier shape instead of drawing it (see the `copyOf` block). Leave
    /// out kind, count, chain, nodes and placement on such an entry.</summary>
    public CopyConfig? CopyOf { get; set; }

    /// <summary>Rolls the chance on a stream of its own, so a shape with the default 100 draws no random
    /// numbers and every other sequence stays as it was.</summary>
    public bool PassesChance(GlyphGen.Core.Rng.DeterministicRandom glyphRng, double extraDraws = 0.0) =>
        Roll(Chance, glyphRng, $"chance-{Id}", extraDraws);

    /// <summary>Whether the shape's <c>requires</c> block lets it be drawn: the shapes that must (or must not) already be in the glyph are
    /// looked at first, and only when they agree is the block's own chance rolled (a second roll, on a stream of its own, with the same usage
    /// bias as <c>chance</c>). <paramref name="exists"/> says whether a shape id has been drawn in this glyph so far. A shape without the
    /// block always passes and draws no random numbers.</summary>
    public bool PassesRequirement(GlyphGen.Core.Rng.DeterministicRandom glyphRng, Func<string, bool> exists, double extraDraws = 0.0)
    {
        if (Requires is not { } r) return true;
        if (r.Present is { Count: > 0 } present && !(r.MatchPresent == RequireMatch.Any ? present.Any(exists) : present.All(exists))) return false;
        if (r.Absent is { Count: > 0 } absent && !(r.MatchAbsent == RequireMatch.Any ? absent.Any(id => !exists(id)) : !absent.Any(exists))) return false;
        return Roll(r.Chance, glyphRng, $"requires-{Id}", extraDraws);
    }

    private static bool Roll(double chance, GlyphGen.Core.Rng.DeterministicRandom glyphRng, string label, double extraDraws)
    {
        if (chance >= 100.0) return true;
        var stream = glyphRng.Derive(label);
        bool result = stream.NextBool(chance / 100.0);
        if (extraDraws == 0) return result;

        // Usage bias (usageSizeBias.affectChance): roll again |extraDraws| more times, a fraction honoured probabilistically. Negative
        // strength: drawn if any roll succeeds; positive: only if every roll does.
        double amount = Math.Abs(extraDraws);
        int whole = (int)Math.Floor(amount);
        double fraction = amount - whole;
        if (fraction > 0 && stream.NextDouble() < fraction) whole++;
        for (int i = 0; i < whole; i++)
        {
            bool again = stream.NextBool(chance / 100.0);
            result = extraDraws > 0 ? result && again : result || again;
        }

        return result;
    }

    internal string StreamLabel(int instanceIndex) =>
        Count is null
            ? SeedLabel ?? $"shape-{Id}"
            : $"{SeedLabel ?? $"shape-{Id}"}-{instanceIndex}-placement";
}

/// <summary>How a shape's <c>requires</c> block combines its present or absent list.</summary>
public enum RequireMatch
{
    /// <summary>present: every shape must be there. absent: every shape must be missing (default).</summary>
    All,

    /// <summary>present: at least one shape must be there. absent: at least one shape must be missing.</summary>
    Any,
}

/// <summary>shapes[].requires: draw the shape only when other shapes are (or are not) already in the glyph.</summary>
public sealed class ShapeRequirement
{
    /// <summary>Ids of earlier shapes that must already be drawn in this glyph (see match). null/empty = no such condition.</summary>
    public List<string>? Present { get; set; }

    /// <summary>Ids of earlier shapes that must NOT be drawn in this glyph. null/empty = no such condition.</summary>
    public List<string>? Absent { get; set; }

    /// <summary>All (default): every shape in present is needed. Any: one of them is enough.</summary>
    public RequireMatch MatchPresent { get; set; } = RequireMatch.All;

    /// <summary>All (default): every shape in absent must be missing (none of them may be there). Any: it is enough that one of them is
    /// missing (the shape is skipped only when all of them are there).</summary>
    public RequireMatch MatchAbsent { get; set; } = RequireMatch.All;

    /// <summary>Chance (0-100) that the shape is drawn when the conditions hold; rolled per glyph, on top of the shape's own chance.
    /// Default 100. usageSizeBias.affectChance bends it like it bends chance.</summary>
    public double Chance { get; set; } = 100.0;
}
