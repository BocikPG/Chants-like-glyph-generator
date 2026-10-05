using System.Text;
using System.Text.Json.Nodes;
using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Tests;

internal static class TestHelpers
{
    // ---- configs ---------------------------------------------------------------------------

    public static string ConfigPath(string name) => Path.Combine(AppContext.BaseDirectory, "TestConfigs", name);

    public static JsonNode LoadJson(string fileName) => JsonNode.Parse(File.ReadAllText(ConfigPath(fileName)))!;

    /// <summary>The minimal valid config, as editable JSON.</summary>
    public static JsonNode BaseJson() => LoadJson("base-config.json");

    public static JsonObject Language(JsonNode root) => root["languages"]![0]!.AsObject();

    public static RootConfig Load(JsonNode root) => RootConfigLoader.Load(root.ToJsonString());

    public static RootConfig LoadBase(Action<JsonObject>? tweak = null)
    {
        var root = BaseJson();
        tweak?.Invoke(Language(root));
        return Load(root);
    }

    /// <summary>Loads the config after editing the language; returns the error text if it is rejected.</summary>
    public static string? Rejection(Action<JsonObject> tweak)
    {
        var root = BaseJson();
        tweak(Language(root));
        try { Load(root); return null; }
        catch (ConfigValidationException ex) { return ex.Message; }
    }

    // ---- generation ------------------------------------------------------------------------

    public static RunOrchestrator Orchestrator() =>
        new(new LanguageGenerator(new GlyphGenerator(new ChainGenerator(), new PlacementResolver(), new CombinationResolver())));

    public static IReadOnlyDictionary<string, IReadOnlyList<Glyph>> Generate(RootConfig config) => Orchestrator().Run(config);

    /// <summary>One glyph straight from the glyph generator (no quality gate, no recipes).</summary>
    public static Glyph GenerateGlyph(LanguageDefinition language, string name, ulong seed)
    {
        var grid = new LatticeGrid(language.Grid.Width, language.Grid.Height);
        return new GlyphGenerator(new ChainGenerator(), new PlacementResolver(), new CombinationResolver())
            .GenerateGlyph(language, new GlyphDefinition { Name = name }, grid, seed, new Dictionary<string, Glyph>(), new GlyphUsageTracker());
    }

    // ---- building shapes by hand -----------------------------------------------------------

    public static GridPoint P(int x, int y) => new(x, y);

    public static SubSymbolChain Chain(params (int X, int Y)[] points)
    {
        var chain = new SubSymbolChain();
        foreach (var (x, y) in points) chain.Nodes.Add(new GridPoint(x, y));
        for (int i = 0; i < points.Length - 1; i++)
            chain.Edges.Add(new GridEdge(chain.Nodes[i], chain.Nodes[i + 1]));
        return chain;
    }

    public static Glyph MakeGlyph(string name, params SubSymbolChain[] chains)
    {
        var glyph = new Glyph { Name = name, LanguageName = "Test", GridWidth = 15, GridHeight = 15 };
        foreach (var chain in chains) glyph.Chains.Add(chain);
        return glyph;
    }

    public static GridEdge Edge(int x1, int y1, int x2, int y2) => new(new GridPoint(x1, y1), new GridPoint(x2, y2));

    /// <summary>The same scale -> rotate -> translate that CombinationResolver applies.</summary>
    public static GridPoint Transform(GridPoint p, int num, int den, int rotation, int dx, int dy) =>
        new GridPoint(p.X * num / den, p.Y * num / den).RotateAround(new GridPoint(0, 0), rotation).Translate(dx, dy);

    public static PlacedComponent Place(Glyph source, int num, int den, int rotation, int dx, int dy)
    {
        var placed = new PlacedComponent
        {
            SourceGlyphName = source.Name,
            Translation = new GridPoint(dx, dy),
            RotationDegrees = rotation,
            ScaleNumerator = num,
            ScaleDenominator = den,
        };

        foreach (var chain in GlyphGeometryUtils.GetAllChains(source))
        {
            var moved = new SubSymbolChain { IsClosedLoop = chain.IsClosedLoop, MotifId = chain.MotifId };
            foreach (var node in chain.Nodes) moved.Nodes.Add(Transform(node, num, den, rotation, dx, dy));
            foreach (var edge in chain.Edges)
                moved.Edges.Add(new GridEdge(Transform(edge.From, num, den, rotation, dx, dy), Transform(edge.To, num, den, rotation, dx, dy)));
            placed.TransformedChains.Add(moved);
        }

        return placed;
    }

    public static Glyph MakeComposite(string name, params PlacedComponent[] components)
    {
        var glyph = new Glyph { Name = name, LanguageName = "Test", GridWidth = 15, GridHeight = 15 };
        foreach (var component in components) glyph.Components.Add(component);
        return glyph;
    }

    // ---- measuring ---------------------------------------------------------------------------

    public static (int MinX, int MaxX, int MinY, int MaxY) BoundsOf(Glyph glyph)
    {
        var nodes = GlyphGeometryUtils.GetAllNodes(glyph).ToList();
        return (nodes.Min(n => n.X), nodes.Max(n => n.X), nodes.Min(n => n.Y), nodes.Max(n => n.Y));
    }

    public static double Distance(GridPoint a, GridPoint b) =>
        Math.Sqrt((a.X - b.X) * (double)(a.X - b.X) + (a.Y - b.Y) * (double)(a.Y - b.Y));

    // ---- snapshots ---------------------------------------------------------------------------

    /// <summary>A stable text rendering of everything that was generated.</summary>
    public static string Dump(IReadOnlyDictionary<string, IReadOnlyList<Glyph>> results)
    {
        var sb = new StringBuilder();
        foreach (var (language, glyphs) in results.OrderBy(r => r.Key, StringComparer.Ordinal))
        {
            sb.AppendLine($"language {language}");
            foreach (var glyph in glyphs)
            {
                sb.AppendLine($"  glyph {glyph.Name}");
                foreach (var chain in glyph.Chains) AppendChain(sb, "    ", chain);
                foreach (var component in glyph.Components)
                {
                    sb.AppendLine($"    component {component.SourceGlyphName} scale {component.ScaleNumerator}/{component.ScaleDenominator} " +
                                  $"rotate {component.RotationDegrees} at {component.Translation}");
                    foreach (var chain in component.TransformedChains) AppendChain(sb, "      ", chain);
                }
            }
        }
        return sb.ToString();
    }

    private static void AppendChain(StringBuilder sb, string indent, SubSymbolChain chain) =>
        sb.AppendLine($"{indent}chain{(chain.MotifId is null ? "" : " [" + chain.MotifId + "]")}{(chain.IsClosedLoop ? " (closed)" : "")}: " +
                      string.Join(" ", chain.Nodes));

    public static string SnapshotDirectory([System.Runtime.CompilerServices.CallerFilePath] string callerFile = "") =>
        Path.Combine(Path.GetDirectoryName(callerFile)!, "Snapshots");

    /// <summary>Compares text with Snapshots/&lt;name&gt;.txt. A missing snapshot is recorded (the test
    /// passes and says so); set UPDATE_SNAPSHOTS=1 to overwrite existing ones after an intended change.</summary>
    public static void MatchSnapshot(string name, string actual)
    {
        string dir = SnapshotDirectory();
        string path = Path.Combine(dir, name + ".txt");
        bool update = Environment.GetEnvironmentVariable("UPDATE_SNAPSHOTS") == "1";

        if (!File.Exists(path) || update)
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(path, actual);
            Note.Add(update && File.Exists(path) ? $"snapshot {name} updated" : $"snapshot {name} recorded");
            return;
        }

        string expected = File.ReadAllText(path);
        if (expected.ReplaceLineEndings() != actual.ReplaceLineEndings())
        {
            string[] e = expected.ReplaceLineEndings().Split('\n'), a = actual.ReplaceLineEndings().Split('\n');
            int line = Enumerable.Range(0, Math.Min(e.Length, a.Length)).FirstOrDefault(i => e[i] != a[i], Math.Min(e.Length, a.Length));
            throw new TestFailedException(
                $"Output no longer matches snapshot '{name}' (first difference at line {line + 1}).\n" +
                $"  expected: {(line < e.Length ? e[line] : "<end>")}\n  actual:   {(line < a.Length ? a[line] : "<end>")}\n" +
                "If the change is intended, re-run with UPDATE_SNAPSHOTS=1.");
        }
    }
}
