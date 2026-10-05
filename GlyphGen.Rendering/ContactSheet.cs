using SkiaSharp;

namespace GlyphGen.Rendering;

/// <summary>
/// One PNG showing a whole language's glyphs in a grid (in config order), handy for eyeballing
/// similarity and consistency. Cells keep the glyphs' own pixel size (no rescaling). No labels,
/// to avoid depending on font availability; the order matches the language's glyph list.
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
            const int gap = 8;

            int width = cols * cellW + (cols + 1) * gap;
            int height = rows * cellH + (rows + 1) * gap;

            SKColor background = SKColor.TryParse(backgroundHex, out var parsed) && parsed.Alpha == 255
                ? parsed
                : SKColors.White; // transparent/translucent backgrounds get white so glyphs stay visible

            using var sheet = new SKBitmap(width, height, SKColorType.Rgba8888, SKAlphaType.Premul);
            using var canvas = new SKCanvas(sheet);
            canvas.Clear(background);

            using var frame = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                StrokeWidth = 1,
                Color = SKColors.LightGray,
            };

            for (int i = 0; i < bitmaps.Count; i++)
            {
                int x = gap + (i % cols) * (cellW + gap);
                int y = gap + (i / cols) * (cellH + gap);
                canvas.DrawBitmap(bitmaps[i], x, y);
                canvas.DrawRect(x, y, cellW, cellH, frame);
            }

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
