using GlyphGen.Core.Grid;

namespace GlyphGen.Core.Config;

public enum PlacementMode
{
    /// <summary>Exact origin every time. Zero degrees of freedom when paired with a Literal
    /// motif — config validation should clamp MaxPlacementAttempts to 1 for that combination,
    /// since retrying would just re-check an identical result.</summary>
    Fixed,

    /// <summary>Random anchor point, retried up to MaxPlacementAttempts on collision.</summary>
    RandomValid,

    /// <summary>Anchored at (or nearest to) the grid's center; still collision-checked.</summary>
    CenterAnchor,
}

/// <summary>
/// One shared schema for "where does this shape go" — used identically by both
/// guaranteedMotifs[].placement and a language's subSymbolPlacement, so there's a single
/// validator and a single resolver implementation for both cases.
/// </summary>
public sealed class PlacementConfig
{
    public PlacementMode Mode { get; set; } = PlacementMode.RandomValid;

    /// <summary>Required only when Mode == Fixed.</summary>
    public GridPoint? FixedOrigin { get; set; }

    public bool AllowRotationOnPlace { get; set; } = true;

    /// <summary>
    /// Failure budget for "find/fit a shape at a location" — distinct from
    /// CombinationConfig.MaxRetriesPerRecipe, which governs "can this recipe's components
    /// work together at all". Keeping these separate lets failure messages say precisely
    /// which stage gave up.
    /// </summary>
    public int MaxPlacementAttempts { get; set; } = 20;
    public bool StartOnExistingNode { get; set; } = false;
}
