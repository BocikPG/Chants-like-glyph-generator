namespace GlyphGen.Core.Config;

/// <summary>
/// One allowed scale factor for overlay composition, expressed as an exact fraction rather
/// than a float — necessary because lattice-safety of shrinking depends on exact divisibility
/// of every node's offset from the component's anchor, which a float range can't express or
/// validate cleanly. See LatticeScaleValidator (Generation) for the actual per-component check:
/// a candidate factor is only usable if it validates against the specific shape being scaled,
/// so "nicer" geometry (lots of even-length edges) naturally shrinks more often than oddly
/// shaped components, without the generator having to force it.
/// </summary>
public sealed class ScaleFactorConfig
{
    public int Numerator { get; set; } = 1;
    public int Denominator { get; set; } = 1;
    public double Weight { get; set; } = 1.0;
}
