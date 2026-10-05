// GlyphGen.Cli entry point.
// Deliberately thin: all real logic lives in GlyphGen.Core (generation) and GlyphGen.Rendering
// (PNG output), so both can be reused as referenced DLLs later without dragging along any
// console-specific code.

using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using GlyphGen.Rendering;

if (args.Length < 1)
{
    Console.WriteLine("Usage: GlyphGen.Cli <path-to-config.json>");
    return;
}

RootConfig config;
try
{
    config = RootConfigLoader.LoadFromFile(args[0]);
}
catch (Exception ex)
{
    Console.WriteLine($"Failed to parse config: {ex.Message}");
    return;
}

ILanguageGenerator languageGenerator = new LanguageGenerator(
    new GlyphGenerator(
        new ChainGenerator(),
        new PlacementResolver(),
        new CombinationResolver()));
var orchestrator = new RunOrchestrator(languageGenerator);

IReadOnlyDictionary<string, IReadOnlyList<GlyphGen.Core.Model.Glyph>> results;
try
{
    results = orchestrator.Run(config);
}
catch (GlyphGenerationException ex)
{
    Console.WriteLine($"Generation failed: {ex.Message}");
    return;
}

var renderer = new LanguageRenderer();
Directory.CreateDirectory(config.OutputDirectory);

foreach (var language in config.Languages)
{
    if (!results.TryGetValue(language.Name, out var glyphs)) continue;

    // Base glyphs (components only) are rendered like everything else - so they share the
    // language-wide framing - but go to their own folder and stay out of the contact sheet.
    var baseNames = new HashSet<string>(language.BaseGlyphs.Select(b => b.Name));

    // ASCII preview, straight lines only - same view used during generation-only debugging,
    // useful to compare against the PNG when something looks off.
    Console.WriteLine($"=== {language.Name} ===");
    foreach (var glyph in glyphs)
    {
        if (baseNames.Contains(glyph.Name)) Console.WriteLine("(base glyph)");
        Console.WriteLine(glyph.ToString());
    }

    var languageDir = Path.Combine(config.OutputDirectory, SanitizeFileName(language.Name));
    Directory.CreateDirectory(languageDir);

    var basesDir = Path.Combine(languageDir, "_bases");
    if (baseNames.Count > 0) Directory.CreateDirectory(basesDir);

    var rendered = renderer.Render(language, glyphs, config.RootSeed);

    foreach (var r in rendered)
    {
        string path = Path.Combine(baseNames.Contains(r.GlyphName) ? basesDir : languageDir, SanitizeFileName(r.GlyphName) + ".png");
        File.WriteAllBytes(path, r.Png);
        Console.WriteLine($"Wrote {path}");
    }

    byte[] sheet = ContactSheet.Create(
        rendered.Where(r => !baseNames.Contains(r.GlyphName)).ToList(), columns: 6, language.Render.Background);
    if (sheet.Length > 0)
    {
        string sheetPath = Path.Combine(languageDir, "_contact-sheet.png");
        File.WriteAllBytes(sheetPath, sheet);
        Console.WriteLine($"Wrote {sheetPath}");
    }

    // Debug pass: node dots + one colour per combination component, regardless of what the
    // config's render.debugShowNodes/debugColorComponents say - always available for
    // debugging without having to edit the config back and forth. RenderConfig is a plain
    // mutable class, so toggling in place and restoring afterward is simplest here.
    bool origNodes = language.Render.DebugShowNodes;
    bool origColors = language.Render.DebugColorComponents;
    language.Render.DebugShowNodes = true;
    language.Render.DebugColorComponents = true;

    var debugRendered = renderer.Render(language, glyphs, config.RootSeed);

    language.Render.DebugShowNodes = origNodes;
    language.Render.DebugColorComponents = origColors;

    foreach (var r in debugRendered)
    {
        string path = Path.Combine(baseNames.Contains(r.GlyphName) ? basesDir : languageDir, SanitizeFileName(r.GlyphName) + ".debug.png");
        File.WriteAllBytes(path, r.Png);
        Console.WriteLine($"Wrote {path}");
    }
}

static string SanitizeFileName(string name)
{
    foreach (char c in Path.GetInvalidFileNameChars())
        name = name.Replace(c, '_');
    return name;
}
