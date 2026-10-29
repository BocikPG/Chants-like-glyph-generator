// GlyphGen.Cli entry point.
// Deliberately thin: all real logic lives in GlyphGen.Core (generation) and GlyphGen.Rendering
// (PNG output), so both can be reused as referenced DLLs later without dragging along any
// console-specific code. Batch planning (configs, seeds, threads) is in BatchRunner / CliOptions.

using System.Runtime.InteropServices;
using GlyphGen.Cli;
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

var options = CliOptions.Parse(args);
EnableTerminalColours();
CliStyle.Enabled = CliStyle.Detect();

StreamWriter? logFile = null;
if (options.LogFile is { } logPath)
{
    try { logFile = new StreamWriter(logPath, append: false) { AutoFlush = true }; }
    catch (Exception ex) { Console.WriteLine($"Cannot write the log '{logPath}': {ex.Message}"); Environment.ExitCode = 1; return; }
}

var console = new TeeWriter(Console.Out, logFile);
int exitCode = Execute();
Environment.ExitCode = exitCode;
logFile?.Dispose();

if (options.Pause ?? StartedByDoubleClick(args))
{
    Console.WriteLine();
    Console.Write("Press Enter to close.");
    try { Console.ReadLine(); } catch (IOException) { }
}

return;

int Execute()
{
    if (options.Help)
    {
        console.WriteLine(CliOptions.Usage);
        return 0;
    }

    var configs = options.ResolveConfigs(Directory.GetCurrentDirectory(), out string? where);
    if (options.Errors.Count > 0)
    {
        foreach (var e in options.Errors) console.WriteLine(CliStyle.Bad(e));
        console.WriteLine();
        console.WriteLine(CliOptions.Usage);
        return 1;
    }

    if (configs.Count == 0)
    {
        if (options.Inputs.Count == 0)
            console.WriteLine(CliStyle.Bad("No config found") + " in " + string.Join(" or in ", options.Searched) + ".");
        else
            console.WriteLine(CliStyle.Bad("No config found") + " for: " + string.Join(", ", options.Inputs) + ".");
        console.WriteLine("Put a config (a .json file with a top-level \"languages\" list) in a 'configs' folder next to the program, or name one: GlyphGen.Cli my-config.json");
        console.WriteLine("All options: GlyphGen.Cli --help");
        return 1;
    }

    if (where is not null && !options.Quiet)
    {
        var names = configs.Select(Path.GetFileName).ToList();
        string list = names.Count <= 6 ? string.Join(", ", names) : string.Join(", ", names.Take(6)) + $" and {names.Count - 6} more";
        console.WriteLine($"Found {configs.Count} config(s) in {where}: {list}");
    }

    var runner = new BatchRunner(options, console, WriteOutput, Console.Out, !Console.IsOutputRedirected && !options.Verbose);
    var loaded = runner.LoadAll(configs);
    if (loaded is null) return 1;

    if (options.ValidateOnly)
    {
        console.WriteLine($"{configs.Count} config(s) are valid.");
        return 0;
    }

    var jobs = runner.Plan(configs, loaded);
    if (jobs.Count > 1 && !options.Quiet)
        console.WriteLine($"{jobs.Count} run(s): {configs.Count} config(s), {runner.ConcurrentRuns} at a time, {runner.ThreadsPerRun} thread(s) each (budget {runner.ThreadBudget}).");

    var results = runner.Run(jobs);
    runner.Summarize(results);
    return results.Any(r => !r.Ok) ? 1 : 0;
}

// Windows: let the old console understand colour codes. Elsewhere terminals already do.
static void EnableTerminalColours()
{
    if (!OperatingSystem.IsWindows() || Console.IsOutputRedirected) return;
    try
    {
        IntPtr handle = NativeMethods.GetStdHandle(-11);
        if (NativeMethods.GetConsoleMode(handle, out uint mode)) NativeMethods.SetConsoleMode(handle, mode | 0x0004);
    }
    catch (Exception) { /* colour just stays off */ }
}

// True when the program was started by a double-click: no arguments, a console window of its own (nobody else shares it), so the
// window would vanish with the messages the moment the run ends.
static bool StartedByDoubleClick(string[] args)
{
    if (args.Length > 0 || !OperatingSystem.IsWindows() || Console.IsInputRedirected || Console.IsOutputRedirected) return false;
    try { return NativeMethods.GetConsoleProcessList(new uint[4], 4) <= 1; }
    catch (Exception) { return false; }
}

static void WriteOutput(RootConfig config, IReadOnlyDictionary<string, IReadOnlyList<GlyphGen.Core.Model.Glyph>> results, bool verbose, TextWriter log)
{
    int written = 0;
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
    if (verbose) log.WriteLine($"=== {language.Name} ===");
    foreach (var glyph in glyphs)
    {
        if (verbose)
        {
            if (baseNames.Contains(glyph.Name)) log.WriteLine("(base glyph)");
            log.WriteLine(glyph.ToString());
        }
    }

    var languageDir = Path.Combine(config.OutputDirectory, SanitizeFileName(language.Name));
    Directory.CreateDirectory(languageDir);

    var basesDir = Path.Combine(languageDir, "_bases");
    if (baseNames.Count > 0) Directory.CreateDirectory(basesDir);

    // The normal images are always clean. The debug options (render.debugShowNodes /
    // debugColorComponents / debugColorParts / debugColorShapes) add a second set, "<glyph>.debug.png", drawn with the
    // enabled overlays; with all of them off no debug files are written. RenderConfig is a plain mutable class, so toggling
    // in place and restoring afterward is simplest here.
    bool wantNodes = language.Render.DebugShowNodes;
    bool wantColors = language.Render.DebugColorComponents;
    bool wantParts = language.Render.DebugColorParts;
    bool wantShapes = language.Render.DebugColorShapes;
    bool wantUndrawn = language.Render.DebugShowUndrawn;

    language.Render.DebugShowNodes = false;
    language.Render.DebugColorComponents = false;
    language.Render.DebugColorParts = false;
    language.Render.DebugColorShapes = false;
    language.Render.DebugShowUndrawn = false;
    var rendered = renderer.Render(language, glyphs, config.RootSeed, config.MaxParallelism);

    IReadOnlyList<RenderedGlyph>? debugRendered = null;
    if (wantNodes || wantColors || wantParts || wantShapes || wantUndrawn)
    {
        language.Render.DebugShowNodes = wantNodes;
        language.Render.DebugColorComponents = wantColors;
        language.Render.DebugColorParts = wantParts;
        language.Render.DebugColorShapes = wantShapes;
        language.Render.DebugShowUndrawn = wantUndrawn;
        debugRendered = renderer.Render(language, glyphs, config.RootSeed, config.MaxParallelism);
    }

    language.Render.DebugShowNodes = wantNodes;
    language.Render.DebugColorComponents = wantColors;
    language.Render.DebugColorParts = wantParts;
    language.Render.DebugColorShapes = wantShapes;
    language.Render.DebugShowUndrawn = wantUndrawn;

    foreach (var r in rendered)
    {
        string path = Path.Combine(baseNames.Contains(r.GlyphName) ? basesDir : languageDir, SanitizeFileName(r.GlyphName) + ".png");
        File.WriteAllBytes(path, r.Png);
        if (verbose) log.WriteLine($"Wrote {path}"); else written++;
    }

    var sheetSettings = config.Run.ContactSheet;
    WriteContactSheet(languageDir, rendered.Where(r => !baseNames.Contains(r.GlyphName)).ToList(),
        sheetSettings.Columns, language.Render.Background, config.MaxParallelism, verbose, log, ref written);
    if (sheetSettings.BaseGlyphsSheet && baseNames.Count > 0)
    {
        WriteContactSheet(basesDir, rendered.Where(r => baseNames.Contains(r.GlyphName)).ToList(),
            sheetSettings.Columns, language.Render.Background, config.MaxParallelism, verbose, log, ref written);
    }

    if (debugRendered is not null)
    {
        foreach (var r in debugRendered)
        {
            string path = Path.Combine(baseNames.Contains(r.GlyphName) ? basesDir : languageDir, SanitizeFileName(r.GlyphName) + ".debug.png");
            File.WriteAllBytes(path, r.Png);
            if (verbose) log.WriteLine($"Wrote {path}"); else written++;
        }
    }
}
}

static void WriteContactSheet(string directory, IReadOnlyList<RenderedGlyph> items, int columns, string background, int maxParallelism, bool verbose, TextWriter log, ref int written)
{
    byte[] sheet = ContactSheet.Create(items, columns, background, maxParallelism);
    if (sheet.Length == 0) return;

    string sheetPath = Path.Combine(directory, "_contact-sheet.png");
    File.WriteAllBytes(sheetPath, sheet);
    if (verbose) log.WriteLine($"Wrote {sheetPath}"); else written++;
}

static string SanitizeFileName(string name)
{
    foreach (char c in Path.GetInvalidFileNameChars())
        name = name.Replace(c, '_');
    return name;
}

static class NativeMethods
{
    [DllImport("kernel32.dll")] public static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")] public static extern bool GetConsoleMode(IntPtr handle, out uint mode);
    [DllImport("kernel32.dll")] public static extern bool SetConsoleMode(IntPtr handle, uint mode);
    [DllImport("kernel32.dll")] public static extern uint GetConsoleProcessList(uint[] list, uint count);
}
