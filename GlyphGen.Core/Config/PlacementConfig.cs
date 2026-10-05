using GlyphGen.Core.Grid;

namespace GlyphGen.Core.Config;

public enum PlacementMode
{
    /// <summary>Exact origin every time (FixedOrigin is required, and must lie on the grid - the
    /// config loader checks both). With nothing else varying (a Literal motif, no rotation) the
    /// resolver makes a single attempt whatever MaxPlacementAttempts says, since retrying would
    /// just re-check an identical result.</summary>
    Fixed,

    /// <summary>Random anchor point, retried up to MaxPlacementAttempts on collision.</summary>
    RandomValid,

    /// <summary>Anchored exactly at (Width / 2, Height / 2) of the grid (integer division, so the
    /// upper-middle cell on even sizes); still collision-checked.</summary>
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

    /// <summary>Required when Mode == Fixed, and an error with any other mode (it would be
    /// ignored). Written as { "x": 7, "y": 9 } or [7, 9].</summary>
    public GridPoint? FixedOrigin { get; set; }

    /// <summary>Allows a random rotation per attempt, from Rotations (below). Only Literal
    /// motifs are affected: a generated chain is already random and ignores the rotation.</summary>
    public bool AllowRotationOnPlace { get; set; } = true;

    /// <summary>
    /// The angles (0/90/180/270) this placement may rotate by - meaningful only for a Literal
    /// motif's placement; the loader rejects it anywhere else. null (default) = inherit
    /// Combination.Rotation when Combination.AllowRotate is on, which is the original coupling;
    /// a list = use exactly these; an empty list = never rotate.
    /// </summary>
    public List<int>? Rotations { get; set; }

    /// <summary>
    /// Failure budget for "find/fit a shape at a location" — distinct from
    /// CombinationConfig.MaxRetriesPerRecipe, which governs "can this recipe's components
    /// work together at all". Keeping these separate lets failure messages say precisely
    /// which stage gave up.
    /// </summary>
    public int MaxPlacementAttempts { get; set; } = 20;

    /// <summary>RandomValid only: start the shape on a node already in the glyph (chosen
    /// uniformly) instead of a random grid point, so shapes grow out of each other. Needs no
    /// crossing permission - edges that merely meet at a node don't count as crossing - but can't
    /// be combined with SubSymbolSpacing.MinNodeDistance &gt; 0 (a shared node is distance 0).</summary>
    public bool StartOnExistingNode { get; set; } = false;
}
