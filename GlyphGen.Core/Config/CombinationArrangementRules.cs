using GlyphGen.Core.Grid;

namespace GlyphGen.Core.Config;

/// <summary>Which edge of the glyph (or its middle) a component is pulled towards. Top is the smaller y: row 0 is the top
/// of the rendered image.</summary>
public enum GlyphSide
{
    /// <summary>The left edge.</summary>
    Left,
    /// <summary>The right edge.</summary>
    Right,
    /// <summary>The top edge (smaller y).</summary>
    Top,
    /// <summary>The bottom edge (larger y).</summary>
    Bottom,
    /// <summary>The middle of the grid. Only a pull (a ranking of the legal placements), never a hard rule, so it puts no
    /// condition on where the other components sit. Cannot be combined with other sides.</summary>
    Center,
}

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

/// <summary>What a Fixed placement does when its one position is not available.</summary>
public enum FixedBlockedAction
{
    /// <summary>Nothing else is tried: the recipe attempt fails, and the component (or another one) is regenerated.</summary>
    Regenerate,

    /// <summary>The component, as drawn, is tried at every other position on the grid first (same size and rotation, in a
    /// shuffled but deterministic order). Only when it fits nowhere does the recipe attempt fail and a component get regenerated.</summary>
    Translate,
}

/// <summary>Controls where one component (named by its glyph, or "*" for all of them) goes inside a composite.</summary>
public sealed class ComponentPlacementRule
{
    /// <summary>The component's glyph name, or a list of names (the rule then applies to each of them). The rule applies wherever that
    /// glyph is a component of a recipe the arrangement covers (every instance, if a recipe lists it more than once).</summary>
    public GlyphNames Glyph { get; set; } = new();

    /// <summary>A rule whose glyph is this applies to every component that has no rule of its own naming it.</summary>
    public const string AllGlyphs = "*";

    /// <summary>What the rule does: Prefer (pull towards the listed sides), SetRandom (draw the sides at random) or Fixed (an exact offset).</summary>
    public ComponentPlacementMode Mode { get; set; } = ComponentPlacementMode.Prefer;

    /// <summary>Prefer only: one or two sides. Two means a corner, so [Top, Left] is the top left. Left with
    /// Right, or Top with Bottom, cancel out and are rejected.</summary>
    public List<GlyphSide> Sides { get; set; } = new();

    /// <summary>SetRandom only: what is drawn. Default Any.</summary>
    public RandomSidePick? Pick { get; set; }

    /// <summary>SetRandom only: whether the draw is made once for the whole language (default) or again
    /// for every composite.</summary>
    public SetRandomScope? Scope { get; set; }

    /// <summary>Fixed only: how far to move the component from where it sits in its own glyph, as
    /// { "x": 2, "y": 0 } or [2, 0]. Omitted = not moved at all (the plain overlay).</summary>
    public GridPoint? Offset { get; set; }

    /// <summary>Fixed only: what to do when the fixed position is blocked (crossing, spacing, off the grid). Default
    /// Regenerate, as before; Translate tries every other position first.</summary>
    public FixedBlockedAction IfBlocked { get; set; } = FixedBlockedAction.Regenerate;

    /// <summary>Prefer and SetRandom: how strongly the side pull counts against the arrangement's bias, when both are
    /// active (default 1; 0 turns the pull off).</summary>
    public double Weight { get; set; } = 1.0;

    /// <summary>Prefer and SetRandom: a node mask the component's nodes must hit (which node, see <c>anchor</c>). Same regions as a shape's
    /// placement.nodes (points, box, pattern) plus <c>nodesOfComponents</c>: nodes of components placed earlier, optionally only those of
    /// some shapes inside them. Only components placed before this one exist, so give the others a Fixed placement or put them earlier in the recipe.</summary>
    public NodeMaskConfig? Nodes { get; set; }

    /// <summary>With <c>nodes</c>: FirstThenAny (default) requires the very first node of the component (first node of its first stroke) on the mask
    /// and, only if no placement is found that way, accepts any node of it; FirstNode never falls back; AnyNode accepts any node from the start.</summary>
    public ComponentNodeAnchor Anchor { get; set; } = ComponentNodeAnchor.FirstThenAny;
}

/// <summary>Which node of a component has to be on the placement's node mask.</summary>
public enum ComponentNodeAnchor
{
    /// <summary>The very first node of the component on the mask; if no placement is found, any node on the mask.</summary>
    FirstThenAny,
    /// <summary>The very first node of the component (first node of its first stroke) on the mask, no fallback.</summary>
    FirstNode,
    /// <summary>At least one node of the component, any, on the mask.</summary>
    AnyNode,
}

/// <summary>
/// The arrangement of a combination's components: everything a shape's arrangement has, plus per-component
/// placement rules. Placing order is the recipe's order, and each component is checked against those placed
/// before it, so list the components you fix first.
/// </summary>
public sealed class CombinationArrangementRules : ArrangementRules
{
    /// <summary>Make the components of a composite touch: at least <c>min</c> shared nodes (junctions) and/or crossings between different
    /// components, with a chance per composite. null (default) = no requirement. If the components do not touch, the last placed one is
    /// moved to a position where they do, then the other movable ones are tried in turn; if no position works the attempt fails and a
    /// component is regenerated, as for any other placement failure. A component with a Fixed placement is not moved; a recipe whose components (after the first) all have Fixed placements is exempt from the requirement.</summary>
    public ContactRequirement? Contact { get; set; }

    /// <summary>Placement rules by component glyph ("*" = every component without a rule of its own). Components without a rule are placed as before.</summary>
    public List<ComponentPlacementRule> Placements { get; set; } = new();

    /// <summary>The rule for a component: the one naming its glyph, else the "*" rule, else none.</summary>
    public ComponentPlacementRule? RuleFor(string glyph) =>
        Placements.FirstOrDefault(r => r.Glyph.Covers(glyph)) ?? Placements.FirstOrDefault(r => r.Glyph.IsAll);

    /// <summary>Whether any rule of the given mode (or any other mode, when negated) applies to a component of this recipe.</summary>
    public bool AnyRuleFor(IEnumerable<string> components, Func<ComponentPlacementRule, bool> test) =>
        components.Distinct().Select(RuleFor).Any(r => r is not null && test(r));
}

/// <summary>What counts as two components touching (see ContactRequirement).</summary>
public enum ContactKind
{
    /// <summary>A node that two components share (their strokes meet at that point).</summary>
    Junction,

    /// <summary>An edge of one component cutting through an edge of another. Needs arrangement.crossing.mode NodesOnly, Anywhere or
    /// AnywhereButRespectStrict: with None no crossing is ever allowed.</summary>
    Crossing,

    /// <summary>Either of the two (default); only what the crossing mode allows can happen.</summary>
    Either,
}

/// <summary>Asks for components that touch each other (arrangement.contact).</summary>
public sealed class ContactRequirement
{
    /// <summary>Chance (0-100) that a composite is held to the requirement; rolled once per composite. Default 100.</summary>
    public double Chance { get; set; } = 100.0;

    /// <summary>How many contacts, counted over the whole composite, are needed. Default 1.</summary>
    public int Min { get; set; } = 1;

    /// <summary>What counts as a contact. Default Either.</summary>
    public ContactKind Kind { get; set; } = ContactKind.Either;
}
