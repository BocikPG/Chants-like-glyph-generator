using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;
using GlyphGen.Rendering.Curves;
using GlyphGen.Rendering.Geometry;
using GlyphGen.Rendering.Layout;
using GlyphGen.Rendering.Typefaces;
using SkiaSharp;

namespace GlyphGen.Rendering;

public sealed class RenderedGlyph
{
    public string LanguageName { get; init; } = "";
    public string GlyphName { get; init; } = "";
    public byte[] Png { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// Renders every glyph of one language to PNG: plan curves, resolve the language's single
/// typeface, frame, build ink polygons, fill. Two passes over the glyphs are needed because
/// FramingMode.Language has to know every glyph's bounds before laying out the first one.
///
/// All ink of a glyph goes into ONE path filled once (non-zero winding), so overlaps and
/// crossings come out solid even with a semi-transparent foreground colour. The exception is
/// DebugColorComponents, which fills one path per combination component.
///
/// Geometry (curves, outlines) is deterministic from the seed; the final pixels can differ very
/// slightly across platforms/Skia versions because of anti-aliasing, so don't rely on
/// byte-identical PNGs.
/// </summary>
public sealed class LanguageRenderer
{
    private static readonly SKColor[] DebugPalette =
    {
        SKColor.Parse("#E6194B"), SKColor.Parse("#3CB44B"), SKColor.Parse("#4363D8"),
        SKColor.Parse("#F58231"), SKColor.Parse("#911EB4"), SKColor.Parse("#008080"),
    };

    private sealed record GlyphPlan(Glyph Glyph, PlannedGlyph Planned, ulong Seed, double WidthScale, Bounds? GlyphBounds);

    public IReadOnlyList<RenderedGlyph> Render(LanguageDefinition language, IReadOnlyList<Glyph> glyphs, ulong rootSeed)
    {
        var render = language.Render;
        int size = Math.Max(16, language.Canvas.SizePx);
        double margin = Math.Max(0, language.Canvas.MarginPx);

        ulong languageSeed = SeedTree.ForLanguage(rootSeed, language.Name);
        var typeface = TypefaceFactory.Create(
            render.Typeface, size, new DeterministicRandom(SplitMix64.DeriveSeed(languageSeed, "typeface")));
        double jitter = Math.Clamp(render.Typeface.PerGlyphJitter, 0.0, 0.9);

        // Pass 1: plan every glyph's curves and collect bounds. One planner for the whole
        // language, so a component reuses its source glyph's curves instead of re-rolling them.
        var curvePlanner = new GlyphCurvePlanner(glyphs, language.Curve, languageSeed);
        var plans = new List<GlyphPlan>();
        Bounds? languageBounds = null;

        foreach (var glyph in glyphs)
        {
            ulong glyphSeed = SeedTree.ForGlyph(languageSeed, glyph.Name);
            var planned = curvePlanner.Plan(glyph);

            var widthRng = new DeterministicRandom(SplitMix64.DeriveSeed(glyphSeed, "width"));
            double widthScale = 1.0 + jitter * (widthRng.NextDouble() * 2 - 1);

            Bounds? bounds = planned.TryGetBounds(out var b) ? b : null;
            if (bounds.HasValue)
                languageBounds = languageBounds.HasValue ? Bounds.Union(languageBounds.Value, bounds.Value) : bounds.Value;

            plans.Add(new GlyphPlan(glyph, planned, glyphSeed, widthScale, bounds));
        }

        double pad = margin + typeface.MaxInkExtentPx * (1.0 + jitter) + 1.0;

        SKColor foreground = ParseColor(render.Foreground, SKColors.Black);
        SKColor background = ParseColor(render.Background, SKColors.Transparent);

        // Pass 2: lay out and draw.
        var results = new List<RenderedGlyph>();
        foreach (var plan in plans)
        {
            var gridBounds = new Bounds(0, 0, Math.Max(1, plan.Glyph.GridWidth - 1), Math.Max(1, plan.Glyph.GridHeight - 1));
            var glyphBounds = plan.GlyphBounds ?? gridBounds;
            var langBounds = languageBounds ?? gridBounds;

            var transform = LayoutTransform.Compute(render.Framing, gridBounds, glyphBounds, langBounds, size, pad);
            byte[] png = DrawGlyph(plan, transform, typeface, render, size, foreground, background);

            results.Add(new RenderedGlyph { LanguageName = language.Name, GlyphName = plan.Glyph.Name, Png = png });
        }

        return results;
    }

    /// <summary>
    /// Patches the gap between two DIFFERENT open chains that happen to end at the same grid
    /// node - a case the per-chain join logic never sees, since joins only ever run between
    /// two segments of the same chain. Builds a REAL join (Miter/Bevel/Round/Overlap, per the
    /// typeface's own settings) using each chain's actual pixel-space tip direction, the same
    /// way an interior corner within one chain is handled - not a generic round patch, so
    /// Miter still gives the sharp "&gt;" look at these junctions too.
    ///
    /// Grid coordinates are exact integers carried through as doubles, so exact equality is
    /// safe for matching "the same node" (no epsilon/clustering needed) - only genuinely
    /// identical shared nodes are patched. Scoped to one layer at a time (the glyph's own
    /// chains, or one combination component); a junction between two DIFFERENT components
    /// isn't currently patched - flag if that turns out to matter.
    /// </summary>
    private static void FillSharedTipJoints(
        List<(PlannedChain Chain, StrokePath Path)> built,
        double tipHalfWidthPx,
        JoinStyle join,
        double miterLimit,
        List<Vec2[]> polygons)
    {
        if (tipHalfWidthPx <= 1e-9) return;

        // DirIn points INTO the tip (the direction of travel arriving there), matching the
        // convention interior joins use for their "incoming" side.
        var tips = new List<(Vec2 Grid, Vec2 Pixel, Vec2 DirIn)>();
        foreach (var (chain, path) in built)
        {
            if (chain.Closed || chain.Edges.Count == 0 || path.Points.Count < 2) continue;

            tips.Add((chain.Edges[0].P0, path.Points[0], (path.Points[0] - path.Points[1]).Normalized()));
            tips.Add((chain.Edges[^1].P3, path.Points[^1], (path.Points[^1] - path.Points[^2]).Normalized()));
        }

        var handled = new HashSet<int>();
        for (int i = 0; i < tips.Count; i++)
        {
            if (handled.Contains(i)) continue;

            var group = new List<int> { i };
            for (int j = i + 1; j < tips.Count; j++)
                if (tips[j].Grid.X == tips[i].Grid.X && tips[j].Grid.Y == tips[i].Grid.Y)
                    group.Add(j);

            if (group.Count < 2) continue;
            foreach (var idx in group) handled.Add(idx);

            // Pairwise between every pair at this node - covers 3+ chains meeting at one point,
            // not just exactly 2. Treat b's side as "outgoing" by negating its tip direction, so
            // this reads as one continuous stroke bending from a into b at the shared point.
            for (int a = 0; a < group.Count; a++)
                for (int b = a + 1; b < group.Count; b++)
                {
                    var ta = tips[group[a]];
                    var tb = tips[group[b]];
                    StrokeOutlineBuilder.AddExternalJoin(
                        ta.Pixel, ta.DirIn, -tb.DirIn, tipHalfWidthPx, tipHalfWidthPx, join, miterLimit, polygons);
                }
        }
    }

    private static byte[] DrawGlyph(
        GlyphPlan plan, LayoutTransform transform, ITypeface typeface, RenderConfig render,
        int size, SKColor foreground, SKColor background)
    {
        // Ink polygons per layer (a layer is the glyph's own chains, or one combination component).
        var layerPolygons = new List<List<Vec2[]>>();
        int chainIndex = 0;

        foreach (var layer in plan.Planned.Layers)
        {
            var polygons = new List<Vec2[]>();
            var built = new List<(PlannedChain, StrokePath)>();

            foreach (var chain in layer.Chains)
            {
                var path = StrokePathBuilder.Build(chain, transform.Apply, typeface.SampleSpacingPx);
                if (path.Points.Count < 2) { chainIndex++; continue; }

                var context = new StrokeContext
                {
                    WidthScale = plan.WidthScale,
                    PathSeed = SplitMix64.DeriveSeed(plan.Seed, chainIndex++),
                };
                typeface.AppendOutline(path, context, polygons);
                built.Add((chain, path));
            }

            double tipHalf = typeface.TipHalfWidthPx * plan.WidthScale;
            FillSharedTipJoints(built, tipHalf, typeface.Join, typeface.MiterLimit, polygons);

            layerPolygons.Add(polygons);
        }

        using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(background);

        bool separateLayers = render.DebugColorComponents && layerPolygons.Count > 1;
        if (separateLayers)
        {
            for (int i = 0; i < layerPolygons.Count; i++)
                FillPolygons(canvas, layerPolygons[i], DebugPalette[i % DebugPalette.Length]);
        }
        else
        {
            var all = new List<Vec2[]>();
            foreach (var polygons in layerPolygons) all.AddRange(polygons);
            FillPolygons(canvas, all, foreground);
        }

        if (render.DebugShowNodes)
        {
            // Full lattice, faint, drawn first - lets you line up a PNG position with the
            // ASCII grid coordinates from the console output.
            using var latticeDot = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0, 0, 0, 60) };
            float latticeRadius = (float)Math.Max(1.0, size * 0.0025);
            for (int gx = 0; gx < plan.Glyph.GridWidth; gx++)
                for (int gy = 0; gy < plan.Glyph.GridHeight; gy++)
                {
                    var p = transform.Apply(new Vec2(gx, gy));
                    canvas.DrawCircle((float)p.X, (float)p.Y, latticeRadius, latticeDot);
                }

            // Coordinate labels every 5 cells, so a position can be read off without counting
            // dots by hand. Wrapped defensively: SKTypeface.Default depends on system fonts
            // being present, which isn't guaranteed on a minimal Linux container - if it's not
            // available, skip labels rather than losing the whole debug image over it.
            try
            {
                using var labelPaint = new SKPaint { IsAntialias = true, Color = new SKColor(0, 0, 0, 140) };
                using var font = new SKFont(SKTypeface.Default, (float)Math.Max(8.0, size * 0.02));
                for (int gx = 0; gx < plan.Glyph.GridWidth; gx += 5)
                    for (int gy = 0; gy < plan.Glyph.GridHeight; gy += 5)
                    {
                        var p = transform.Apply(new Vec2(gx, gy));
                        canvas.DrawText($"{gx},{gy}", (float)p.X + 3, (float)p.Y - 3, font, labelPaint);
                    }
            }
            catch
            {
                // Labels are a nice-to-have for reading the grid; the dots below still work.
            }

            // A node appearing more than once (a chain revisiting its own earlier point, or two
            // DIFFERENT chains/components sharing one) gets flagged in yellow, ahead of the
            // usual red/blue distinction - these are exactly the "coincidence" points worth
            // spotting at a glance, and the ones FillSharedTipJoints above is patching for.
            var occurrences = new Dictionary<GridPoint, int>();
            foreach (var chain in GlyphGeometryUtils.GetAllChains(plan.Glyph))
                foreach (var node in chain.Nodes)
                    occurrences[node] = occurrences.GetValueOrDefault(node) + 1;

            using var freeDot = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.Red };
            using var motifDot = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.Blue };
            using var stackedDot = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.Gold };
            float radius = (float)Math.Max(3.0, size * 0.008);
            foreach (var chain in GlyphGeometryUtils.GetAllChains(plan.Glyph))
            {
                var paint = chain.MotifId is not null ? motifDot : freeDot;
                foreach (var node in chain.Nodes)
                {
                    if (occurrences[node] >= 2) paint = stackedDot;
                    var p = transform.Apply(new Vec2(node.X, node.Y));
                    canvas.DrawCircle((float)p.X, (float)p.Y, radius, paint);
                }
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static void FillPolygons(SKCanvas canvas, List<Vec2[]> polygons, SKColor color)
    {
        if (polygons.Count == 0) return;

        using var path = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var poly in polygons)
        {
            path.MoveTo((float)poly[0].X, (float)poly[0].Y);
            for (int i = 1; i < poly.Length; i++)
                path.LineTo((float)poly[i].X, (float)poly[i].Y);
            path.Close();
        }

        using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
        canvas.DrawPath(path, paint);
    }

    private static SKColor ParseColor(string hex, SKColor fallback) =>
        SKColor.TryParse(hex, out var color) ? color : fallback;
}
