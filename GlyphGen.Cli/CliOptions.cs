using System.Text.Json;

namespace GlyphGen.Cli;

/// <summary>What the command line asked for. Parsed by <see cref="CliOptions.Parse"/>; the problems found are in <see cref="Errors"/>.</summary>
public sealed class CliOptions
{
    /// <summary>Config files or folders named on the command line (globs are expanded by <see cref="ResolveConfigs"/>).</summary>
    public List<string> Inputs { get; } = new();

    /// <summary>--seed: the seeds to run, shared by every config. Null: the config's own seed, or a random one.</summary>
    public List<ulong>? Seeds { get; set; }

    /// <summary>--runs N: how many runs with random seeds.</summary>
    public int? Runs { get; set; }

    /// <summary>--master-seed: the seed the random run seeds are drawn from (printed when it was random, so a batch can be repeated).</summary>
    public ulong? MasterSeed { get; set; }

    /// <summary>--threads: the thread budget. Null or 0 = auto (every core the process may use).</summary>
    public int? Threads { get; set; }

    /// <summary>--parallel-runs: how many runs go at once. Null: as many as the budget allows.</summary>
    public int? ParallelRuns { get; set; }

    /// <summary>--threads-per-run: threads inside one run. Null: the budget divided over the parallel runs.</summary>
    public int? ThreadsPerRun { get; set; }

    /// <summary>--out: folder the output goes to. Null: each config's run.outputDirectory.</summary>
    public string? OutputDirectory { get; set; }

    /// <summary>--no-render: generate only (no images). Fast, for judging how often a config succeeds.</summary>
    public bool NoRender { get; set; }

    /// <summary>--fail-fast: no new run starts after one failed.</summary>
    public bool FailFast { get; set; }

    /// <summary>--validate-only: load and check the configs, then stop.</summary>
    public bool ValidateOnly { get; set; }

    /// <summary>--summary-json: where to write the machine-readable result.</summary>
    public string? SummaryJson { get; set; }

    /// <summary>--verbose: also the ASCII preview of every glyph, every file written, and the retries as they happen.</summary>
    public bool Verbose { get; set; }

    /// <summary>--quiet: only failures and the final line.</summary>
    public bool Quiet { get; set; }

    /// <summary>--log: also write everything printed to this file (without colour codes).</summary>
    public string? LogFile { get; set; }

    /// <summary>--pause / --no-pause: wait for Enter before closing. Null = decided by the program (only when it was started by a
    /// double-click, with no arguments, in a console window of its own).</summary>
    public bool? Pause { get; set; }

    public bool Help { get; set; }

    public List<string> Errors { get; } = new();

    public static CliOptions Parse(IReadOnlyList<string> args)
    {
        var o = new CliOptions();
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string? inline = null;
            if (a.StartsWith("--") && a.Contains('=')) { inline = a[(a.IndexOf('=') + 1)..]; a = a[..a.IndexOf('=')]; }

            string? Value()
            {
                if (inline is not null) return inline;
                if (i + 1 < args.Count) return args[++i];
                o.Errors.Add($"{a} needs a value.");
                return null;
            }

            switch (a)
            {
                case "-h": case "--help": o.Help = true; break;
                case "--seed":
                    if (Value() is { } sv)
                    {
                        var seeds = ParseSeeds(sv, out string? why);
                        if (seeds is null) o.Errors.Add($"--seed '{sv}': {why}"); else o.Seeds = seeds;
                    }
                    break;
                case "--runs": if (Value() is { } rv) o.Runs = PositiveInt(o, a, rv); break;
                case "--master-seed":
                    if (Value() is { } mv) { if (ulong.TryParse(mv, out var m)) o.MasterSeed = m; else o.Errors.Add($"--master-seed '{mv}' is not a whole number."); }
                    break;
                case "--threads":
                    if (Value() is { } tv)
                    {
                        if (tv.Equals("auto", StringComparison.OrdinalIgnoreCase)) o.Threads = 0;
                        else o.Threads = PositiveInt(o, a, tv);
                    }
                    break;
                case "--parallel-runs": if (Value() is { } pv) o.ParallelRuns = PositiveInt(o, a, pv); break;
                case "--threads-per-run": if (Value() is { } pr) o.ThreadsPerRun = PositiveInt(o, a, pr); break;
                case "--out": o.OutputDirectory = Value(); break;
                case "--summary-json": o.SummaryJson = Value(); break;
                case "--no-render": o.NoRender = true; break;
                case "--render": o.NoRender = false; break;
                case "--fail-fast": o.FailFast = true; break;
                case "--validate-only": o.ValidateOnly = true; break;
                case "--verbose": o.Verbose = true; break;
                case "--quiet": o.Quiet = true; break;
                case "--log": o.LogFile = Value(); break;
                case "--pause": o.Pause = true; break;
                case "--no-pause": o.Pause = false; break;
                default:
                    if (a.StartsWith("-") && a.Length > 1) o.Errors.Add($"Unknown option '{a}'. See --help.");
                    else o.Inputs.Add(args[i]);
                    break;
            }
        }

        if (o.Verbose && o.Quiet) o.Errors.Add("--verbose and --quiet cannot be combined.");
        if (o.Seeds is not null && o.Runs is not null) o.Errors.Add("--seed and --runs cannot be combined: --seed names the seeds, --runs draws random ones.");
        if (o.MasterSeed is not null && o.Seeds is not null) o.Errors.Add("--master-seed only matters for random seeds: leave it out when --seed is given.");
        if (o.ThreadsPerRun is not null && o.Threads is not null && o.Threads > 0 && o.ThreadsPerRun > o.Threads)
            o.Errors.Add($"--threads-per-run {o.ThreadsPerRun} is above the --threads budget {o.Threads}.");
        return o;
    }

    private static int? PositiveInt(CliOptions o, string name, string text)
    {
        if (int.TryParse(text, out int n) && n >= 1) return n;
        o.Errors.Add($"{name} '{text}' is not a whole number of at least 1.");
        return null;
    }

    /// <summary>"7", "1-20" (inclusive) or "3,7,11" (any mix of these, comma separated).</summary>
    public static List<ulong>? ParseSeeds(string text, out string? why)
    {
        why = null;
        var seeds = new List<ulong>();
        foreach (var part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int dash = part.IndexOf('-', 1 < part.Length ? 1 : 0);
            if (dash > 0)
            {
                if (!ulong.TryParse(part[..dash], out var from) || !ulong.TryParse(part[(dash + 1)..], out var to)) { why = $"'{part}' is not a range like 1-20."; return null; }
                if (to < from) { why = $"the range '{part}' ends before it starts."; return null; }
                if (to - from >= 100000) { why = $"the range '{part}' is more than 100000 seeds."; return null; }
                for (ulong s = from; s <= to; s++) seeds.Add(s);
            }
            else
            {
                if (!ulong.TryParse(part, out var s)) { why = $"'{part}' is not a whole number."; return null; }
                seeds.Add(s);
            }
        }

        if (seeds.Count == 0) { why = "no seed given."; return null; }
        var distinct = seeds.Distinct().ToList();
        return distinct;
    }

    /// <summary>The config files to run: the named files, folders (every config in them) and globs; with none named, a "configs" (or
    /// "config") folder in the current directory when there is one, else the configs in the current directory.</summary>
    public List<string> ResolveConfigs(string workingDirectory, out string? notice)
    {
        notice = null;
        Searched.Clear();
        var found = new List<string>();
        var inputs = Inputs.ToList();

        if (inputs.Count == 0)
        {
            foreach (var folder in new[] { "configs", "config" })
            {
                string dir = Path.Combine(workingDirectory, folder);
                if (Directory.Exists(dir))
                {
                    inputs.Add(dir);
                    notice = $"the '{folder}' folder";
                    Searched.Add($"the '{folder}' folder ({dir})");
                    break;
                }
            }

            if (inputs.Count == 0 || (inputs.Count == 1 && !ConfigsIn(inputs[0]).Any()))
            {
                // No config folder, or nothing valid in it: look in the current folder too.
                inputs.Clear();
                inputs.Add(workingDirectory);
                notice = "the current folder";
                Searched.Add($"the current folder ({workingDirectory})");
            }
        }

        foreach (var input in inputs)
        {
            string path = Path.IsPathRooted(input) ? input : Path.Combine(workingDirectory, input);
            if (Directory.Exists(path)) found.AddRange(ConfigsIn(path));
            else if (File.Exists(path)) found.Add(Path.GetFullPath(path));
            else if (input.Contains('*') || input.Contains('?'))
            {
                string dir = Path.GetDirectoryName(path) is { Length: > 0 } d ? d : workingDirectory;
                if (Directory.Exists(dir)) found.AddRange(Directory.GetFiles(dir, Path.GetFileName(path)).Select(Path.GetFullPath).OrderBy(f => f, StringComparer.Ordinal));
            }
            else Errors.Add($"'{input}' is not a file or folder.");
        }

        return found.Distinct().ToList();
    }

    /// <summary>Where <see cref="ResolveConfigs"/> looked when no config was named (for the "nothing found" message).</summary>
    public List<string> Searched { get; } = new();

    /// <summary>The json files of a folder that look like a GlyphGen config (a top-level "languages" list); everything else, like
    /// config.schema.json, is left alone.</summary>
    public static IEnumerable<string> ConfigsIn(string directory)
    {
        foreach (var file in Directory.GetFiles(directory, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            if (Path.GetFileName(file).EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase)) continue;
            if (LooksLikeConfig(file)) yield return Path.GetFullPath(file);
        }
    }

    private static bool LooksLikeConfig(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.EnumerateObject().Any(p => p.Name.Equals("languages", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Array);
        }
        catch (Exception) { return false; }
    }

    /// <summary>Whether the config file sets a seed itself (run.seed, or the older top-level rootSeed / seed).</summary>
    public static bool FileHasSeed(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name.Equals("rootSeed", StringComparison.OrdinalIgnoreCase) || p.Name.Equals("seed", StringComparison.OrdinalIgnoreCase)) return true;
                if (p.Name.Equals("run", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.Object
                    && p.Value.EnumerateObject().Any(q => q.Name.Equals("seed", StringComparison.OrdinalIgnoreCase))) return true;
            }
        }
        catch (Exception) { /* the loader reports the real problem */ }
        return false;
    }

    public const string Usage = """
        GlyphGen.Cli [config.json | folder | glob ...] [options]
        GlyphGen.Cli --schema [config.schema.json]

        Configs   With none named: the 'configs' (or 'config') folder if it exists, else the configs in the current folder.
                  A folder means every GlyphGen config in it.
        Seeds     (none)             the config's run.seed when it has one, else a random seed (printed)
                  --seed 7 | 1-20 | 3,7,11   run these seeds (overrides run.seed; the same seeds for every config)
                  --runs N           N runs with random seeds (shared by every config)
                  --master-seed X    draw the random seeds from X (a batch is repeated with the master seed it printed)
        Threads   --threads N|auto   thread budget (default auto = every core available)
                  --parallel-runs N  runs at once (default: as many as the budget allows)
                  --threads-per-run N  threads inside a run (default: budget / parallel runs)
        Output    --out DIR          write to DIR/<config>/<seed>/<language>/ (default: the config's run.outputDirectory)
                  --no-render        generate only, write no images
        Messages  --verbose          also the ASCII preview of every glyph, every file written and each retry
                  --quiet            only failures and the final line
                  --log FILE         also write everything printed to FILE
                  --pause | --no-pause   wait for Enter before closing (default: only when started by double-click)
        Batches   --fail-fast        start no new run after one failed
                  --validate-only    check the configs and stop
                  --summary-json F   write the result of every run to F
        """;
}
