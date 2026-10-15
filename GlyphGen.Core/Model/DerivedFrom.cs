namespace GlyphGen.Core.Model;

/// <summary>Where a derived glyph comes from. Its curves are stored on its edges like any other glyph's.</summary>
public sealed class DerivedFrom
{
    public string Source { get; set; } = "";

    /// <summary>True when the renderer should draw this glyph at its source's rotation angle (see DerivationConfig.FinalOutputRotation
    /// and the steps that actually ran).</summary>
    public bool FollowsSourceRotation { get; set; }
}
