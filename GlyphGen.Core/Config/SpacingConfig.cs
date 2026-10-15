namespace GlyphGen.Core.Config;

/// <summary>How MaxNodeDistance is applied against the shapes already placed in the glyph.</summary>
public enum ProximityMode
{
    /// <summary>Candidate must be within MaxNodeDistance of AT LEAST ONE existing shape.</summary>
    NearestShape,
    /// <summary>Candidate must be within MaxNodeDistance of EVERY existing shape.</summary>
    AllShapes,
}

/// <summary>Which of a chain's existing nodes a newly added node is measured against.</summary>
public enum SelfSpacingMode
{
    /// <summary>MIN: at least the distance from every earlier node EXCEPT the one it connects
    /// from (that gap is set by the connection rules) and except an exact revisit of an
    /// existing node. MAX: within the distance of every earlier node, connecting-from node
    /// included.</summary>
    Existing,

    /// <summary>Only the last Lookback nodes of the chain (1 = just the node it connects from,
    /// 2 = also the one before that, ...). Constrains the local shape of the stroke - e.g. a
    /// minimum here forces steps/turns to be at least that long. An exact revisit of one of
    /// those nodes is exempt from the minimum.</summary>
    Adjacent,

    /// <summary>EVERY earlier node, no exemptions at all. For a MIN this also means the
    /// connecting step must be at least that long and an exact node revisit is impossible
    /// (distance 0). For a MAX it behaves the same as Existing.</summary>
    AllNodes,
}

/// <summary>One node-to-node spacing rule applied while a single chain grows. Several rules
/// (even of the same Mode) can be active at once; a new node must satisfy all of them.
/// Distances are in grid units, 0 = that bound is off.</summary>
public sealed class SelfSpacingRule
{
    public SelfSpacingMode Mode { get; set; } = SelfSpacingMode.Existing;
    public double MinDistance { get; set; } = 0.0;
    public double MaxDistance { get; set; } = 0.0;

    /// <summary>Adjacent mode only: how many of the chain's most recent nodes are checked.</summary>
    public int Lookback { get; set; } = 1;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        if (!Enum.IsDefined(Mode))
            errors.Add($"{JsonNames.Of(nameof(Mode))} has an unknown value ({(int)Mode}).");

        if (double.IsNaN(MinDistance) || double.IsInfinity(MinDistance) || MinDistance < 0)
            errors.Add($"{JsonNames.Of(nameof(MinDistance))} must be a finite number >= 0 (got {MinDistance}).");
        if (double.IsNaN(MaxDistance) || double.IsInfinity(MaxDistance) || MaxDistance < 0)
            errors.Add($"{JsonNames.Of(nameof(MaxDistance))} must be a finite number >= 0 (got {MaxDistance}).");

        if (MinDistance > 0 && MaxDistance > 0 && MaxDistance < MinDistance)
        {
            errors.Add(
                $"{JsonNames.Of(nameof(MaxDistance))} ({MaxDistance}) is smaller than " +
                $"{JsonNames.Of(nameof(MinDistance))} ({MinDistance}) - no node could ever satisfy both.");
        }

        if (Mode == SelfSpacingMode.Adjacent && Lookback < 1)
            errors.Add($"{JsonNames.Of(nameof(Lookback))} must be >= 1 (got {Lookback}).");

        return errors;
    }
}

/// <summary>
/// Spacing enforced between a shape and the shapes already placed, on top of (not instead of)
/// the ordinary intersection check. Part of a shape's (or a combination's) arrangement rules;
/// the spacing within one chain is ChainRules.SelfSpacing. Minimums keep things from sitting uncomfortably close
/// together; maximums keep them clustered. Disabled by default, since these are additional
/// constraints that can make placement noticeably harder to satisfy.
///
/// A "shape" is one placed instance of a shapes entry when generating a glyph, and a whole placed
/// component when combining glyphs per Combination.Recipes - both go through
/// SubSymbolSpacingChecker. The rules of the shape BEING PLACED are checked against EVERY
/// already-placed shape (minimums against all; the maximum per MaxDistanceMode), so the result
/// depends on placement order.
/// </summary>
public sealed class SpacingConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>Minimum distance between the CENTROIDS of two shapes (the average of each
    /// shape's own node positions) - a coarse "keep this shape in its own area of the grid"
    /// check, independent of how close any individual pair of nodes gets.</summary>
    public double MinCenterDistance { get; set; } = 0.0;

    /// <summary>Minimum distance between the CLOSEST pair of nodes belonging to two different
    /// shapes - a finer, local check that catches two shapes whose overall centers are far
    /// apart but which still reach out and nearly touch at one point.</summary>
    public double MinNodeDistance { get; set; } = 0.0;

    /// <summary>Maximum distance between the CLOSEST pair of nodes of the candidate and an
    /// existing shape. 0 = no maximum. Which existing shapes it must hold against is controlled
    /// by MaxDistanceMode. Must be >= MinNodeDistance when both are set.</summary>
    public double MaxNodeDistance { get; set; } = 0.0;

    public ProximityMode MaxDistanceMode { get; set; } = ProximityMode.NearestShape;

    /// <summary>For a shape with placement.startOnExistingNode: leave the node it starts on out of the spacing checks
    /// (centroid, minimum and maximum node distance), since that node is shared with an existing shape and so sits at distance 0
    /// from it. Every other node of the shape is still measured. Default false.</summary>
    public bool IgnoreStartNode { get; set; } = false;

    /// <summary>Returns a human-readable message per problem found (empty = valid). Checked
    /// even when Enabled is false, so a typo isn't silently waiting for the day it's switched on.</summary>
    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();

        CheckDistance(JsonNames.Of(nameof(MinCenterDistance)), MinCenterDistance);
        CheckDistance(JsonNames.Of(nameof(MinNodeDistance)), MinNodeDistance);
        CheckDistance(JsonNames.Of(nameof(MaxNodeDistance)), MaxNodeDistance);

        if (!Enum.IsDefined(MaxDistanceMode))
            errors.Add($"{JsonNames.Of(nameof(MaxDistanceMode))} has an unknown value ({(int)MaxDistanceMode}).");

        if (MaxNodeDistance > 0 && MinNodeDistance > 0 && MaxNodeDistance < MinNodeDistance)
        {
            errors.Add(
                $"{JsonNames.Of(nameof(MaxNodeDistance))} ({MaxNodeDistance}) is smaller than " +
                $"{JsonNames.Of(nameof(MinNodeDistance))} ({MinNodeDistance}) - no placement could ever satisfy both.");
        }

        return errors;

        void CheckDistance(string name, double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value < 0)
                errors.Add($"{name} must be a finite number >= 0 (got {value}).");
        }
    }
}
