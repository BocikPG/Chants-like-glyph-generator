using GlyphGen.Core.Grid;

namespace GlyphGen.Core.Config;

/// <summary>Which edge of the glyph a component is pulled towards. Top is the smaller y: row 0 is the top
/// of the rendered image.</summary>
public enum GlyphSide { Left, Right, Top, Bottom }

/// <summary>How often a component SetRandom rule draws its side.</summary>
public enum SetRandomScope
{
    /// <summary>Once per language and glyph: the glyph gets the same drawn side in every composite it is part of
    /// (so Man/Human sits in the same place everywhere, and a reader can learn it). The draw is fixed by the
    /// root seed and does not change when a composite is retried.</summary>
    Language,

    /// <summary>Once per composite: each composite draws for itself, so the same glyph can sit on different
    /// sides in different composites (and on a different one after a quality retry).</summary>
    Composite,
}

/// <summary>What SetRandom draws.</summary>
public enum RandomSidePick
{
    /// <summary>One of the four sides.</summary>
    Any,

    /// <summary>Two sides, one horizontal and one vertical: a random corner.</summary>
    Corner,

    /// <summary>Left or Right.</summary>
    Horizontal,

    /// <summary>Top or Bottom.</summary>
    Vertical,
}

/// <summary>How a placement rule controls its component.</summary>
public enum ComponentPlacementMode
{
    /// <summary>Like Prefer, but the sides are drawn at random (see Pick) for each composite instead of listed.
    /// Whatever is drawn then acts exactly like a Prefer rule with those sides.</summary>
    SetRandom,

    /// <summary>Among the placements that pass every hard rule, take the one nearest the chosen side(s).</summary>
    Prefer,

    /// <summary>Exactly one placement: the component as it is in its own glyph, moved by offset. No scaling,
    /// no rotation, no alternatives - if it does not fit, the recipe cannot be built.</summary>
    Fixed,
}

/// <summary>Controls where one component (named by its glyph) goes inside a composite.</summary>
public sealed class ComponentPlacementRule
{
    /// <summary>The component's glyph name. The rule applies wherever that glyph is a component of a recipe the
    /// arrangement covers (every instance, if a recipe lists it more than once).</summary>
    public string Glyph { get; set; } = "";

    public ComponentPlacementMode Mode { get; set; } = ComponentPlacementMode.Prefer;

    /// <summary>Prefer only: one or two sides. Two means a corner, so [Top, Left] is the top left. Left with
    /// Right, or Top with Bottom, cancel out and are rejected.</summary>
    public List<GlyphSide> Sides { get; set; } = new();

    /// <summary>SetRandom only: what is drawn. Default Any.</summary>
    public RandomSidePick? Pick { get; set; }

    /// <summary>SetRandom only: whether the draw is made once for the whole language (default) or again
    /// for every composite. See SetRandomScope.</summary>
    public SetRandomScope? Scope { get; set; }

    /// <summary>Fixed only: how far to move the component from where it sits in its own glyph, as
    /// { "x": 2, "y": 0 } or [2, 0]. Omitted = not moved at all (the plain overlay).</summary>
    public GridPoint? Offset { get; set; }

    /// <summary>Prefer and SetRandom: how strongly the side pull counts against the arrangement's bias, when both are
    /// active (default 1; 0 turns the pull off).</summary>
    public double Weight { get; set; } = 1.0;
}

/// <summary>
/// The arrangement of a combination's components: everything a shape's arrangement has, plus per-component
/// placement rules. Placing order is the recipe's order, and each component is checked against those placed
/// before it, so list the components you fix first.
/// </summary>
public sealed class CombinationArrangementRules : ArrangementRules
{
    /// <summary>Placement rules by component glyph. Components without a rule are placed as before.</summary>
    public List<ComponentPlacementRule> Placements { get; set; } = new();
}
