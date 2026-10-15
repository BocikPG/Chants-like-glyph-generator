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

/// <summary>How a SidePull chooses its sides.</summary>
public enum SidePullMode
{
    /// <summary>The listed sides.</summary>
    Prefer,

    /// <summary>Sides drawn at random (see Pick) for each instance of the shape, then used like Prefer.</summary>
    SetRandom,
}

/// <summary>
/// Pulls a shape towards a side or corner of the glyph. Among the shape's legal placements (the best-of-N
/// candidates of its arrangement) the one nearest the chosen side(s) is kept. A ranking only: it never relaxes
/// a hard rule, and the pull is as strong as arrangement.candidates is high.
/// </summary>
public sealed class SidePull
{
    public SidePullMode Mode { get; set; } = SidePullMode.Prefer;

    /// <summary>Prefer only: one or two sides; two means a corner. Left with Right, or Top with Bottom, cancel out.</summary>
    public List<GlyphSide> Sides { get; set; } = new();

    /// <summary>SetRandom only: what is drawn. Default Any.</summary>
    public RandomSidePick? Pick { get; set; }

    /// <summary>How strongly the pull counts against the arrangement's bias, when both are active (default 1;
    /// 0 turns it off).</summary>
    public double Weight { get; set; } = 1.0;
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

    /// <summary>A box smaller than the grid that this shape must stay inside, treated as the edge of the grid while the
    /// shape is generated and placed (nodes, and circles' whole outlines). Inclusive node coordinates, e.g.
    /// { "minX": 4, "minY": 4, "maxX": 10, "maxY": 10 }. Other shapes are not limited by it. null = the whole grid.</summary>
    public PlacementBounds? Bounds { get; set; }

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

    /// <summary>When an instance of this shape cannot be placed within maxPlacementAttempts: how many times to redraw
    /// ALL instances of this shape (new chains, new positions) before giving up on this try of the glyph. Earlier
    /// shapes stay as they are. Default 0 = no redraw (the glyph fails, as before).</summary>
    public int RedoShape { get; set; } = 0;

    /// <summary>After redoShape is spent: how many times to redraw the WHOLE glyph from a fresh seed (every shape
    /// again) before the failure is reported. Default 0.</summary>
    public int RedrawGlyph { get; set; } = 0;

    /// <summary>RandomValid only: start the shape on a node already in the glyph (chosen
    /// uniformly) instead of a random grid point, so shapes grow out of each other. Needs no
    /// crossing permission - edges that merely meet at a node don't count as crossing - but can't
    /// be combined with SubSymbolSpacing.MinNodeDistance &gt; 0 (a shared node is distance 0).</summary>
    public bool StartOnExistingNode { get; set; } = false;

    /// <summary>With startOnExistingNode: the chance (0-100) that an attempt starts on an existing node; otherwise it
    /// starts on a random grid point as usual. Rolled per attempt. Default 100 = always start on a node.</summary>
    public double StartOnExistingNodeChance { get; set; } = 100.0;

    /// <summary>With startOnExistingNode: only the nodes of these shapes (ids of EARLIER shapes) count as places to
    /// start on. null (default) = every node already in the glyph. When none of the listed shapes was drawn in this
    /// glyph, there is nothing to attach to and the shape is placed like the first one, at a random point.</summary>
    public List<string>? StartOnShapes { get; set; }

    /// <summary>RandomValid only: pull the shape towards a side or corner of the glyph. null = no pull. It ranks
    /// the shape's best-of-N candidates (arrangement.candidates), so raise that for a stronger pull.</summary>
    public SidePull? Side { get; set; }
}

/// <summary>An inclusive box of lattice node coordinates (placement.bounds).</summary>
public sealed class PlacementBounds
{
    public int MinX { get; set; }
    public int MinY { get; set; }
    public int MaxX { get; set; }
    public int MaxY { get; set; }
}
