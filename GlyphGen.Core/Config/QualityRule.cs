namespace GlyphGen.Core.Config;

/// <summary>What happens when a quality rule is broken by a finished glyph.</summary>
public enum QualityAction
{
    /// <summary>The glyph is rejected and regenerated (fresh seed), like a too-similar glyph.</summary>
    Reject,

    /// <summary>The glyph is kept; the problem is reported as a warning at the end of the run.</summary>
    Warn,

    /// <summary>A soft score: every broken check adds <c>weight</c> to the glyph's penalty. A glyph with a penalty above
    /// <c>quality.acceptPenalty</c> is retried, but if no attempt gets under it the best one is kept instead of failing.</summary>
    Score,
}

/// <summary>Which strokes a selector matches by form.</summary>
public enum StrokeKind
{
    Any,
    /// <summary>A circle of diameter 0 only.</summary>
    Dot,
    /// <summary>Part of a circle.</summary>
    Arc,
    /// <summary>Any whole circle, a dot (a circle of diameter 0, as the Circle connection rule draws it) included.</summary>
    Circle,
    /// <summary>A whole circle with a diameter above 0 (no dots).</summary>
    Ring,
    /// <summary>A straight line.</summary>
    Line,
    /// <summary>A line with a bend.</summary>
    Curve,
}

public enum QualityMeasure
{
    /// <summary>How many of the 'against' strokes lie in the window (at most <c>max</c>, default 0).</summary>
    Count,

    /// <summary>How many of the 'against' strokes in the window LOOK LIKE the subject (similarity at or above
    /// <c>similarityThreshold</c>); at most <c>max</c>, default 0.</summary>
    Similar,

    /// <summary>The nearest 'against' stroke is at least <c>minDistance</c> away.</summary>
    Distance,
}

/// <summary>Which strokes count as "something else" for a subject.</summary>
public enum QualityAmong
{
    /// <summary>Composite glyphs: strokes of the other components. Plain glyphs: strokes of other chains.</summary>
    Auto,

    /// <summary>Composites only: strokes that belong to another component. A plain glyph is not checked by the rule.</summary>
    OtherComponents,

    /// <summary>Any stroke that is not part of the subject's own chain.</summary>
    OtherChains,
}

/// <summary>What is regenerated when a composite breaks a rule (the composite is always arranged again; this adds a redraw of a
/// component). Only the plain/base glyphs a composite is built from can be redrawn, and every other composite built from the
/// redrawn one is rebuilt with it (the change is undone if one of them no longer fits or breaks a Reject rule).</summary>
public enum QualityRedo
{
    /// <summary>Only rearrange the composite (what a quality retry always does).</summary>
    Composite,

    /// <summary>Also redraw the component that holds the stroke that was judged (the subject).</summary>
    Subject,

    /// <summary>Also redraw the component that holds the stroke it clashed with.</summary>
    Other,

    /// <summary>Redraw the subject's or the other component, alternating from one attempt to the next.</summary>
    Either,
}

public enum QualityAppliesTo { Both, Plain, Composite }

public enum SimilarityMode
{
    /// <summary>What the strokes look like: both are drawn with a brush of <c>strokeWidth</c> cells, centred on each other,
    /// and the overlap (intersection over union) is the similarity.</summary>
    Visual,

    /// <summary>Same kind of stroke and about the same size (lines and curves: length within 1 cell; arcs: sweep within 45
    /// degrees and diameter within 1 cell). 1 or 0.</summary>
    Structural,
}

public enum WindowShape
{
    /// <summary>The subject's bounding box grown by <c>radius</c> on every side (a square around a dot).</summary>
    Square,

    /// <summary>Everything within <c>radius</c> of the subject's path.</summary>
    Circle,
}

public enum CountDiffersAction
{
    /// <summary>Strokes of components whose count of the shape differs are not compared at all.</summary>
    Skip,

    /// <summary>The window radius (and minDistance) is multiplied by <c>factor</c> for such a pair.</summary>
    Loosen,
}

/// <summary>Picks strokes by the shape that drew them and by their form.</summary>
public sealed class StrokeSelector
{
    /// <summary>Ids of the shapes that drew the strokes. Empty/missing = any shape.</summary>
    public List<string>? Shapes { get; set; }

    public StrokeKind Kind { get; set; } = StrokeKind.Any;

    /// <summary>quality.pairRules only: the glyphs (by name, "*" = any glyph) whose strokes this selector picks from.</summary>
    public List<string>? Glyphs { get; set; }
}

public sealed class QualityWindow
{
    public WindowShape Shape { get; set; } = WindowShape.Square;

    /// <summary>Lattice units. Default 4.</summary>
    public double Radius { get; set; } = 4;
}

/// <summary>Skip or loosen a rule for a pair of strokes whose components rolled a different number of instances of a shape
/// (two bases with 1 and 3 dots are told apart by the count already).</summary>
public sealed class CountDiffersConfig
{
    /// <summary>Id of the shape whose instances are counted in each component.</summary>
    public string Shape { get; set; } = "";

    public CountDiffersAction Action { get; set; } = CountDiffersAction.Skip;

    /// <summary>Loosen: multiplier (0-1) for the window radius and minDistance of that pair. Default 0.5.</summary>
    public double Factor { get; set; } = 0.5;
}

/// <summary>One declarative check of a finished glyph (quality.rules): for every stroke picked by <c>subject</c>, look at what
/// lies around it among the strokes picked by <c>against</c>, and judge it with <c>measure</c>.</summary>
public sealed class QualityRule
{
    public string Id { get; set; } = "";

    public QualityAppliesTo AppliesTo { get; set; } = QualityAppliesTo.Both;

    /// <summary>Only these glyphs (by name). Empty/missing = every glyph.</summary>
    public List<string>? OnlyForGlyphs { get; set; }

    /// <summary>Not these glyphs (by name).</summary>
    public List<string>? ExcludeGlyphs { get; set; }

    public StrokeSelector Subject { get; set; } = new();
    public StrokeSelector Against { get; set; } = new();
    public QualityAmong Among { get; set; } = QualityAmong.Auto;

    public QualityMeasure Measure { get; set; } = QualityMeasure.Count;

    /// <summary>Count and Similar: the area around the subject that is searched.</summary>
    public QualityWindow Window { get; set; } = new();

    /// <summary>Count and Similar: how many strokes may be found before the check fails. Default 0.</summary>
    public int Max { get; set; } = 0;

    /// <summary>Distance: the nearest 'against' stroke must be at least this far (lattice units).</summary>
    public double MinDistance { get; set; } = 0;

    /// <summary>Similar: how the strokes are compared. Default Visual.</summary>
    public SimilarityMode Similarity { get; set; } = SimilarityMode.Visual;

    /// <summary>Similar: similarity (0-1) at or above which two strokes count as alike. Default 0.7.</summary>
    public double SimilarityThreshold { get; set; } = 0.7;

    /// <summary>Visual similarity: brush width in lattice units. Default 1.5.</summary>
    public double StrokeWidth { get; set; } = 1.5;

    public QualityAction OnFail { get; set; } = QualityAction.Reject;

    /// <summary>Composites, onFail Reject: what is redrawn when the rule fails. Default Composite.</summary>
    public QualityRedo Redo { get; set; } = QualityRedo.Composite;

    /// <summary>Score: the penalty one broken check adds. Default 1.</summary>
    public double Weight { get; set; } = 1;

    /// <summary>Composites: skip or loosen the rule for components that rolled a different count of a shape.</summary>
    public CountDiffersConfig? WhenCountsDiffer { get; set; }
}
