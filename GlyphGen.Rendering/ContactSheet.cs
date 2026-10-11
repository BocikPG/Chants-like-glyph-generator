using SkiaSharp;

namespace GlyphGen.Rendering;

/// <summary>
/// One PNG showing a whole language's glyphs in a grid (in config order), handy for eyeballing
/// similarity and consistency. The images sit edge to edge: no margins, no gaps, no frames, and they keep
/// their own pixel size (no rescaling). The sheet uses render.background exactly as configured, transparent
/// or translucent included. No labels, to avoid depending on font availability; the order matches the
/// language's glyph list.
/// </summary>
public static class ContactSheet
{
    public static byte[] Create(IReadOnlyList<RenderedGlyph> glyphs, int columns, string backgroundHex)
    {
        if (glyphs.Count == 0) return Array.Empty<byte>();

        var bitmaps = glyphs.Select(g => SKBitmap.Decode(g.Png)).ToList();
        try
        {
            int cellW = bitmaps.Max(b => b.Width);
            int cellH = bitmaps.Max(b => b.Height);
            int cols = Math.Clamp(columns, 1, glyphs.Count);
            int rows = (glyphs.Count + cols - 1) / cols;
            int width = cols * cellW;
            int height = rows * cellH;

            // The configured background as it is (transparent included); an unparsable value falls back to
            // transparent, like the glyph images themselves.
            SKColor background = SKColor.TryParse(backgroundHex, out var parsed) ? parsed : SKColors.Transparent;

            using var sheet = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(sheet);
            canvas.Clear(background);

            // Copy each glyph as it is. Blending would stack a translucent glyph background on the sheet's
            // own and change the colour.
            using var copy = new SKPaint { BlendMode = SKBlendMode.Src };

            for (int i = 0; i < bitmaps.Count; i++)
                canvas.DrawBitmap(bitmaps[i], (i % cols) * cellW, (i / cols) * cellH, copy);

            using var image = SKImage.FromBitmap(sheet);
            using var data = image.Encode(SKEncodedImageFormat.Png, 100);
            return data.ToArray();
        }
        finally
        {
            foreach (var b in bitmaps) b.Dispose();
        }
    }
}
