namespace GlyphGen.Core.Config;

/// <summary>
/// Allowed rotation angles for overlay composition. Must stay lattice-preserving:
/// 0/90/180/270 are always safe; 45 is only valid when the owning language's
/// connectionRules include Diagonal (validated at config-load time, not here).
/// </summary>
public sealed class RotationConfig
{
    public List<int> Allowed { get; set; } = new() { 0 };
    public double Weight { get; set; } = 1.0;
}
