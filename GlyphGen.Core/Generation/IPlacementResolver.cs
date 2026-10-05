using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

public sealed class PlacementResult
{
    public bool Success { get; set; }
    public GridPoint Origin { get; set; }
    public int RotationDegrees { get; set; }
    public int AttemptsUsed { get; set; }

    /// <summary>The actual placed geometry, already translated/rotated into absolute grid
    /// coordinates and validated against existingGeometry. Null when Success is false.</summary>
    public SubSymbolChain? PlacedShape { get; set; }
}

/// <summary>
/// Resolves WHERE a shape (motif or free-form chain) ends up, per its PlacementConfig, and
/// validates the result against everything already committed to the glyph.
///
/// Shape production is supplied by the caller as shapeFactory: a Literal motif's shape is just
/// a translated/rotated fixed template (identical geometry every time), while a Generative
/// motif's shape comes from IChainGenerator, can differ on every attempt, and can legitimately
/// fail to reach its target length. shapeVariesPerAttempt tells this resolver which case it's
/// dealing with - purely so it can decide whether repeating an otherwise-identical attempt
/// (same origin, same rotation) is worth doing at all.
///
/// How many *meaningfully different* attempts are even possible comes from three independent
/// axes: position (varies only under RandomValid), rotation (varies only when
/// AllowRotationOnPlace is set and rotationOptions offers more than one angle), and shape
/// (varies only when shapeVariesPerAttempt is true). When none of the three can vary, retrying
/// is pointless - effective attempts get clamped to 1 rather than repeating an identical check
/// MaxPlacementAttempts times, regardless of which specific mode/motif-type combination
/// produced that situation. This generalizes the Fixed+Literal "zero degrees of freedom" case
/// noted earlier into one computed rule that also covers e.g. Fixed+Literal with rotation
/// still enabled, where rotation alone is a real remaining degree of freedom.
/// </summary>
public interface IPlacementResolver
{
    PlacementResult TryResolve(
        PlacementConfig config,
        LatticeGrid grid,
        DeterministicRandom rng,
        IIntersectionContext existingGeometry,
        bool shapeVariesPerAttempt,
        RotationConfig? rotationOptions,
        Func<GridPoint, int, DeterministicRandom, SubSymbolChain?> shapeFactory);
}
