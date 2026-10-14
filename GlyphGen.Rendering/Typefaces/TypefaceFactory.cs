using GlyphGen.Core.Config;
using GlyphGen.Core.Rng;

namespace GlyphGen.Rendering.Typefaces;

/// <summary>
/// Builds the language's single typeface. Every numeric setting is drawn from its range ONCE
/// here (from the language's typeface seed), so all glyphs in a language share one consistent
/// font. Widths in config are canvas fractions, converted to pixels here.
/// </summary>
public static class TypefaceFactory
{
    public static ITypeface Create(TypefaceConfig config, JoinsConfig joins, double canvasPx, DeterministicRandom rng)
    {
        switch (config.Kind)
        {
            case TypefaceKind.Rectangular:
            {
                var c = config.Rectangular ?? new RectangularTypefaceConfig();
                return new RectangularTypeface(c.Width.Resolve(rng) * canvasPx, c.Cap, joins, c.CapLevel);
            }

            case TypefaceKind.ThickEnds:
            {
                var c = config.ThickEnds ?? new ThickEndsTypefaceConfig();
                double width = c.Width.Resolve(rng) * canvasPx;
                double endWidth = c.EndWidth.Resolve(rng) * canvasPx;
                double flare = c.FlareLength.Resolve(rng);
                return new ThickEndsTypeface(width, endWidth, flare, c, joins);
            }

            case TypefaceKind.Brush:
            {
                var c = config.Brush ?? new BrushTypefaceConfig();
                double width = c.Width.Resolve(rng) * canvasPx;
                double tip = c.TipWidth.Resolve(rng) * canvasPx;
                double thin = c.ThinWidth.Resolve(rng) * canvasPx;
                double nib = c.NibAngleDegrees.Resolve(rng);
                return new BrushTypeface(c, width, tip, thin, nib, joins);
            }

            default:
                throw new NotSupportedException($"Unknown typeface kind: {config.Kind}");
        }
    }
}
