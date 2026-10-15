using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;
using GlyphGen.Rendering.Curves;
using GlyphGen.Core.Geometry;
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

    private sealed record GlyphPlan(Glyph Glyph, PlannedGlyph Planned, ulong Seed, double WidthScale, Bounds? GlyphBounds, double RotateDegrees);

    /// <summary>The angle a glyph is drawn at. A derived glyph that only scaled, mirrored, rotated or nudged its source
    /// takes the source's angle (followed back to the first glyph that is not such a derivation), so the pair stays aligned.</summary>
    private static double RotationFor(Glyph glyph, IReadOnlyDictionary<string, Glyph> byName, RenderConfig render, ulong languageSeed)
    {
        var seen = new HashSet<string>();
        var origin = glyph;
        while (origin.Derived is { FollowsSourceRotation: true } d && seen.Add(origin.Name) && byName.TryGetValue(d.Source, out var src))
            origin = src;

        double rotate = render.RotateDegrees;
        if (render.RotateOptions.Count > 0)
        {
            var rotateRng = new DeterministicRandom(SplitMix64.DeriveSeed(SeedTree.ForGlyph(languageSeed, origin.Name), "rotate"));
            var option = render.RotateOptions[rotateRng.WeightedIndex(render.RotateOptions.Select(o => o.Weight).ToList())];
            rotate += option.Min + rotateRng.NextDouble() * (option.Max - option.Min);
        }
        return rotate;
    }

    public IReadOnlyList<RenderedGlyph> Render(LanguageDefinition language, IReadOnlyList<Glyph> glyphs, ulong rootSeed)
    {
        var render = language.Render;
        int size = Math.Max(16, language.Canvas.SizePx);
        double margin = Math.Max(0, language.Canvas.MarginPx);

        ulong languageSeed = SeedTree.ForLanguage(rootSeed, language.Name);
        var typeface = TypefaceFactory.Create(
            render.Typeface, render.Joins, size, new DeterministicRandom(SplitMix64.DeriveSeed(languageSeed, "typeface")));
        double jitter = Math.Clamp(render.Typeface.PerGlyphJitter, 0.0, 0.9);

        // Pass 1: plan every glyph's curves and collect bounds. One planner for the whole
        // language, so a component reuses its source glyph's curves instead of re-rolling them.
        var curvePlanner = new GlyphCurvePlanner(glyphs, language.Curve, languageSeed);
        var plans = new List<GlyphPlan>();
        Bounds? languageBounds = null;

        var byName = glyphs.GroupBy(g => g.Name).ToDictionary(g => g.Key, g => g.First());
        foreach (var glyph in glyphs)
        {
            ulong glyphSeed = SeedTree.ForGlyph(languageSeed, glyph.Name);
            var planned = curvePlanner.Plan(glyph);
            double rotate = RotationFor(glyph, byName, render, languageSeed);

            if (rotate % 360.0 != 0.0)
                planned = planned.Rotated(rotate, new Vec2((glyph.GridWidth - 1) / 2.0, (glyph.GridHeight - 1) / 2.0));

            var widthRng = new DeterministicRandom(SplitMix64.DeriveSeed(glyphSeed, "width"));
            double widthScale = 1.0 + jitter * (widthRng.NextDouble() * 2 - 1);

            Bounds? bounds = planned.TryGetBounds(out var b) ? b : null;
            if (bounds.HasValue)
                languageBounds = languageBounds.HasValue ? Bounds.Union(languageBounds.Value, bounds.Value) : bounds.Value;

            plans.Add(new GlyphPlan(glyph, planned, glyphSeed, widthScale, bounds, rotate));
        }

        double pad = margin + typeface.MaxInkExtentPx * (1.0 + jitter) + 1.0;

        SKColor foreground = ParseColor(render.Foreground, SKColors.Black);
        SKColor background = ParseColor(render.Background, SKColors.Transparent);

        // Debug colouring by shape: each shape's place in the shapes list.
        var shapeIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var shape in language.Shapes) shapeIndex.TryAdd(shape.Id, shapeIndex.Count);

        // Pass 2: lay out and draw.
        var results = new List<RenderedGlyph>();
        foreach (var plan in plans)
        {
            var gridBounds = new Bounds(0, 0, Math.Max(1, plan.Glyph.GridWidth - 1), Math.Max(1, plan.Glyph.GridHeight - 1));
            var glyphBounds = plan.GlyphBounds ?? gridBounds;
            var langBounds = languageBounds ?? gridBounds;

            var transform = LayoutTransform.Compute(render.Framing, gridBounds, glyphBounds, langBounds, size, pad);
            byte[] png = DrawGlyph(plan, transform, typeface, render, size, foreground, background, shapeIndex);

            results.Add(new RenderedGlyph { LanguageName = language.Name, GlyphName = plan.Glyph.Name, Png = png });
        }

        return results;
    }

    private static byte[] DrawGlyph(
        GlyphPlan plan, LayoutTransform transform, ITypeface typeface, RenderConfig render,
        int size, SKColor foreground, SKColor background, IReadOnlyDictionary<string, int> shapeIndex)
    {
        // Ink polygons per layer (a layer is the glyph's own chains, or one combination component).
        var layerPolygons = new List<List<Vec2[]>>();
        var layerSinks = new List<JoinSink>();
        var layerColors = new List<Dictionary<Vec2[], SKColor>>();
        bool byShape = render.DebugColorShapes;
        int chainIndex = 0;

        foreach (var layer in plan.Planned.Layers)
        {
            var polygons = new List<Vec2[]>();
            // Every stroke of this layer records its nodes here; joins are built per node afterwards,
            // so a bend, a meeting of strokes and a crossing all go through the same code.
            var joinSink = new JoinSink();
            var colors = new Dictionary<Vec2[], SKColor>(ReferenceEqualityComparer.Instance);

            foreach (var chain in layer.Chains)
            {
                int firstPolygon = polygons.Count;
                var shapeColor = byShape ? ShapeColor(chain.ShapeTag, shapeIndex) : (SKColor?)null;

                if (chain.Kind == PlannedChainKind.Dot)
                {
                    // A dot is just a disc, thick as the stroke; it takes no part in joins.
                    StrokeOutlineBuilder.AddDisc(transform.Apply(chain.Edges[0].P0),
                        typeface.DotRadiusPx * plan.WidthScale, polygons);
                    if (shapeColor is { } dotColor) for (int k = firstPolygon; k < polygons.Count; k++) colors[polygons[k]] = dotColor;
                    chainIndex++;
                    continue;
                }

                var path = StrokePathBuilder.Build(chain, transform.Apply, typeface.SampleSpacingPx);
                if (path.Points.Count < 2) { chainIndex++; continue; }

                int strokeId = chainIndex;
                var context = new StrokeContext
                {
                    WidthScale = plan.WidthScale,
                    PathSeed = SplitMix64.DeriveSeed(plan.Seed, chainIndex++),
                    JoinSink = joinSink,
                    StrokeId = strokeId,
                };
                typeface.AppendOutline(path, context, polygons);
                if (shapeColor is { } strokeColor) for (int k = firstPolygon; k < polygons.Count; k++) colors[polygons[k]] = strokeColor;
            }

            JoinBuilder.BuildNodes(joinSink, render.Joins, polygons);

            layerPolygons.Add(polygons);
            layerSinks.Add(joinSink);
            layerColors.Add(colors);
        }

        using var bitmap = new SKBitmap(size, size, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(background);

        bool separateLayers = render.DebugColorComponents && layerPolygons.Count > 1 && !byShape;
        if (byShape)
        {
            for (int i = 0; i < layerPolygons.Count; i++)
                FillByShape(canvas, layerPolygons[i], layerColors[i], layerSinks[i].Kinds, foreground, render.DebugColorParts);
        }
        else if (render.DebugColorParts)
        {
            // Debug only: strokes in their colour, then the ink that joins and caps add, in their own colours.
            for (int i = 0; i < layerPolygons.Count; i++)
            {
                var bodyColor = separateLayers ? DebugPalette[i % DebugPalette.Length] : foreground;
                FillParts(canvas, layerPolygons[i], layerSinks[i].Kinds, bodyColor);
            }
        }
        else if (separateLayers)
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
            // The strokes were turned when they were planned; the overlays (lattice, labels, node dots) turn the same way.
            double turn = plan.RotateDegrees * Math.PI / 180.0, cos = Math.Cos(turn), sin = Math.Sin(turn);
            double midX = (plan.Glyph.GridWidth - 1) / 2.0, midY = (plan.Glyph.GridHeight - 1) / 2.0;
            Vec2 Turn(double x, double y) => new(
                midX + cos * (x - midX) - sin * (y - midY),
                midY + sin * (x - midX) + cos * (y - midY));

            // Full lattice, faint, drawn first - lets you line up a PNG position with the
            // ASCII grid coordinates from the console output.
            using var latticeDot = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0, 0, 0, 60) };
            float latticeRadius = (float)Math.Max(1.0, size * 0.0025);
            for (int gx = 0; gx < plan.Glyph.GridWidth; gx++)
                for (int gy = 0; gy < plan.Glyph.GridHeight; gy++)
                {
                    var p = transform.Apply(Turn(gx, gy));
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
                        var p = transform.Apply(Turn(gx, gy));
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
                var chainPaint = chain.MotifId is not null ? motifDot : freeDot;
                foreach (var node in chain.Nodes)
                {
                    // Chosen per node: only the points that really occur twice are yellow.
                    var paint = occurrences[node] >= 2 ? stackedDot : chainPaint;
                    var p = transform.Apply(Turn(node.X, node.Y));
                    canvas.DrawCircle((float)p.X, (float)p.Y, radius, paint);
                }
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    /// <summary>Debug: polygons grouped by their shape's colour, one fill per colour. Polygons nobody claimed (joins between strokes)
    /// keep the foreground colour. With the parts view on, each group is drawn as stroke bodies plus the coloured parts.</summary>
    private static void FillByShape(
        SKCanvas canvas, List<Vec2[]> polygons, Dictionary<Vec2[], SKColor> colors,
        IReadOnlyDictionary<Vec2[], InkKind> kinds, SKColor foreground, bool parts)
    {
        foreach (var group in polygons.GroupBy(p => colors.TryGetValue(p, out var c) ? c : foreground))
        {
            var list = group.ToList();
            if (parts) FillParts(canvas, list, kinds, group.Key);
            else FillPolygons(canvas, list, group.Key);
        }
    }

    /// <summary>A colour for "shapeId#instance": the hue belongs to the shape (spread around the colour wheel in list order, by the
    /// golden angle so neighbours differ), and the instances of that shape step through lightness and drift a little in hue.</summary>
    private static SKColor ShapeColor(string? tag, IReadOnlyDictionary<string, int> shapeIndex)
    {
        if (tag is null) return SKColors.Gray;
        int at = tag.LastIndexOf('#');
        string id = at < 0 ? tag : tag[..at];
        int instance = at >= 0 && int.TryParse(tag[(at + 1)..], out var n) ? n : 0;
        int index = shapeIndex.TryGetValue(id, out var found) ? found : shapeIndex.Count;

        float hue = (float)((index * 137.508 + 15.0) % 360.0) + (instance % 5 - 2) * 7f;
        float[] lightness = { 46f, 62f, 32f, 72f, 54f, 38f };
        return SKColor.FromHsl((hue % 360f + 360f) % 360f, 78f, lightness[instance % lightness.Length]);
    }

    private static readonly SKColor CapColor = SKColors.Gray;
    private static readonly SKColor CornerColor = new(148, 0, 211);     // dark violet
    private static readonly SKColor JunctionColor = SKColors.HotPink;

    private static void AddPolygon(SKPath path, Vec2[] poly)
    {
        path.MoveTo((float)poly[0].X, (float)poly[0].Y);
        for (int i = 1; i < poly.Length; i++)
            path.LineTo((float)poly[i].X, (float)poly[i].Y);
        path.Close();
    }

    /// <summary>
    /// Debug "parts" view of one layer: the stroke bodies in bodyColor, then every cap / corner join /
    /// junction join in its own colour, in the order they were built. Each part is painted only where it
    /// goes beyond the bodies, so what you see is the ink that part actually adds.
    /// </summary>
    private static void FillParts(SKCanvas canvas, List<Vec2[]> polygons, IReadOnlyDictionary<Vec2[], InkKind> kinds, SKColor bodyColor)
    {
        using var bodies = new SKPath { FillType = SKPathFillType.Winding };
        foreach (var poly in polygons)
            if (!kinds.ContainsKey(poly)) AddPolygon(bodies, poly);

        using (var bodyPaint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = bodyColor })
            canvas.DrawPath(bodies, bodyPaint);

        foreach (var poly in polygons)
        {
            if (!kinds.TryGetValue(poly, out var kind)) continue;

            using var part = new SKPath { FillType = SKPathFillType.Winding };
            AddPolygon(part, poly);

            // Only the part outside the strokes; if Skia can't compute it, show the whole polygon.
            using var outside = part.Op(bodies, SKPathOp.Difference);
            var color = kind switch
            {
                InkKind.Cap => CapColor,
                InkKind.Corner => CornerColor,
                _ => JunctionColor,
            };

            using var paint = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = color };
            canvas.DrawPath(outside ?? part, paint);
        }
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
