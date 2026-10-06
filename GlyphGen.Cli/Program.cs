// GlyphGen.Cli entry point.
// Deliberately thin: all real logic lives in GlyphGen.Core (generation) and GlyphGen.Rendering
// (PNG output), so both can be reused as referenced DLLs later without dragging along any
// console-specific code.

using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using GlyphGen.Rendering;

// GlyphGen.Cli --schema [file]: write the JSON Schema for the config (to a file, or stdout).
if (args.Length >= 1 && args[0] == "--schema")
{
    string schema = ConfigSchemaGenerator.Generate();
    if (args.Length >= 2)
    {
        File.WriteAllText(args[1], schema);
        Console.WriteLine($"Wrote {args[1]}");
    }
    else
    {
        Console.WriteLine(schema);
    }
    return;
}

// GlyphGen.Cli --migrate old.json [new.json]: convert a pre-shapes config to the current layout
// (same glyphs for the same seed). Writes to new.json, or to stdout when no output file is given.
if (args.Length >= 2 && args[0] == "--migrate")
{
    try
    {
        string? migrated = ConfigMigrator.Migrate(File.ReadAllText(args[1]));
        if (migrated is null)
        {
            Console.Error.WriteLine("Nothing to migrate: the config already uses the shapes layout.");
            return;
        }

        if (args.Length >= 3)
        {
            File.WriteAllText(args[2], migrated);
            Console.WriteLine($"Wrote {args[2]}");
        }
        else
        {
            Console.WriteLine(migrated);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not migrate '{args[1]}': {ex.Message}");
        Environment.ExitCode = 1;
    }
    return;
}

if (args.Length < 1)
{
    Console.WriteLine("Usage: GlyphGen.Cli <path-to-config.json>");
    Console.WriteLine("       GlyphGen.Cli --schema [config.schema.json]");
    Console.WriteLine("       GlyphGen.Cli --migrate old.json [new.json]");
    Environment.ExitCode = 1;
    return;
}

RootConfig config;
try
{
    config = RootConfigLoader.LoadFromFile(args[0]);
}
catch (ConfigValidationException ex)
{
    // Already reads "Invalid config:" followed by one problem per line.
    Console.WriteLine(ex.Message);
    Environment.ExitCode = 1;
    return;
}
catch (System.Text.Json.JsonException ex)
{
    Console.WriteLine($"Could not read the config as JSON: {ex.Message}");
    Environment.ExitCode = 1;
    return;
}
catch (Exception ex)
{
    Console.WriteLine($"Could not load config '{args[0]}': {ex.Message}");
    Environment.ExitCode = 1;
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
    Environment.ExitCode = 1;
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

    // The normal images are always clean. The debug options (render.debugShowNodes /
    // debugColorComponents) add a second set, "<glyph>.debug.png", drawn with the enabled overlays;
    // with both off no debug files are written. RenderConfig is a plain mutable class, so toggling
    // in place and restoring afterward is simplest here.
    bool wantNodes = language.Render.DebugShowNodes;
    bool wantColors = language.Render.DebugColorComponents;

    language.Render.DebugShowNodes = false;
    language.Render.DebugColorComponents = false;
    var rendered = renderer.Render(language, glyphs, config.RootSeed);

    IReadOnlyList<RenderedGlyph>? debugRendered = null;
    if (wantNodes || wantColors)
    {
        language.Render.DebugShowNodes = wantNodes;
        language.Render.DebugColorComponents = wantColors;
        debugRendered = renderer.Render(language, glyphs, config.RootSeed);
    }

    language.Render.DebugShowNodes = wantNodes;
    language.Render.DebugColorComponents = wantColors;

    foreach (var r in rendered)
    {
        string path = Path.Combine(baseNames.Contains(r.GlyphName) ? basesDir : languageDir, SanitizeFileName(r.GlyphName) + ".png");
        File.WriteAllBytes(path, r.Png);
        Console.WriteLine($"Wrote {path}");
    }

    var sheetSettings = language.Render.ContactSheet;
    WriteContactSheet(languageDir, rendered.Where(r => !baseNames.Contains(r.GlyphName)).ToList(),
        sheetSettings.Columns, language.Render.Background);
    if (sheetSettings.BaseGlyphsSheet && baseNames.Count > 0)
    {
        WriteContactSheet(basesDir, rendered.Where(r => baseNames.Contains(r.GlyphName)).ToList(),
            sheetSettings.Columns, language.Render.Background);
    }

    if (debugRendered is not null)
    {
        foreach (var r in debugRendered)
        {
            string path = Path.Combine(baseNames.Contains(r.GlyphName) ? basesDir : languageDir, SanitizeFileName(r.GlyphName) + ".debug.png");
            File.WriteAllBytes(path, r.Png);
            Console.WriteLine($"Wrote {path}");
        }
    }
}

static void WriteContactSheet(string directory, IReadOnlyList<RenderedGlyph> items, int columns, string background)
{
    byte[] sheet = ContactSheet.Create(items, columns, background);
    if (sheet.Length == 0) return;

    string sheetPath = Path.Combine(directory, "_contact-sheet.png");
    File.WriteAllBytes(sheetPath, sheet);
    Console.WriteLine($"Wrote {sheetPath}");
}

static string SanitizeFileName(string name)
{
    foreach (char c in Path.GetInvalidFileNameChars())
        name = name.Replace(c, '_');
    return name;
}
