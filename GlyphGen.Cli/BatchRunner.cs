using System.Diagnostics;
using System.Text;
using System.Text.Json;
using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Cli;

/// <summary>One run: a config file with one seed.</summary>
public sealed record BatchJob(int Index, string ConfigPath, string ConfigName, ulong Seed);

/// <summary>What one run came to.</summary>
public sealed record BatchResult(BatchJob Job, bool Ok, bool Skipped, double Seconds, string? Error, string? OutputDirectory)
{
    /// <summary>What the run printed (previews, file list, warnings), shown when the run finishes.</summary>
    public string? LogText { get; init; }
}

/// <summary>Writes what a finished run made (ASCII preview, images). Gets the config with its output folder and thread count already
/// set for this run; <c>verbose</c> is true when the batch is a single run, so the full preview and file list are wanted.</summary>
public delegate void BatchOutputWriter(RootConfig config, IReadOnlyDictionary<string, IReadOnlyList<Glyph>> glyphs, bool verbose, TextWriter log);

/// <summary>Plans and runs the batch: which config with which seed, how many at once and with how many threads each.</summary>
public sealed class BatchRunner
{
    private readonly CliOptions _options;
    private readonly TextWriter _console;
    private readonly BatchOutputWriter? _writer;
    private readonly object _consoleLock = new();

    public BatchRunner(CliOptions options, TextWriter console, BatchOutputWriter? writer)
    {
        _options = options;
        _console = console;
        _writer = writer;
    }

    /// <summary>The master seed random seeds were drawn from, when there were any.</summary>
    public ulong? MasterSeed { get; private set; }

    /// <summary>Runs at once, and threads inside each run, as planned by <see cref="Plan"/>.</summary>
    public int ConcurrentRuns { get; private set; }
    public int ThreadsPerRun { get; private set; }
    public int ThreadBudget { get; private set; }

    /// <summary>Loads every config (reporting every problem, not just the first). Null when any failed to load.</summary>
    public Dictionary<string, RootConfig>? LoadAll(IReadOnlyList<string> configs)
    {
        var loaded = new Dictionary<string, RootConfig>();
        bool bad = false;
        foreach (var path in configs)
        {
            try
            {
                var cfg = RootConfigLoader.LoadFromFile(path);
                loaded[path] = cfg;
                foreach (var w in cfg.Warnings) _console.WriteLine($"Warning: {Path.GetFileName(path)}: {w}");
            }
            catch (ConfigValidationException ex) { bad = true; _console.WriteLine($"{Path.GetFileName(path)}: {ex.Message}"); }
            catch (JsonException ex) { bad = true; _console.WriteLine($"{Path.GetFileName(path)}: could not read as JSON: {ex.Message}"); }
            catch (Exception ex) { bad = true; _console.WriteLine($"{Path.GetFileName(path)}: could not load: {ex.Message}"); }
        }

        return bad ? null : loaded;
    }

    /// <summary>The runs, in order: config by config, seed by seed. Seeds named or drawn are shared by every config.</summary>
    public List<BatchJob> Plan(IReadOnlyList<string> configs, IReadOnlyDictionary<string, RootConfig> loaded)
    {
        var names = ConfigNames(configs);
        var jobs = new List<BatchJob>();
        List<ulong>? shared = _options.Seeds;

        if (shared is null && _options.Runs is { } runs)
        {
            MasterSeed = _options.MasterSeed ?? RandomMaster();
            shared = DrawSeeds(MasterSeed.Value, runs);
        }

        ulong? lone = null; // one random seed for every config that sets none, so they stay comparable
        foreach (var path in configs)
        {
            List<ulong> seeds;
            if (shared is not null) seeds = shared;
            else if (CliOptions.FileHasSeed(path)) seeds = new List<ulong> { loaded[path].RootSeed };
            else
            {
                if (lone is null)
                {
                    MasterSeed = _options.MasterSeed ?? RandomMaster();
                    lone = DrawSeeds(MasterSeed.Value, 1)[0];
                }
                seeds = new List<ulong> { lone.Value };
            }

            foreach (var seed in seeds) jobs.Add(new BatchJob(jobs.Count, path, names[path], seed));
        }

        ThreadBudget = _options.Threads is > 0 ? _options.Threads.Value : Math.Max(1, Environment.ProcessorCount);
        int concurrent = _options.ParallelRuns ?? (_options.ThreadsPerRun is { } per ? Math.Max(1, ThreadBudget / per) : ThreadBudget);
        ConcurrentRuns = Math.Max(1, Math.Min(concurrent, Math.Max(1, jobs.Count)));
        ThreadsPerRun = _options.ThreadsPerRun ?? Math.Max(1, ThreadBudget / ConcurrentRuns);
        return jobs;
    }

    /// <summary>Runs every job, ConcurrentRuns at a time, and prints the summary.</summary>
    public List<BatchResult> Run(IReadOnlyList<BatchJob> jobs)
    {
        var results = new BatchResult[jobs.Count];
        int finished = 0;
        int failed = 0;
        bool verbose = jobs.Count == 1;

        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = ConcurrentRuns }, job =>
        {
            BatchResult result = _options.FailFast && Volatile.Read(ref failed) > 0
                ? new BatchResult(job, false, true, 0, null, null)
                : RunOne(job, verbose);

            results[job.Index] = result;
            if (!result.Ok && !result.Skipped) Interlocked.Increment(ref failed);
            int done = Interlocked.Increment(ref finished);
            lock (_consoleLock)
            {
                string head = $"[{done}/{jobs.Count}] {job.ConfigName} seed {job.Seed}";
                if (result.Skipped) _console.WriteLine($"{head} skipped (--fail-fast)");
                else if (verbose)
                {
                    _console.Write(result.LogText);
                    if (!result.Ok) _console.WriteLine($"Generation failed: {result.Error}");
                }
                else
                {
                    _console.WriteLine(result.Ok ? $"{head} ok ({result.Seconds:0.0} s)" : $"{head} FAILED ({result.Seconds:0.0} s): {FirstLine(result.Error)}");
                    if (!string.IsNullOrEmpty(result.LogText)) _console.Write(result.LogText);
                }
            }
        });

        return results.ToList();
    }

    private BatchResult RunOne(BatchJob job, bool verbose)
    {
        var sb = new StringWriter();
        var clock = Stopwatch.StartNew();
        string? outDir = null;
        try
        {
            var cfg = RootConfigLoader.LoadFromFile(job.ConfigPath);
            cfg.RootSeed = job.Seed;
            cfg.MaxParallelism = ThreadsPerRun;
            string root = _options.OutputDirectory ?? cfg.OutputDirectory;
            outDir = Path.Combine(root, job.ConfigName, job.Seed.ToString());
            cfg.OutputDirectory = outDir;

            // Every run has its own generator: it keeps per-run state (warnings, redraw counters).
            var orchestrator = new RunOrchestrator(new LanguageGenerator(new GlyphGenerator(new ChainGenerator(), new PlacementResolver(), new CombinationResolver())));
            var glyphs = orchestrator.Run(cfg);
            foreach (var w in orchestrator.Warnings.Skip(cfg.Warnings.Count)) sb.WriteLine($"Warning: {job.ConfigName} seed {job.Seed}: {w}");

            if (!_options.NoRender && _writer is not null)
            {
                try { _writer(cfg, glyphs, verbose, sb); }
                catch (Exception ex) { return new BatchResult(job, false, false, clock.Elapsed.TotalSeconds, "rendering failed: " + ex.Message, outDir) { LogText = sb.ToString() }; }
            }

            return new BatchResult(job, true, false, clock.Elapsed.TotalSeconds, null, outDir) { LogText = sb.ToString() };
        }
        catch (GlyphGenerationException ex)
        {
            return new BatchResult(job, false, false, clock.Elapsed.TotalSeconds, ex.Message, outDir) { LogText = sb.ToString() };
        }
        catch (Exception ex)
        {
            return new BatchResult(job, false, false, clock.Elapsed.TotalSeconds, $"{ex.GetType().Name}: {ex.Message}", outDir) { LogText = sb.ToString() };
        }
    }

    /// <summary>Prints the per-config table (only when there was more than one run) and writes --summary-json.</summary>
    public void Summarize(IReadOnlyList<BatchResult> results)
    {
        if (results.Count > 1)
        {
            _console.WriteLine();
            _console.WriteLine($"Summary - {results.Count} run(s), {ConcurrentRuns} at a time, {ThreadsPerRun} thread(s) each:");
            foreach (var group in results.GroupBy(r => r.Job.ConfigName))
            {
                var ran = group.Where(r => !r.Skipped).ToList();
                var ok = ran.Where(r => r.Ok).ToList();
                var times = ran.Select(r => r.Seconds).OrderBy(t => t).ToList();
                string time = times.Count == 0 ? "-" : $"{times[0]:0.0} / {times[times.Count / 2]:0.0} / {times[^1]:0.0} s";
                string skipped = group.Count(r => r.Skipped) is var s and > 0 ? $", {s} skipped" : "";
                _console.WriteLine($"  {group.Key}: {ok.Count}/{ran.Count} ok{skipped}, time min/median/max {time}");
                foreach (var bad in ran.Where(r => !r.Ok))
                    _console.WriteLine($"      seed {bad.Job.Seed}: {FirstLine(bad.Error)}");
            }
        }

        if (MasterSeed is { } master && (_options.Runs is not null))
            _console.WriteLine($"Master seed {master} (repeat this batch with --master-seed {master} --runs {_options.Runs}).");
        else if (MasterSeed is { } lone && results.Count > 0 && _options.Seeds is null)
            _console.WriteLine($"Random seed drawn from master seed {lone}. Repeat a run with --seed <seed>.");

        if (_options.SummaryJson is { } file)
        {
            var doc = new
            {
                masterSeed = MasterSeed,
                concurrentRuns = ConcurrentRuns,
                threadsPerRun = ThreadsPerRun,
                runs = results.Select(r => new
                {
                    config = r.Job.ConfigName, path = r.Job.ConfigPath, seed = r.Job.Seed,
                    status = r.Skipped ? "skipped" : r.Ok ? "ok" : "failed",
                    seconds = Math.Round(r.Seconds, 2), error = r.Error, output = r.OutputDirectory,
                }),
            };
            File.WriteAllText(file, JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true }));
            _console.WriteLine($"Wrote {file}");
        }
    }

    /// <summary>"my.language.json" is called "my"; two configs with the same name get -2, -3 ...</summary>
    public static Dictionary<string, string> ConfigNames(IReadOnlyList<string> configs)
    {
        var used = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var names = new Dictionary<string, string>();
        foreach (var path in configs)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            if (name.EndsWith(".language", StringComparison.OrdinalIgnoreCase)) name = name[..^".language".Length];
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            if (name.Length == 0) name = "config";
            int n = used.GetValueOrDefault(name) + 1;
            used[name] = n;
            names[path] = n == 1 ? name : $"{name}-{n}";
        }
        return names;
    }

    /// <summary>N distinct seeds from the master seed, small enough (below 2^32) to write by hand in a config.</summary>
    public static List<ulong> DrawSeeds(ulong master, int count)
    {
        var seeds = new List<ulong>();
        var seen = new HashSet<ulong>();
        for (int i = 0; seeds.Count < count; i++)
        {
            ulong s = SplitMix64.DeriveSeed(master, i) % 4294967296UL;
            if (s != 0 && seen.Add(s)) seeds.Add(s);
        }
        return seeds;
    }

    private static ulong RandomMaster() => (ulong)Random.Shared.NextInt64(1, uint.MaxValue);

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        string line = text.Split('\n')[0].Trim();
        return line.Length > 200 ? line[..200] + "..." : line;
    }
}
