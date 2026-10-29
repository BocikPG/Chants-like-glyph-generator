using System.Text;
using System.Text.RegularExpressions;
using GlyphGen.Core.Generation;

namespace GlyphGen.Cli;

/// <summary>Colour for the console: green / red / dim, switched off when output is redirected, NO_COLOR is set or the terminal is dumb.</summary>
public static class CliStyle
{
    public static bool Enabled { get; set; }

    public static string Ok(string text) => Wrap("32", text);
    public static string Bad(string text) => Wrap("31", text);
    public static string Warn(string text) => Wrap("33", text);
    public static string Dim(string text) => Wrap("2", text);
    public static string Bold(string text) => Wrap("1", text);

    private static string Wrap(string code, string text) => Enabled ? $"\x1b[{code}m{text}\x1b[0m" : text;

    private static readonly Regex Ansi = new("\x1b\\[[0-9;]*m", RegexOptions.Compiled);

    /// <summary>The text without colour codes (for log files).</summary>
    public static string Strip(string text) => Ansi.Replace(text, "");

    /// <summary>Whether colour makes sense here: a real terminal, NO_COLOR unset, TERM not "dumb".</summary>
    public static bool Detect()
    {
        if (Console.IsOutputRedirected) return false;
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NO_COLOR"))) return false;
        return !string.Equals(Environment.GetEnvironmentVariable("TERM"), "dumb", StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>Writes to the console and, without colour codes, to a log file.</summary>
public sealed class TeeWriter : TextWriter
{
    private readonly TextWriter _console;
    private readonly TextWriter? _log;
    private readonly object _lock = new();

    public TeeWriter(TextWriter console, TextWriter? log)
    {
        _console = console;
        _log = log;
    }

    public override Encoding Encoding => _console.Encoding;

    public override void Write(char value) { lock (_lock) { _console.Write(value); _log?.Write(value); } }

    public override void Write(string? value)
    {
        if (value is null) return;
        lock (_lock) { _console.Write(value); _log?.Write(CliStyle.Strip(value)); }
    }

    public override void WriteLine(string? value)
    {
        lock (_lock) { _console.WriteLine(value); _log?.WriteLine(value is null ? "" : CliStyle.Strip(value)); }
    }

    public override void Flush() { lock (_lock) { _console.Flush(); _log?.Flush(); } }
}

/// <summary>Turns the long one-paragraph errors of the generator into a headline and indented detail lines.</summary>
public static class ErrorFormatter
{
    /// <summary>The first sentence of the message (without the "[Language] " tag), for one-line summaries.</summary>
    public static string Headline(string? message)
    {
        var parts = Split(message);
        return parts.Count == 0 ? "" : parts[0];
    }

    /// <summary>Headline on the first line, every further sentence on its own indented line.</summary>
    public static string Format(string? message, string indent = "  ")
    {
        var parts = Split(message);
        if (parts.Count == 0) return "";
        var sb = new StringBuilder(parts[0]);
        foreach (var part in parts.Skip(1)) sb.Append('\n').Append(indent).Append(part);
        return sb.ToString();
    }

    private static List<string> Split(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return new List<string>();
        string text = message.Trim();
        // Sentences end at ". " (or ".\n") followed by a capital letter, a quote or a bracket; "(s)" and "e.g." stay inside.
        var parts = Regex.Split(text, @"(?<=[a-z0-9\)'\]]\.)\s+(?=[A-Z'\(\[])")
            .Select(p => p.Trim()).Where(p => p.Length > 0).ToList();
        return parts;
    }
}

/// <summary>Merges identical warning lines into one with a count.</summary>
public static class WarningCollapser
{
    public static List<string> Collapse(IEnumerable<string> warnings)
    {
        var order = new List<string>();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var w in warnings)
        {
            if (counts.TryGetValue(w, out int n)) counts[w] = n + 1;
            else { counts[w] = 1; order.Add(w); }
        }

        return order.Select(w => counts[w] > 1 ? $"{w} (x{counts[w]})" : w).ToList();
    }
}

/// <summary>Shows what a single run is doing: how many glyphs are done, which one is being drawn and for how long. On a terminal it
/// rewrites one line; redirected to a file it prints a line every tenth glyph, and when one glyph is slow.</summary>
public sealed class ProgressReporter : IDisposable
{
    private readonly TextWriter _live;   // the console: the rewritten line goes here only
    private readonly TextWriter _log;    // permanent lines
    private readonly bool _interactive;
    private readonly bool _verbose;
    private readonly object _lock = new();
    private readonly Timer? _timer;
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private System.Diagnostics.Stopwatch _glyphClock = System.Diagnostics.Stopwatch.StartNew();
    private GenerationProgress? _last;
    private string? _note;
    private int _lastWidth;
    private double _nextSlowNote = 15;
    private string? _noteGlyph;

    public ProgressReporter(TextWriter live, TextWriter log, bool interactive, bool verbose)
    {
        _live = live;
        _log = log;
        _interactive = interactive;
        _verbose = verbose;
        if (interactive) _timer = new Timer(_ => Refresh(), null, 500, 500);
    }

    public void OnProgress(GenerationProgress p)
    {
        lock (_lock)
        {
            switch (p.Stage)
            {
                case GenerationStage.Started:
                    _last = p;
                    if (_verbose && !_interactive) Permanent($"{p.Language}: {p.Total} glyph(s) to draw");
                    break;
                case GenerationStage.GlyphStarted:
                    _last = p;
                    _note = null;
                    _glyphClock = System.Diagnostics.Stopwatch.StartNew();
                    _nextSlowNote = 15;
                    if (_verbose && !_interactive) Permanent($"[{p.Done + 1}/{p.Total}] {p.Kind} {p.Glyph}");
                    break;
                case GenerationStage.GlyphDone:
                    _last = p;
                    if (!_interactive && !_verbose && p.Done % 10 == 0) Permanent($"[{p.Done}/{p.Total}] glyphs done ({_clock.Elapsed.TotalSeconds:0} s)");
                    break;
                case GenerationStage.Retry:
                case GenerationStage.PartnerRedraw:
                    _note = p.Detail;
                    _noteGlyph = p.Glyph;
                    if (_verbose) Permanent($"    {p.Glyph}: {p.Detail}");
                    break;
            }
        }

        if (_interactive) Refresh();
        else NoteSlow();
    }

    private void NoteSlow()
    {
        lock (_lock)
        {
            if (_last?.Glyph is not { } glyph || _last.Stage != GenerationStage.GlyphStarted) return;
            if (_glyphClock.Elapsed.TotalSeconds < _nextSlowNote) return;
            _nextSlowNote += 30;
            Permanent($"  still drawing {glyph} ({_glyphClock.Elapsed.TotalSeconds:0} s)" + (_note is not null ? $": {_note}" : ""));
        }
    }

    private void Refresh()
    {
        lock (_lock)
        {
            if (_last is not { } p) return;
            string line;
            if (p.Stage == GenerationStage.Started) line = $"Starting {p.Language}: {p.Total} glyph(s) to draw";
            else
            {
                int shown = p.Stage == GenerationStage.GlyphStarted ? p.Done + 1 : p.Done;
                string what = p.Stage == GenerationStage.GlyphStarted ? $"drawing {p.Kind} '{p.Glyph}' ({_glyphClock.Elapsed.TotalSeconds:0.0} s)" : "between glyphs";
                line = $"[{Math.Min(shown, p.Total)}/{p.Total}] {what}";
                if (_note is not null && _noteGlyph == p.Glyph && p.Stage == GenerationStage.GlyphStarted) line += $" - {_note}";
            }

            int width = 100;
            try { width = Math.Max(40, Console.WindowWidth - 1); } catch (IOException) { }
            if (line.Length > width) line = line[..(width - 3)] + "...";
            _live.Write("\r" + line.PadRight(Math.Max(_lastWidth, line.Length)));
            _lastWidth = line.Length;
        }
    }

    /// <summary>Ends the live line (so what is printed next starts on a fresh line).</summary>
    public void Clear()
    {
        lock (_lock)
        {
            if (_interactive && _lastWidth > 0) { _live.Write("\r" + new string(' ', _lastWidth) + "\r"); _lastWidth = 0; }
        }
    }

    private void Permanent(string text)
    {
        Clear();
        _log.WriteLine(text);
    }

    public void Dispose()
    {
        _timer?.Dispose();
        Clear();
    }
}
