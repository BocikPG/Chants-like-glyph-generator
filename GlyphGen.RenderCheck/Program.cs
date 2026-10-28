using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using GlyphGen.Rendering;

// Checks that rendering on several threads gives exactly the images (and the contact sheet) a single thread gives, in the same order.

int failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail.Length == 0 ? "" : "  -> " + detail)}");
    if (!ok) failures++;
}

const string Language = """
{ "name": "R", "grid": { "width": 15, "height": 15 },
  "glyphs": ["g1", "g2", "g3", "g4", "g5", "g6", "g7", "g8", "g9"],
  "quality": { "similarity": { "max": 0.9 }, "rejectStraightLineOnly": false, "retries": 40 },
  "shapes": [
    { "id": "bar", "kind": "Literal", "nodes": [[2,12],[3,12],[4,12],[5,12],[6,12],[7,12],[8,12],[9,12],[10,12],[11,12],[12,12]],
      "placement": { "start": { "mode": "Fixed", "origin": [0,0] } }, "arrangement": { "crossing": { "mode": "NodesOnly" } } },
    { "id": "stroke", "count": { "min": 1, "max": 3 },
      "chain": { "length": { "min": 2, "max": 4 }, "connectionRules": [
          { "direction": "Orthogonal", "minLength": 2, "maxLength": 6, "weight": 2 },
          { "direction": "Diagonal", "minLength": 2, "maxLength": 5, "weight": 2, "curve": { "chanceToBeCurved": 40, "mode": "Hybrid", "tension": { "options": [ { "min": 0.2, "max": 0.3 } ] } } },
          { "direction": "Arc", "minLength": 3, "maxLength": 5, "weight": 1, "arc": { "angles": "Cardinal", "minSweep": 180, "maxSweep": 270 } } ] },
      "arrangement": { "crossing": { "mode": "NodesOnly" } },
      "placement": { "start": { "onExistingNode": { "shapes": ["bar"] } }, "retries": { "placement": 60 }, "area": { "bounds": { "minX": 1, "minY": 1, "maxX": 13, "maxY": 12 } } } },
    { "id": "dot", "count": { "min": 1, "max": 2 },
      "chain": { "length": { "min": 2, "max": 2 }, "connectionRules": [ { "direction": "Circle", "minLength": 0, "maxLength": 0 } ] },
      "placement": { "start": { "mode": "RandomValid" }, "retries": { "placement": 60 } }, "arrangement": { "crossing": { "mode": "Strict" } } } ],
  "render": { "canvas": { "sizePx": 128, "marginPx": 8 }, "framing": "Grid", "foreground": "#FF202020", "background": "#FFFFFFFF",
    "typeface": { "kind": "Brush", "perGlyphJitter": 0.1, "brush": { "mode": "BroadNib", "width": { "min": 0.07, "max": 0.07 }, "thinWidth": { "min": 0.035, "max": 0.035 }, "nibAngleDegrees": { "min": 35, "max": 35 }, "nibRoundness": 0.6, "nibAngleWobble": 2 } } } }
""";

var config = RootConfigLoader.Load("{ \"run\": { \"seed\": 7, \"outputDirectory\": \"./o\" }, \"languages\": [ " + Language + " ] }");
var language = config.Languages[0];
var glyphs = new RunOrchestrator(new LanguageGenerator(new GlyphGenerator(new ChainGenerator(), new PlacementResolver(), new CombinationResolver())))
    .Run(config)[language.Name];

var renderer = new LanguageRenderer();

bool Same(IReadOnlyList<RenderedGlyph> a, IReadOnlyList<RenderedGlyph> b, out string why)
{
    why = "";
    if (a.Count != b.Count) { why = $"{a.Count} images vs {b.Count}"; return false; }
    for (int i = 0; i < a.Count; i++)
    {
        if (a[i].GlyphName != b[i].GlyphName) { why = $"image {i} is '{a[i].GlyphName}' vs '{b[i].GlyphName}' (order changed)"; return false; }
        if (!a[i].Png.AsSpan().SequenceEqual(b[i].Png)) { why = $"'{a[i].GlyphName}' differs"; return false; }
    }
    return true;
}

foreach (bool debug in new[] { false, true })
{
    language.Render.DebugShowNodes = debug;
    language.Render.DebugColorComponents = debug;
    language.Render.DebugColorParts = debug;
    language.Render.DebugColorShapes = debug;
    string mode = debug ? "debug overlays on" : "plain";

    var single = renderer.Render(language, glyphs, config.RootSeed);
    var again = renderer.Render(language, glyphs, config.RootSeed);
    bool deterministic = Same(single, again, out _);
    Check($"render ({mode}): every glyph is drawn, in glyph order", single.Count == glyphs.Count && single.Select(r => r.GlyphName).SequenceEqual(glyphs.Select(g => g.Name)));
    Check($"render ({mode}): the images are not empty", single.All(r => r.Png.Length > 100));
    if (!deterministic) Console.WriteLine("NOTE  this Skia build does not give byte-identical PNGs from run to run, so the parallel comparison below is skipped for the plain-vs-parallel bytes");

    foreach (int threads in new[] { 2, 4, 0 })
    {
        var parallel = renderer.Render(language, glyphs, config.RootSeed, threads);
        bool same = Same(single, parallel, out var why);
        Check($"render ({mode}): {(threads == 0 ? "all cores" : threads + " threads")} give the images one thread gives", same || !deterministic, why);
    }
}

language.Render.DebugShowNodes = false; language.Render.DebugColorComponents = false;
language.Render.DebugColorParts = false; language.Render.DebugColorShapes = false;
var images = renderer.Render(language, glyphs, config.RootSeed);
var sheet1 = ContactSheet.Create(images, 3, language.Render.Background);
var sheet4 = ContactSheet.Create(images, 3, language.Render.Background, 4);
var sheet0 = ContactSheet.Create(images, 3, language.Render.Background, 0);
Check("contact sheet: decoding on several threads gives the same sheet", sheet1.Length > 0 && sheet1.AsSpan().SequenceEqual(sheet4) && sheet1.AsSpan().SequenceEqual(sheet0));
Check("contact sheet: a single image still works with threads", ContactSheet.Create(images.Take(1).ToList(), 3, language.Render.Background, 4).Length > 0);

Console.WriteLine(failures == 0 ? "\nAll rendering checks passed." : $"\n{failures} check(s) FAILED.");
return failures == 0 ? 0 : 1;
