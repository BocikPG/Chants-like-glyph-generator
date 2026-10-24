namespace GlyphGen.Core.Config;

/// <summary>
/// One allowed scale factor for overlay composition, expressed as an exact fraction rather
/// than a float — necessary because lattice-safety of shrinking depends on exact divisibility
/// of every node's offset from the component's anchor, which a float range can't express or
/// validate cleanly. The check is made per component:
/// a shrinking factor is only usable if every node's coordinates divide exactly for the specific
/// shape being scaled, so "nicer" geometry (lots of even-length edges) naturally shrinks more
/// often than oddly shaped components, without the generator having to force it.
///
/// Weight orders the fallback search: heavier factors are tried earlier (it multiplies with
/// Rotation.Weight for a rotated placement); 0 = never use this factor. With every weight equal
/// the order is a plain uniform shuffle.
/// </summary>
public sealed class ScaleFactorConfig
{
    /// <summary>Top of the ratio (2 and 3 give 2/3). Must be positive. Default 1.</summary>
    public int Numerator { get; set; } = 1;
    /// <summary>Bottom of the ratio. Must be positive. A ratio above 1 enlarges, below 1 shrinks (only when every node divides exactly). Default 1.</summary>
    public int Denominator { get; set; } = 1;
    /// <summary>How early this ratio is tried relative to the others. 0 = never used. Default 1.</summary>
    public double Weight { get; set; } = 1.0;
}
