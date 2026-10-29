using System.Text.Json.Serialization;
namespace GlyphGen.Core.Config;

/// <summary>
/// Post-generation quality gates applied to every finished glyph in a language, checked
/// against every glyph already accepted earlier in the same run. A glyph failing either check
/// is regenerated (fresh seed) up to retries times before the run fails with a
/// clear error naming the glyph.
/// </summary>
/// <summary>What two glyphs are compared by when judging how alike they are.</summary>
public enum SimilarityBasis
{
    /// <summary>Which nodes they use. Two glyphs that touch the same nodes are alike however the nodes are joined (the original behaviour).</summary>
    Nodes,

    /// <summary>Which strokes they draw: the lines, circles and arcs between nodes. Glyphs on the same few nodes still differ when
    /// they join them differently.</summary>
    Edges,
}

/// <summary>Checks a finished glyph must pass (similarity, straight lines, declarative rules) and how often it is redrawn when it fails.</summary>
public sealed class QualityConfig
{
    /// <summary>How alike two glyphs may be: the limits and what is compared.</summary>
    public SimilarityConfig Similarity { get; set; } = new();

    /// <summary>Ids of the language's shapes with isDrawn false. Filled by the loader (never part of the json): similarity and the
    /// straight-line check look at the visible strokes only.</summary>
    [JsonIgnore] public HashSet<string> UndrawnShapeIds { get; set; } = new();

    /// <summary>Shortcut to quality.similarity.max.</summary>
    [JsonIgnore] public double MaxSimilarity { get => Similarity.Max; set => Similarity.Max = value; }
    /// <summary>Shortcut to quality.similarity.compositeMax.</summary>
    [JsonIgnore] public double CompositeMaxSimilarity { get => Similarity.CompositeMax; set => Similarity.CompositeMax = value; }
    /// <summary>Shortcut to quality.similarity.basis.</summary>
    [JsonIgnore] public SimilarityBasis SimilarityBasis { get => Similarity.Basis; set => Similarity.Basis = value; }
    /// <summary>Shortcut to quality.similarity.comparePosition.</summary>
    [JsonIgnore] public bool ComparePosition { get => Similarity.ComparePosition; set => Similarity.ComparePosition = value; }

    /// <summary>Reject a glyph whose entire geometry (across all its chains/components)
    /// reduces to a single straight line with no bends.</summary>
    public bool RejectStraightLineOnly { get; set; } = true;

    /// <summary>How many times a glyph that fails a quality check is redrawn before the run fails (or, for Score rules, keeps the best attempt). Default 100.</summary>
    public int Retries { get; set; } = 100;

    /// <summary>Declarative checks of every finished glyph (see the fields of a rule). Evaluated after the similarity and straight-line
    /// checks, in order. Default: none.</summary>
    public List<QualityRule> Rules { get; set; } = new();

    /// <summary>Checks BETWEEN glyphs (same fields as `rules`; the selectors name glyphs): the strokes of one glyph are judged against the
    /// strokes of another, e.g. the dots of two base glyphs that never share a composite. Checked when a glyph is accepted, against
    /// every glyph already built. Default: none.</summary>
    public List<QualityRule> PairRules { get; set; } = new();

    /// <summary>Score rules: the penalty at or below which a glyph is accepted at once. A glyph above it is retried (up to
    /// quality.retries) and the lowest-penalty attempt is kept if none gets under it. Default 0.</summary>
    public double AcceptPenalty { get; set; } = 0.0;
}

/// <summary>quality.similarity: how alike two glyphs may be, and what is compared.</summary>
public sealed class SimilarityConfig
{
    /// <summary>Jaccard similarity (0-1) of two glyphs' normalized shapes, at or above which
    /// they're considered too similar and one gets regenerated. 1.0 (or higher) disables the
    /// check entirely. Default 0.7 matches "no more than 70% alike".</summary>
    public double Max { get; set; } = 0.7;

    /// <summary>
    /// Similarity limit for COMPOSITES, checked only against other composites - never against
    /// plain or base glyphs, which a composite contains by construction. 1.0 or more (the
    /// default) = composites are not compared at all, as before. A composite at or above the
    /// limit is rebuilt with a fresh seed, up to quality.retries. Composites are never
    /// subject to RejectStraightLineOnly.
    /// </summary>
    public double CompositeMax { get; set; } = 1.0;

    /// <summary>What <c>max</c> compares: the nodes (default) or the strokes. Edges is the one to use when glyphs are
    /// built on a small fixed set of nodes (a node mask), where nearly every glyph uses the same nodes.</summary>
    public SimilarityBasis Basis { get; set; } = SimilarityBasis.Nodes;

    /// <summary>false (default): two glyphs are compared after shifting each so its bounding box starts at the origin, so the same
    /// shape in another place counts as alike. true: compared where they lie on the grid (for glyphs whose position means something,
    /// like coordinates).</summary>
    public bool ComparePosition { get; set; } = false;
}
