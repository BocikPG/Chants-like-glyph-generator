using System.Text.Json.Serialization;
using GlyphGen.Core.Grid;

namespace GlyphGen.Core.Config;

public enum PlacementMode
{
    /// <summary>Exact origin every time (origin is required, and must lie on the grid - the
    /// config loader checks both). With nothing else varying (a Literal motif, no rotation) the
    /// resolver makes a single attempt whatever retries.placement says, since retrying would
    /// just re-check an identical result.</summary>
    Fixed,

    /// <summary>Random anchor point, retried up to retries.placement on collision.</summary>
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
    /// <summary>How the sides are chosen: Prefer (the listed ones) or SetRandom (drawn per instance).</summary>
    public SidePullMode Mode { get; set; } = SidePullMode.Prefer;

    /// <summary>Prefer only: one or two sides; two means a corner. Left with Right, or Top with Bottom, cancel out.</summary>
    public List<GlyphSide> Sides { get; set; } = new();

    /// <summary>SetRandom only: what is drawn. Default Any.</summary>
    public RandomSidePick? Pick { get; set; }

    /// <summary>How strongly the pull counts against the arrangement's bias, when both are active (default 1;
    /// 0 turns it off).</summary>
    public double Weight { get; set; } = 1.0;
}

/// <summary>placement.start: where the shape starts.</summary>
public sealed class PlacementStart
{
    /// <summary>Where the shape starts: RandomValid (default), Fixed (an exact origin) or CenterAnchor (the middle of the grid).</summary>
    public PlacementMode Mode { get; set; } = PlacementMode.RandomValid;

    /// <summary>Required when mode is Fixed, and an error with any other mode (it would be
    /// ignored). Written as { "x": 7, "y": 9 } or [7, 9] - or as an array of such points, in which case one is picked at random for
    /// each glyph (a failed try moves on to the next of a shuffled order, so every origin is tried before giving up).</summary>
    public OriginChoice? Origin { get; set; }

    /// <summary>Fixed with several origin points: an origin an earlier instance of this shape (same glyph) was placed at is
    /// not used again, so a shape with count N and N origins fills each of them once. When every origin is used the instance cannot
    /// be placed. Default false.</summary>
    public bool DiscardUsed { get; set; } = false;

    /// <summary>RandomValid only. Present = start the shape on a node already in the glyph (chosen uniformly) instead of a random grid
    /// point, so shapes grow out of each other. Needs no crossing permission - edges that merely meet at a node don't count as crossing -
    /// but can't be combined with `arrangement.spacing.minNodeDistance` above 0 unless <c>ignoreStartNode</c> is set (a shared node is
    /// distance 0). Left out = never.</summary>
    public ExistingNodeStart? OnExistingNode { get; set; }

    /// <summary>RandomValid only. Present = start the shape on a node of a partner glyph (see combination.partners) instead of a random grid
    /// point, so the glyph grows out of the glyph it is drawn against. Not together with <c>onExistingNode</c>. Left out = never.</summary>
    public PartnerNodeStart? OnPartnerNode { get; set; }

    internal PlacementStart Clone() => (PlacementStart)MemberwiseClone();
}

/// <summary>placement.start.onExistingNode: starting a shape on a node that is already there.</summary>
public sealed class ExistingNodeStart
{
    /// <summary>The chance (0-100) that an attempt starts on an existing node; otherwise it starts on a random grid point as usual.
    /// Rolled per attempt. Default 100 = always start on a node.</summary>
    public double Chance { get; set; } = 100.0;

    /// <summary>Only the nodes of these shapes (ids of EARLIER shapes) count as places to start on. null (default) = every node already
    /// in the glyph. When none of the listed shapes was drawn in this glyph, there is nothing to attach to and the shape is placed like
    /// the first one, at a random point.</summary>
    public List<string>? Shapes { get; set; }

    /// <summary>Leave the node the shape starts on out of the spacing checks (centroid, minimum and maximum node distance), since that node
    /// is shared with an existing shape and so sits at distance 0 from it. Every other node of the shape is still measured. Default false.</summary>
    public bool IgnoreStartNode { get; set; } = false;
}

/// <summary>What a shape does when the partner glyph has no node to start on.</summary>
public enum PartnerFallback
{
    /// <summary>Start on a random grid point instead (default).</summary>
    Anywhere,

    /// <summary>Do not draw the shape in this glyph.</summary>
    Skip,

    /// <summary>The glyph cannot be drawn: the run stops with an error naming the shape and the partner.</summary>
    Fail,
}

/// <summary>placement.start.onPartnerNode: starting a shape on a node of a partner glyph (combination.partners).</summary>
public sealed class PartnerNodeStart
{
    /// <summary>The partner whose nodes count (it has to be one of the partners of the glyph being drawn). Left out: with one partner, its
    /// nodes; with several, each start goes to the first partner (in the order of <c>combination.partners</c>) that the glyph has not touched
    /// yet, so the starts reach every partner one after another. Once all are touched, any partner's nodes count.</summary>
    public string? Partner { get; set; }

    /// <summary>Only the nodes of these shapes (ids) of the partner count. Left out = every node of the partner. Applies to every partner
    /// unless <c>shapesByPartner</c> names it.</summary>
    public List<string>? Shapes { get; set; }

    /// <summary>Per partner: only the nodes of these shapes (ids) count, for the partners named here (they replace <c>shapes</c> for that
    /// partner). A partner not named uses <c>shapes</c>.</summary>
    public Dictionary<string, List<string>>? ShapesByPartner { get; set; }

    /// <summary>The chance (0-100) that an attempt starts on a partner node; otherwise that attempt starts on a random grid point. Rolled
    /// per attempt. Default 100.</summary>
    public double Chance { get; set; } = 100.0;

    /// <summary>What happens when there is no such node (the partner has none of those shapes, or it is not a partner of this glyph).
    /// Default Anywhere.</summary>
    public PartnerFallback IfNone { get; set; } = PartnerFallback.Anywhere;
}

/// <summary>placement.area: where the shape may be.</summary>
public sealed class PlacementArea
{
    /// <summary>A box smaller than the grid that this shape must stay inside, treated as the edge of the grid while the
    /// shape is generated and placed (nodes, and circles' whole outlines). Inclusive node coordinates, e.g.
    /// { "minX": 4, "minY": 4, "maxX": 10, "maxY": 10 }. Other shapes are not limited by it. null = the whole grid.</summary>
    public PlacementBounds? Bounds { get; set; }

    /// <summary>Which nodes this shape may use, beyond <c>bounds</c>: direct nodes, boxes, odd/even/every-n patterns, the
    /// nodes of other shapes, the nodes of another glyph, or the box around other shapes. Every node of the shape's strokes (circle nodes
    /// included) must pass, and so must the node it starts on. Not applied to copyOf shapes. null = no restriction.</summary>
    public NodeMaskConfig? Nodes { get; set; }
}

/// <summary>placement.rotation: turning a Literal shape when it is placed.</summary>
public sealed class PlacementRotation
{
    /// <summary>Allows a random rotation per attempt, from <c>angles</c> (below). Only Literal shapes are affected: a generated chain is
    /// already random and ignores the rotation. Default true.</summary>
    public bool Allowed { get; set; } = true;

    /// <summary>
    /// The angles (0/90/180/270) this placement may rotate by - meaningful only for a Literal shape's placement; the loader rejects it
    /// anywhere else. null (default) = inherit combination.rotation when combination.allowRotate is on, which is the original coupling;
    /// a list = use exactly these; an empty list = never rotate.
    /// </summary>
    public List<int>? Angles { get; set; }
}

/// <summary>placement.retries: what is tried, in this order, when a shape cannot be placed.</summary>
public sealed class PlacementRetries
{
    /// <summary>Failure budget for "find/fit a shape at a location" - how many positions are tried for one instance. Distinct from
    /// `combination.retries`, which governs "can this recipe's components work together at all". Default 100.</summary>
    public int Placement { get; set; } = 100;

    /// <summary>When an instance of this shape cannot be placed within <c>placement</c>: how many times to redraw ALL instances of this
    /// shape (new chains, new positions) before giving up on this try of the glyph. Earlier shapes stay as they are. Default 100.</summary>
    public int Shape { get; set; } = 100;

    /// <summary>After <c>shape</c> is spent: how many times to redraw the WHOLE glyph from a fresh seed (every shape again) before the
    /// failure is reported. Default 100.</summary>
    public int Glyph { get; set; } = 100;

    /// <summary>What happens when <c>shape</c> is spent and the shape still cannot be placed. <c>Fail</c> (default): the whole glyph is
    /// redrawn (<c>glyph</c> times), then generation fails. <c>Skip</c>: the shape is left out of this glyph (all its instances, and the
    /// copies of it) and the rest is drawn without it. To skip right after the positions are tried, set <c>shape</c> to 0. Shapes that
    /// need it (<c>requires</c>) see it as absent, and a copy shape (<c>copyOf</c>) of it makes no copies. Not for copy shapes themselves.</summary>
    public PlacementFailure OnFail { get; set; } = PlacementFailure.Fail;
}

/// <summary>placement.retries.onFail: what a shape that cannot be placed does once its own redraws are spent.</summary>
public enum PlacementFailure
{
    /// <summary>Redraw the whole glyph, and when that is spent too, fail the glyph.</summary>
    Fail,

    /// <summary>Leave the shape out of the glyph and carry on with the others.</summary>
    Skip,
}

/// <summary>
/// Where a shape goes: how it starts (placement.start), the area it may use (area), whether a Literal shape may turn (rotation),
/// how often placing it is retried (retries) and a pull towards a side (side).
/// </summary>
public sealed class PlacementConfig
{
    /// <summary>Where and how the shape starts.</summary>
    public PlacementStart Start { get; set; } = new();

    /// <summary>The bounds and node mask the shape must stay in.</summary>
    public PlacementArea Area { get; set; } = new();

    /// <summary>Turning a Literal shape on placement.</summary>
    public PlacementRotation Rotation { get; set; } = new();

    /// <summary>The retry budgets: positions, shape redraws and glyph redraws.</summary>
    public PlacementRetries Retries { get; set; } = new();

    /// <summary>RandomValid only: pull the shape towards a side or corner of the glyph. null = no pull. It ranks
    /// the shape's best-of-N candidates (arrangement.candidates), so raise that for a stronger pull.</summary>
    public SidePull? Side { get; set; }

    // C# shortcuts to the nested options, so the generators read as before. Not json options.
    [JsonIgnore] public PlacementMode Mode { get => Start.Mode; set => Start.Mode = value; }
    [JsonIgnore] public OriginChoice? FixedOrigin { get => Start.Origin; set => Start.Origin = value; }
    [JsonIgnore] public bool DiscardUsedOrigins { get => Start.DiscardUsed; set => Start.DiscardUsed = value; }
    [JsonIgnore] public bool StartOnExistingNode { get => Start.OnExistingNode is not null; set => Start.OnExistingNode = value ? Start.OnExistingNode ?? new() : null; }
    [JsonIgnore] public double StartOnExistingNodeChance { get => Start.OnExistingNode?.Chance ?? 100.0; set => (Start.OnExistingNode ??= new()).Chance = value; }
    [JsonIgnore] public List<string>? StartOnShapes { get => Start.OnExistingNode?.Shapes; set => (Start.OnExistingNode ??= new()).Shapes = value; }
    [JsonIgnore] public PlacementBounds? Bounds { get => Area.Bounds; set => Area.Bounds = value; }
    [JsonIgnore] public NodeMaskConfig? Nodes { get => Area.Nodes; set => Area.Nodes = value; }
    [JsonIgnore] public bool AllowRotationOnPlace { get => Rotation.Allowed; set => Rotation.Allowed = value; }
    [JsonIgnore] public List<int>? Rotations { get => Rotation.Angles; set => Rotation.Angles = value; }
    [JsonIgnore] public int MaxPlacementAttempts { get => Retries.Placement; set => Retries.Placement = value; }
    [JsonIgnore] public int RedoShape { get => Retries.Shape; set => Retries.Shape = value; }
    [JsonIgnore] public int RedrawGlyph { get => Retries.Glyph; set => Retries.Glyph = value; }

    internal PlacementConfig WithOrigins(IEnumerable<GridPoint> origins)
    {
        var copy = (PlacementConfig)MemberwiseClone();
        copy.Start = Start.Clone();
        copy.Start.Origin = new OriginChoice(origins.ToArray());
        return copy;
    }
}

/// <summary>An inclusive box of lattice node coordinates (placement.bounds).</summary>
public sealed class PlacementBounds
{
    /// <summary>Smallest x a node may have (inclusive).</summary>
    public int MinX { get; set; }
    /// <summary>Smallest y a node may have (inclusive).</summary>
    public int MinY { get; set; }
    /// <summary>Largest x a node may have (inclusive).</summary>
    public int MaxX { get; set; }
    /// <summary>Largest y a node may have (inclusive).</summary>
    public int MaxY { get; set; }
}
