namespace GlyphGen.Core.Config;

/// <summary>
/// Allowed rotation angles for overlay composition. Only 0, 90, 180 and 270 are supported (they
/// keep every node on the grid; any other angle, 45 included, would leave the grid) -
/// the config loader rejects other angles.
/// </summary>
public sealed class RotationConfig
{
    /// <summary>Angles (0, 90, 180, 270) a component may be rotated by when combination.allowRotate is on. Default [0].</summary>
    public List<int> Allowed { get; set; } = new() { 0 };

    /// <summary>
    /// One weight shared by every non-zero angle (there are no per-angle weights): how likely a
    /// rotated placement is, relative to an unrotated one (weight 1), when the combination search
    /// falls back to trying scale/rotation combinations. 1 = rotated as likely as not, 0.5 = half
    /// as likely, 0 = never rotate. Multiplies with the scale factor's weight.
    /// </summary>
    public double Weight { get; set; } = 1.0;
}
