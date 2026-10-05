using GlyphGen.Core.Config;

namespace GlyphGen.Rendering.Typefaces;

public static class WidthProfiles
{
    /// <summary>Maps t in [0,1] (0 = start of the flare, 1 = its tip) to a blend factor in [0,1].</summary>
    public static double Apply(WidthProfile profile, double t)
    {
        t = Math.Clamp(t, 0.0, 1.0);
        return profile switch
        {
            WidthProfile.Smooth => t * t * (3 - 2 * t),
            WidthProfile.Sharp => t * t * t,
            _ => t,
        };
    }
}
