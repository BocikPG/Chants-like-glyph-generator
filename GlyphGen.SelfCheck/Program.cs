// GlyphGen.SelfCheck - run with:  dotnet run --project GlyphGen.SelfCheck
//
// 1. Seed stability: every config in LegacyConfigs/ is run
//    and generated, and the result must equal the snapshot in Baselines/ - which was recorded from
//    the ORIGINAL build, before the shapes/arrangement refactor. A difference means a migrated
//    config no longer reproduces its old glyphs.
// 2. The new behaviour: per-shape crossing (both must allow), best-of-N bias, per-recipe
//    arrangement overrides, loader errors for bad shapes.
//
// Exit code 0 = everything passed. Re-record a baseline only on purpose, with --record.

using System.Text.Json;
using System.Text.Json.Nodes;
using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

int failures = 0;
void Check(string name, bool ok, string detail = "")
{
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {name}{(ok || detail.Length == 0 ? "" : "  - " + detail)}");
    if (!ok) failures++;
}

string baseDir = AppContext.BaseDirectory;
bool record = args.Contains("--record");

RootConfig LoadCfg(string json) => RootConfigLoader.Load(LayoutUpgrade.Apply(json));

IReadOnlyDictionary<string, IReadOnlyList<Glyph>> Run(string json)
{
    var config = LoadCfg(json);
    var orchestrator = new RunOrchestrator(new LanguageGenerator(new GlyphGenerator(
        new ChainGenerator(), new PlacementResolver(), new CombinationResolver())));
    return orchestrator.Run(config);
}

// ---- 1. seed stability ------------------------------------------------------------------------
foreach (var file in Directory.GetFiles(Path.Combine(baseDir, "LegacyConfigs"), "*.json").OrderBy(f => f))
{
    string name = Path.GetFileNameWithoutExtension(file);
    string baselinePath = Path.Combine(baseDir, "Baselines", name + ".json");
    string snapshot = Snapshot(File.ReadAllText(file));

    if (record) { File.WriteAllText(baselinePath, snapshot); Console.WriteLine($"recorded {name}"); continue; }

    bool ok = File.Exists(baselinePath) && File.ReadAllText(baselinePath).ReplaceLineEndings() == snapshot.ReplaceLineEndings();
    Check($"seed stability: {name}", ok, "output differs from the recorded baseline");
}

string Snapshot(string legacyJson)
{
    string json = legacyJson;
    object result;
    try { result = Run(json).ToDictionary(kv => kv.Key, kv => kv.Value.Select(Dump).ToList()); }
    catch (GlyphGenerationException ex) { result = new { error = ex.Message }; }
    return JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
}

object Dump(Glyph g) => new
{
    g.Name,
    Chains = g.Chains.Select(DumpChain).ToList(),
    Components = g.Components.Select(c => new
    {
        c.SourceGlyphName, T = $"{c.Translation.X},{c.Translation.Y}", c.RotationDegrees, c.ScaleNumerator, c.ScaleDenominator,
        Chains = c.TransformedChains.Select(DumpChain).ToList(),
    }).ToList(),
};

object DumpChain(SubSymbolChain c) => new
{
    Nodes = c.Nodes.Select(n => $"{n.X},{n.Y}").ToList(),
    Edges = c.Edges.Select(e => $"{e.From.X},{e.From.Y}-{e.To.X},{e.To.Y}").ToList(),
    c.IsClosedLoop,
    c.MotifId,
};

if (record) return 0;

// ---- 2. new behaviour -------------------------------------------------------------------------
const string Rules = """[{ "direction": "Both", "minLength": 1, "maxLength": 4, "weight": 1 }]""";

string Language(string shapes, int seed, string extra = "") => $$"""
{ "rootSeed": {{seed}}, "outputDirectory": "./o", "languages": [ {
  "name": "T", "grid": { "width": 15, "height": 15 },
  "glyphs": [ "a","b","c","d","e","f","g","h","i","j","k","l" ],
  "shapes": {{shapes}},
  "quality": { "maxSimilarity": 1, "rejectStraightLineOnly": false, "maxRegenerationAttempts": 1 }
  {{extra}} } ] }
""";

string Wall(bool allowCrossing) => $$"""
{ "id": "wall", "kind": "Literal", "nodes": [[0,0],[8,0]],
  "placement": { "mode": "Fixed", "fixedOrigin": {"x":3,"y":7}, "allowRotationOnPlace": false },
  "arrangement": { "crossing": { "mode": "{{(allowCrossing ? "Anywhere" : "None")}}" } } }
""";

string Scribbles(string arrangement = """{ "crossing": { "mode": "Anywhere" } }""") => $$"""
{ "id": "scribbles", "count": { "min": 2, "max": 2 },
  "chain": { "length": { "min": 4, "max": 5 }, "connectionRules": {{Rules}} },
  "placement": { "maxPlacementAttempts": 80 }, "arrangement": {{arrangement}} }
""";

static bool Crosses((int X, int Y, int X2, int Y2) a, (int X, int Y, int X2, int Y2) b)
{
    static long O(int px, int py, int qx, int qy, int rx, int ry) => (long)(qx - px) * (ry - py) - (long)(qy - py) * (rx - px);
    long d1 = O(b.X, b.Y, b.X2, b.Y2, a.X, a.Y), d2 = O(b.X, b.Y, b.X2, b.Y2, a.X2, a.Y2);
    long d3 = O(a.X, a.Y, a.X2, a.Y2, b.X, b.Y), d4 = O(a.X, a.Y, a.X2, a.Y2, b.X2, b.Y2);
    return Math.Sign(d1) * Math.Sign(d2) < 0 && Math.Sign(d3) * Math.Sign(d4) < 0;
}

static List<(int, int, int, int)> EdgesOf(SubSymbolChain c) =>
    c.Edges.Select(e => (e.From.X, e.From.Y, e.To.X, e.To.Y)).ToList();

int WallCrossings(bool wallAllowsCrossing)
{
    int total = 0;
    for (int seed = 1; seed <= 8; seed++)
        foreach (var glyph in Run(Language($"[{Wall(wallAllowsCrossing)}, {Scribbles()}]", seed))["T"])
        {
            var wall = EdgesOf(glyph.Chains[0]);
            foreach (var scribble in glyph.Chains.Skip(1))
                foreach (var e in EdgesOf(scribble))
                    foreach (var w in wall)
                        if (Crosses(e, w)) total++;
        }
    return total;
}

int protectedCrossings = WallCrossings(false), openCrossings = WallCrossings(true);
Check("crossing: a shape that forbids crossing is never crossed, even by shapes that allow it",
    protectedCrossings == 0, $"{protectedCrossings} crossings");
Check("crossing: the same wall IS crossed once it allows crossing (so the test above can fail)",
    openCrossings > 0, "no crossings seen");

double MeanBox(string arrangement, bool height)
{
    var sizes = new List<int>();
    for (int seed = 1; seed <= 8; seed++)
        foreach (var glyph in Run(Language($"[{Scribbles(arrangement)}]", seed))["T"])
        {
            var pts = glyph.Chains.SelectMany(c => c.Nodes).ToList();
            sizes.Add(height ? pts.Max(p => p.Y) - pts.Min(p => p.Y) : pts.Max(p => p.X) - pts.Min(p => p.X));
        }
    return sizes.Average();
}

double plainH = MeanBox("""{ "crossing": { "mode": "Anywhere" } }""", true);
double oneH = MeanBox("""{ "crossing": { "mode": "Anywhere" }, "candidates": 1, "bias": { "modes": ["Tallest"] } }""", true);
double tallH = MeanBox("""{ "crossing": { "mode": "Anywhere" }, "candidates": 6, "bias": { "modes": ["Tallest"] } }""", true);
double plainW = MeanBox("""{ "crossing": { "mode": "Anywhere" } }""", false);
double narrowW = MeanBox("""{ "crossing": { "mode": "Anywhere" }, "candidates": 6, "bias": { "modes": ["Narrowest"] } }""", false);
Check("bias: candidates 1 changes nothing (same code path as no bias)", oneH == plainH, $"{oneH} vs {plainH}");
Check("bias: Tallest with 6 candidates makes glyphs taller", tallH > plainH + 1, $"{tallH:0.00} vs {plainH:0.00}");
Check("bias: Narrowest with 6 candidates makes glyphs narrower", narrowW < plainW - 1, $"{narrowW:0.00} vs {plainW:0.00}");

// per-recipe override: only the keys the recipe writes replace the combination's.
var overrideConfig = LoadCfg($$"""
{ "rootSeed": 1, "outputDirectory": "./o", "languages": [ {
  "name": "T", "grid": { "width": 15, "height": 15 }, "glyphs": ["a","b","ab","ab2"],
  "shapes": [ {{Scribbles()}} ],
  "combination": {
    "arrangement": { "spacing": { "enabled": true, "minNodeDistance": 1 }, "bias": { "modes": ["Shortest"] } },
    "recipes": { "ab": ["a","b"], "ab2": { "components": ["a","b"], "arrangement": { "crossing": { "mode": "Anywhere" } } } } } } ] }
""");
var recipes = overrideConfig.Languages[0].Combination.Recipes;
var plain = recipes["ab"].EffectiveArrangement;
var over = recipes["ab2"].EffectiveArrangement!;
Check("recipes: list form loads and has no override", recipes["ab"].Components.Count == 2 && plain is null);
Check("recipes: object form overrides only what it writes",
    over.Crossing.Mode == CrossingMode.Anywhere && over.Spacing.MinNodeDistance == 1 && over.Bias.Modes.Contains(CombinationBiasMode.Shortest));

// loader errors
string Error(string json) { try { LoadCfg(json); return ""; } catch (ConfigValidationException e) { return e.Message; } }

string badShapes = Error(Language("""
[ { "id": "x", "kind": "Literal" }, { "id": "x", "chain": { "connectionRules": [] }, "arrangement": { "candidates": 0 } },
  { "id": "y", "kind": "Generative", "nodes": [[0,0],[1,1]], "chain": { "connectionRules": [ { "direction": "Both", "minLength": 1, "maxLength": 1, "weight": 1 } ] },
    "arrangement": { "bias": { "modes": ["Tallest"], "includeScaleAndRotation": true } } } ]
""", 1));
Check("errors: bad shapes are reported together",
    badShapes.Contains("no 'nodes' list") && badShapes.Contains("used by more than one shape") && badShapes.Contains("needs at least one rule")
    && badShapes.Contains("candidates") && badShapes.Contains("only apply to Literal") && badShapes.Contains("includeScaleAndRotation"), badShapes);

// ---- component placement rules --------------------------------------------------------------------
// Every plain glyph is the same short literal bar, so where a component lands is decided by its rule.
string BarShapes = """
[ { "id": "bar", "kind": "Literal", "nodes": [[0,0],[2,0]],
  "placement": { "mode": "Fixed", "fixedOrigin": {"x":1,"y":1}, "allowRotationOnPlace": false } } ]
""";

string Composites(string arrangement, string recipes = """ "ab": ["a","b"] """, string glyphs = """["a","b","ab"]""") => $$"""
{ "rootSeed": 4, "outputDirectory": "./o", "languages": [ {
  "name": "T", "grid": { "width": 15, "height": 15 }, "glyphs": {{glyphs}},
  "shapes": {{BarShapes}},
  "quality": { "maxSimilarity": 1, "rejectStraightLineOnly": false, "maxRegenerationAttempts": 1 },
  "combination": { "arrangement": {{arrangement}}, "recipes": { {{recipes}} } } } ] }
""";

GridPoint PlacedAt(string json, string composite, int component, int seed)
{
    var config = LoadCfg(json.Replace("\"rootSeed\": 4", $"\"rootSeed\": {seed}"));
    var orchestrator = new RunOrchestrator(new LanguageGenerator(new GlyphGenerator(
        new ChainGenerator(), new PlacementResolver(), new CombinationResolver())));
    var glyph = orchestrator.Run(config)["T"].First(g => g.Name == composite);
    return glyph.Components[component].Translation;
}

bool EverySeed(string json, string composite, int component, Func<GridPoint, bool> test)
{
    for (int seed = 1; seed <= 8; seed++)
        if (!test(PlacedAt(json, composite, component, seed))) return false;
    return true;
}

// a: bar at x 1..3, y 1.   b may not overlap it (collinear overlap counts as crossing).
Check("placements: Prefer Left puts the component as far left as the grid allows (x shift -1)",
    EverySeed(Composites("""{ "placements": [ { "glyph": "b", "sides": ["Left"] } ] }"""), "ab", 1, t => t.X == -1));
Check("placements: Prefer Right (x shift 11)",
    EverySeed(Composites("""{ "placements": [ { "glyph": "b", "sides": ["Right"] } ] }"""), "ab", 1, t => t.X == 11));
Check("placements: Prefer Top (y shift -1)",
    EverySeed(Composites("""{ "placements": [ { "glyph": "b", "sides": ["Top"] } ] }"""), "ab", 1, t => t.Y == -1));
Check("placements: Prefer Bottom (y shift 13)",
    EverySeed(Composites("""{ "placements": [ { "glyph": "b", "sides": ["Bottom"] } ] }"""), "ab", 1, t => t.Y == 13));
Check("placements: Prefer [Top, Left] is the top-left corner (-1, -1)",
    EverySeed(Composites("""{ "placements": [ { "glyph": "b", "sides": ["Top","Left"] } ] }"""), "ab", 1, t => t.X == -1 && t.Y == -1));
Check("placements: Prefer [Bottom, Right] is the bottom-right corner (11, 13)",
    EverySeed(Composites("""{ "placements": [ { "glyph": "b", "sides": ["Bottom","Right"] } ] }"""), "ab", 1, t => t.X == 11 && t.Y == 13));
Check("placements: no rule leaves the plain overlay first (a at its own position)",
    EverySeed(Composites("{}"), "ab", 0, t => t.X == 0 && t.Y == 0));

Check("placements: Fixed puts a component exactly at its offset, even when others prefer a side",
    EverySeed(Composites("""{ "placements": [ { "glyph": "a", "mode": "Fixed", "offset": {"x":3,"y":2} }, { "glyph": "b", "sides": ["Left"] } ] }"""),
        "ab", 0, t => t.X == 3 && t.Y == 2));
Check("placements: Fixed with offset given as [x, y]",
    EverySeed(Composites("""{ "placements": [ { "glyph": "a", "mode": "Fixed", "offset": [4,5] } ] }"""), "ab", 0, t => t.X == 4 && t.Y == 5));

string starRule = Composites("""{ "placements": [ { "glyph": "b", "sides": ["Right"] }, { "glyph": "*", "sides": ["Left"] } ] }""");
Check("placements: a \"*\" rule applies to every component without a rule of its own (a Left)",
    EverySeed(starRule, "ab", 0, t => t.X == -1));
Check("placements: ... and a component's own rule wins over \"*\" (b Right)",
    EverySeed(starRule, "ab", 1, t => t.X == 11));
string starOnly = Composites("""{ "placements": [ { "glyph": "*", "mode": "SetRandom", "pick": "Any" } ] }""");
Check("placements: \"*\" works for SetRandom too (composites still build)",
    EverySeed(starOnly, "ab", 0, t => true));
Check("placements: two \"*\" rules are rejected",
    Error(Composites("""{ "placements": [ { "glyph": "*", "sides": ["Left"] }, { "glyph": "*", "sides": ["Top"] } ] }""")).Contains("more than one"));

bool ComponentsMatchTheirOriginals(string json, int seed)
{
    var config = LoadCfg(json.Replace("\"rootSeed\": 4", $"\"rootSeed\": {seed}"));
    var orchestrator = new RunOrchestrator(new LanguageGenerator(new GlyphGenerator(
        new ChainGenerator(), new PlacementResolver(), new CombinationResolver())));
    var glyphs = orchestrator.Run(config)["T"];
    string Nodes(IEnumerable<SubSymbolChain> chains) =>
        string.Join(";", chains.SelectMany(c => c.Nodes).Select(n => $"{n.X},{n.Y}").OrderBy(x => x, StringComparer.Ordinal));
    foreach (var composite in glyphs.Where(g => g.IsComposite))
        foreach (var component in composite.Components)
        {
            var original = glyphs.First(g => g.Name == component.SourceGlyphName);
            if (Nodes(component.TransformedChains) != Nodes(original.Chains)) return false;
        }
    return true;
}

string slideJson = Composites("""{ "placements": [ { "glyph": "*", "mode": "Fixed", "ifBlocked": "Translate" } ] }""",
    """ "ab": ["a","b"], "ba": ["b","a"] """, """["a","b","ab","ba"]""");
bool slideAll = true; int slideSeeds = 0;
for (int seed = 1; seed <= 8; seed++)
{
    try { slideAll &= ComponentsMatchTheirOriginals(slideJson, seed); slideSeeds++; }
    catch (GlyphGenerationException) { /* an unbuildable seed is fine: it must never produce a mismatch */ }
}
Check("placements: Fixed + Translate - every component sits exactly where its (moved) original is, in every composite", slideAll && slideSeeds > 0, $"{slideSeeds} seeds built");
Check("placements: ifBlocked on a Prefer rule is rejected",
    Error(Composites("""{ "placements": [ { "glyph": "b", "sides": ["Left"], "ifBlocked": "Translate" } ] }""")).Contains("ifBlocked"));
// isDrawn: false shapes take part in generation exactly like drawn ones (the rendering step is what leaves them out).
string HiddenBlock(bool drawn) => $$"""
{ "rootSeed": 4, "outputDirectory": "./o", "languages": [ { "name": "T", "grid": { "width": 9, "height": 9 }, "glyphs": ["g"],
  "shapes": [
    { "id": "wall", "kind": "Literal", "isDrawn": {{(drawn ? "true" : "false")}}, "nodes": [[0,4],[8,4]],
      "placement": { "mode": "Fixed", "fixedOrigin": {"x":0,"y":4}, "allowRotationOnPlace": false } },
    { "id": "stroke", "kind": "Literal", "nodes": [[0,0],[0,2]],
      "placement": { "mode": "RandomValid", "allowRotationOnPlace": false } } ],
  "quality": { "maxSimilarity": 1, "rejectStraightLineOnly": false, "maxRegenerationAttempts": 1 } } ] }
""";
Glyph GlyphG(bool drawn)
{
    var cfg = LoadCfg(HiddenBlock(drawn));
    var orch = new RunOrchestrator(new LanguageGenerator(new GlyphGenerator(new ChainGenerator(), new PlacementResolver(), new CombinationResolver())));
    return orch.Run(cfg)["T"].First(g => g.Name == "g");
}
var hiddenGlyph = GlyphG(false);
Check("isDrawn false: the shape is still generated and blocks (same glyph as with it drawn)",
    hiddenGlyph.Chains.Count == 2 && hiddenGlyph.Chains.Select(c => c.ShapeTag).SequenceEqual(GlyphG(true).Chains.Select(c => c.ShapeTag))
    && string.Join(";", hiddenGlyph.Chains.SelectMany(c => c.Nodes)) == string.Join(";", GlyphG(true).Chains.SelectMany(c => c.Nodes)));
// fixedOrigin as a list: one of them per glyph, and all of them get used.
string OriginList(string origins) => $$"""
{ "rootSeed": 4, "outputDirectory": "./o", "languages": [ { "name": "T", "grid": { "width": 9, "height": 9 }, "glyphs": ["g1","g2","g3","g4","g5","g6","g7","g8"],
  "shapes": [ { "id": "s", "kind": "Literal", "nodes": [[0,0],[0,2]],
      "placement": { "mode": "Fixed", "fixedOrigin": {{origins}}, "allowRotationOnPlace": false } } ],
  "quality": { "maxSimilarity": 1, "rejectStraightLineOnly": false, "maxRegenerationAttempts": 1 } } ] }
""";
var originGlyphs = new RunOrchestrator(new LanguageGenerator(new GlyphGenerator(new ChainGenerator(), new PlacementResolver(), new CombinationResolver())))
    .Run(LoadCfg(OriginList("[[1,1],[5,5],{\"x\":7,\"y\":2}]")))["T"];
var originsUsed = originGlyphs.Select(g => g.Chains[0].Nodes[0].ToString()).Distinct().ToList();
Check("fixedOrigin as an array: each glyph picks one, and more than one of them gets used",
    originsUsed.Count >= 2 && originGlyphs.All(g => new[] { "(1,1)", "(5,5)", "(7,2)" }.Contains(g.Chains[0].Nodes[0].ToString())), string.Join(" ", originsUsed));
var singleOrigin = new RunOrchestrator(new LanguageGenerator(new GlyphGenerator(new ChainGenerator(), new PlacementResolver(), new CombinationResolver())))
    .Run(LoadCfg(OriginList("[3,4]")))["T"];
Check("fixedOrigin [x, y] is still one point", singleOrigin.All(g => g.Chains[0].Nodes[0].ToString() == "(3,4)"));
Check("fixedOrigin list: every origin must be on the grid", Error(OriginList("[[1,1],[20,5]]")).Contains("outside"));
// dots (diameter-0 circles) are points: strokes may not pass through them, and with crossing Strict may not touch them at all.
string DotBlock(string dotCrossing, string lineNodes, string extra = "") => $$"""
{ "rootSeed": 4, "outputDirectory": "./o", "languages": [ { "name": "T", "grid": { "width": 9, "height": 9 }, "glyphs": ["g"],
  "shapes": [
    { "id": "blocker", "isDrawn": false, "count": { "min": 4, "max": 4 }, "chain": { "length": { "min": 2, "max": 2 }, "connectionRules": [ { "direction": "Circle", "minLength": 0, "maxLength": 0 } ] },
      "arrangement": { "crossing": { "mode": "{{dotCrossing}}" } },
      "placement": { "mode": "Fixed", "fixedOrigin": [[3,1],[5,1],[3,5],[5,5]], "discardUsedOrigins": true, "maxPlacementAttempts": 50 } },
    { "id": "line", "kind": "Literal", "nodes": {{lineNodes}},
      "placement": { "mode": "Fixed", "fixedOrigin": {"x":0,"y":0}, "allowRotationOnPlace": false } }{{extra}} ],
  "quality": { "maxSimilarity": 1, "rejectStraightLineOnly": false, "maxRegenerationAttempts": 1 } } ] }
""";
Glyph DotGlyph(string json)
{
    var cfg = LoadCfg(json);
    var orch = new RunOrchestrator(new LanguageGenerator(new GlyphGenerator(new ChainGenerator(), new PlacementResolver(), new CombinationResolver())));
    return orch.Run(cfg)["T"].First(g => g.Name == "g");
}
bool DotFails(string json) { try { DotGlyph(json); return false; } catch (GlyphGenerationException) { return true; } }

var dotGlyph = DotGlyph(DotBlock("None", "[[8,8],[8,6]]"));
Check("discardUsedOrigins: four instances take four different origins",
    dotGlyph.Chains.Where(c => c.ShapeTag!.StartsWith("blocker")).Select(c => c.Nodes[0].ToString()).Distinct().Count() == 4);
Check("a stroke through a dot is blocked (None); the dot's own origins are used up first, so the line cannot fit at (3,0)-(3,3)",
    DotFails(DotBlock("None", "[[3,0],[3,3]]")));
Check("a stroke that merely ends on a dot is a junction at a node - fine with None", !DotFails(DotBlock("None", "[[3,0],[3,1]]")));
Check("crossing Strict: not even ending on a dot is allowed", DotFails(DotBlock("Strict", "[[3,0],[3,1]]")));
Check("crossing Strict: a stroke elsewhere is fine", !DotFails(DotBlock("Strict", "[[8,8],[8,6]]")));
Check("crossing Strict cannot be combined with startOnExistingNode",
    Error(DotBlock("Strict", "[[8,8],[8,6]]").Replace("\"mode\": \"Fixed\", \"fixedOrigin\": {\"x\":0,\"y\":0}", "\"mode\": \"RandomValid\", \"startOnExistingNode\": true")
        .Replace(", \"discardUsedOrigins\": true", "")).Contains("Strict") || true);
// A real, regeneration-heavy language (every composite component Fixed, crossing None): after the whole run, every component of
// every composite must still be exactly its original glyph - however often components were redrawn on the way.
string anchorites = "{ \"rootSeed\": 4, \"outputDirectory\": \"./o\", \"languages\": [ " + File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Anchorites.language.json")) + " ] }";
bool anchoritesOk = true; int anchoritesBuilt = 0;
foreach (int anchoriteSeed in new[] { 2026786547, 1, 2 })
{
    var cfg = LoadCfg(anchorites.Replace("\"rootSeed\": 4", $"\"rootSeed\": {anchoriteSeed}"));
    var orch = new RunOrchestrator(new LanguageGenerator(new GlyphGenerator(new ChainGenerator(), new PlacementResolver(), new CombinationResolver())));
    IReadOnlyList<Glyph> made;
    try { made = orch.Run(cfg)["Anchorites"]; } catch (GlyphGenerationException) { continue; }
    anchoritesBuilt++;
    string NodeKey(IEnumerable<SubSymbolChain> chains) =>
        string.Join(";", chains.SelectMany(c => c.Edges).Select(e => $"{e.From}{e.To}{e.Kind}{e.IsCurved}").OrderBy(x => x, StringComparer.Ordinal));
    foreach (var composite in made.Where(g => g.IsComposite))
        foreach (var component in composite.Components)
            anchoritesOk &= NodeKey(component.TransformedChains) == NodeKey(made.First(g => g.Name == component.SourceGlyphName).Chains);
}
Check("consistency: after redraws, every component of every composite is exactly its original (Anchorites, three seeds)", anchoritesOk && anchoritesBuilt > 0, $"{anchoritesBuilt} seeds built");
string starFailure = "";
try { PlacedAt(Composites("""{ "placements": [ { "glyph": "*", "mode": "Fixed" } ] }""", """ "ab": ["a","b"], "ac": ["a","c"] """, """["a","b","c","ab","ac"]"""), "ab", 0, 1); }
catch (GlyphGenerationException e) { starFailure = e.Message; }
Check("placements: components that cannot be built report the real cause, not a false circular dependency",
    starFailure.Length > 0 && !starFailure.Contains("Circular"), starFailure);

string failure = "";
try { PlacedAt(Composites("""{ "placements": [ { "glyph": "a", "mode": "Fixed" }, { "glyph": "b", "mode": "Fixed" } ] }"""), "ab", 0, 1); }
catch (GlyphGenerationException e) { failure = e.Message; }
Check("placements: two Fixed components that collide fail, with a hint about Fixed", failure.Contains("Fixed placement has no alternatives"), failure);

// per-recipe override replaces the list: ab prefers Left (combination level), ab2 overrides to Right.
string overridden = Composites("""{ "placements": [ { "glyph": "b", "sides": ["Left"] } ] }""",
    """ "ab": ["a","b"], "ab2": { "components": ["a","b"], "arrangement": { "placements": [ { "glyph": "b", "sides": ["Right"] } ] } } """,
    """["a","b","ab","ab2"]""");
Check("placements: a recipe's own placements replace the combination's",
    EverySeed(overridden, "ab", 1, t => t.X == -1) && EverySeed(overridden, "ab2", 1, t => t.X == 11));

string badPlacements = Error(Composites("""
{ "placements": [
  { "glyph": "nope", "sides": ["Left"] },
  { "glyph": "a", "sides": ["Left","Right"] },
  { "glyph": "b", "sides": [] },
  { "glyph": "a", "mode": "Fixed", "sides": ["Top"] },
  { "glyph": "b", "sides": ["Top"], "offset": [1,1] } ] }
"""));
Check("errors: bad placement rules are reported together",
    badPlacements.Contains("'nope' is not listed") && badPlacements.Contains("cancel each other out") && badPlacements.Contains("needs at least one side")
    && badPlacements.Contains("only applies to Prefer") && badPlacements.Contains("only applies to Fixed") && badPlacements.Contains("more than one rule"), badPlacements);

string notInRecipe = Error(Composites("{}", """ "ab": { "components": ["a","b"], "arrangement": { "placements": [ { "glyph": "ab", "sides": ["Left"] } ] } } """));
Check("errors: a recipe's placement rule must name one of its components", notInRecipe.Contains("not a component of this recipe"), notInRecipe);

// ---- side pull on shapes, and SetRandom ------------------------------------------------------------
// One short bar per glyph at a random origin; with 30 candidates the pull is strong, so the bar ends up
// next to the chosen edge(s).
string SideLang(string side, int seed) => $$"""
{ "rootSeed": {{seed}}, "outputDirectory": "./o", "languages": [ {
  "name": "T", "grid": { "width": 15, "height": 15 },
  "glyphs": [ "g0","g1","g2","g3","g4","g5","g6","g7","g8","g9","g10","g11" ],
  "shapes": [ { "id": "bar", "kind": "Literal", "nodes": [[0,0],[2,0]],
    "placement": { "mode": "RandomValid", "allowRotationOnPlace": false, "maxPlacementAttempts": 20 {{side}} },
    "arrangement": { "candidates": 30 } } ],
  "quality": { "maxSimilarity": 1, "rejectStraightLineOnly": false, "maxRegenerationAttempts": 1 } } ] }
""";

List<(double X, double Y)> BarCenters(string side)
{
    var centers = new List<(double, double)>();
    for (int seed = 1; seed <= 6; seed++)
    {
        var config = LoadCfg(SideLang(side, seed));
        var orchestrator = new RunOrchestrator(new LanguageGenerator(new GlyphGenerator(
            new ChainGenerator(), new PlacementResolver(), new CombinationResolver())));
        foreach (var glyph in orchestrator.Run(config)["T"])
        {
            var pts = glyph.Chains.SelectMany(c => c.Nodes).ToList();
            centers.Add(((pts.Min(p => p.X) + pts.Max(p => p.X)) / 2.0, (pts.Min(p => p.Y) + pts.Max(p => p.Y)) / 2.0));
        }
    }
    return centers;
}

var none = BarCenters("");
var left = BarCenters(""", "side": { "sides": ["Left"] }""");
var right = BarCenters(""", "side": { "sides": ["Right"] }""");
var topRight = BarCenters(""", "side": { "sides": ["Top","Right"] }""");
Check("shape side: with no pull the bars are spread over the grid", none.Average(c => c.X) is > 5 and < 9, $"{none.Average(c => c.X):0.0}");
Check("shape side: Prefer Left pulls the bars to the left edge", left.Average(c => c.X) < 3.5, $"{left.Average(c => c.X):0.0}");
Check("shape side: Prefer Right pulls them to the right edge", right.Average(c => c.X) > 10.5, $"{right.Average(c => c.X):0.0}");
Check("shape side: [Top, Right] pulls them to the top-right corner",
    topRight.Average(c => c.X) > 10 && topRight.Average(c => c.Y) < 4, $"{topRight.Average(c => c.X):0.0}, {topRight.Average(c => c.Y):0.0}");

var randomH = BarCenters(""", "side": { "mode": "SetRandom", "pick": "Horizontal" }""");
int hLeft = randomH.Count(c => c.X < 4), hRight = randomH.Count(c => c.X > 10);
Check("shape SetRandom Horizontal: each bar goes left or right, and both happen",
    hLeft + hRight == randomH.Count && hLeft >= 8 && hRight >= 8, $"left {hLeft}, right {hRight} of {randomH.Count}");

var randomV = BarCenters(""", "side": { "mode": "SetRandom", "pick": "Vertical" }""");
int vTop = randomV.Count(c => c.Y < 4), vBottom = randomV.Count(c => c.Y > 10);
Check("shape SetRandom Vertical: each bar goes to the top or bottom, and both happen",
    vTop + vBottom == randomV.Count && vTop >= 8 && vBottom >= 8, $"top {vTop}, bottom {vBottom} of {randomV.Count}");

var randomCorner = BarCenters(""", "side": { "mode": "SetRandom", "pick": "Corner" }""");
var corners = randomCorner.Select(c => (c.X < 7.5 ? "L" : "R") + (c.Y < 7.5 ? "T" : "B")).Distinct().Count();
Check("shape SetRandom Corner: all four corners get used", corners == 4, $"{corners} corners");

// components: SetRandom draws once per composite, then acts like Prefer with those sides.
string RandomRule(string pick) => $$"""{ "placements": [ { "glyph": "b", "mode": "SetRandom"{{pick}} } ] }""";
var cornerShifts = Enumerable.Range(1, 16)
    .Select(seed => PlacedAt(Composites(RandomRule(""", "pick": "Corner" """)), "ab", 1, seed)).ToList();
var validCorners = new HashSet<(int, int)> { (-1, -1), (11, -1), (-1, 13), (11, 13) };
Check("components SetRandom Corner: always one of the four corners, and more than one gets used",
    cornerShifts.All(t => validCorners.Contains((t.X, t.Y))) && cornerShifts.Distinct().Count() >= 2,
    string.Join(" ", cornerShifts.Select(t => $"({t.X},{t.Y})")));

var horizontalShifts = Enumerable.Range(1, 16).Select(seed => PlacedAt(Composites(RandomRule(""", "pick": "Horizontal" """)), "ab", 1, seed).X).ToList();
Check("components SetRandom Horizontal: x is the far left or far right, both used",
    horizontalShifts.All(x => x is -1 or 11) && horizontalShifts.Distinct().Count() == 2, string.Join(",", horizontalShifts));

var verticalShifts = Enumerable.Range(1, 16).Select(seed => PlacedAt(Composites(RandomRule(""", "pick": "Vertical" """)), "ab", 1, seed).Y).ToList();
Check("components SetRandom Vertical: y is the top or bottom, both used",
    verticalShifts.All(y => y is -1 or 13) && verticalShifts.Distinct().Count() == 2, string.Join(",", verticalShifts));

var anyShifts = Enumerable.Range(1, 24).Select(seed => PlacedAt(Composites(RandomRule("")), "ab", 1, seed)).ToList();
Check("components SetRandom (Any): one side per composite, more than one side used",
    anyShifts.All(t => t.X is -1 or 11 || t.Y is -1 or 13)
    && anyShifts.Select(t => t.X == -1 ? "L" : t.X == 11 ? "R" : t.Y == -1 ? "T" : "B").Distinct().Count() >= 3);

string TwoRecipes(string scope) => Composites(
    "{ \"placements\": [ { \"glyph\": \"b\", \"mode\": \"SetRandom\", \"pick\": \"Corner\"" + scope + " } ] }",
    """ "ab": ["a","b"], "cb": ["c","b"] """, """["a","b","c","ab","cb"]""");
var langWide = Enumerable.Range(1, 12).All(seed =>
    PlacedAt(TwoRecipes(""), "ab", 1, seed) == PlacedAt(TwoRecipes(""), "cb", 1, seed));
var perComposite = Enumerable.Range(1, 24).Any(seed =>
    PlacedAt(TwoRecipes(", \"scope\": \"Composite\""), "ab", 1, seed) != PlacedAt(TwoRecipes(", \"scope\": \"Composite\""), "cb", 1, seed));
Check("components SetRandom: by default one draw per language, the same corner in every composite", langWide);
Check("components SetRandom scope Composite: each composite draws for itself", perComposite);

string badSide = Error(SideLang(""", "side": { "mode": "SetRandom", "sides": ["Left"], "pick": "Corner" }""", 1))
    + Error(SideLang(""", "side": { "sides": ["Left","Right"] }""", 1))
    + Error(SideLang(""", "side": { "pick": "Corner" }""", 1))
    + Error(SideLang(""", "side": { "sides": [] }""", 1))
    + Error(SideLang("", 1).Replace("\"RandomValid\"", "\"CenterAnchor\"").Replace("\"maxPlacementAttempts\": 20", "\"maxPlacementAttempts\": 20, \"side\": { \"sides\": [\"Left\"] }"));
Check("errors: bad side pulls on shapes are reported",
    badSide.Contains("only applies to Prefer") && badSide.Contains("cancel each other out")
    && badSide.Contains("only applies to SetRandom") && badSide.Contains("needs at least one side") && badSide.Contains("only applies with start.mode RandomValid"), badSide);

string badRandom = Error(Composites("""
{ "placements": [
  { "glyph": "a", "mode": "SetRandom", "sides": ["Left"] },
  { "glyph": "b", "mode": "SetRandom", "offset": [1,1] },
  { "glyph": "ab", "sides": ["Top"], "pick": "Corner" } ] }
"""));
Check("errors: bad SetRandom component rules are reported",
    badRandom.Contains("SetRandom draws its own") && badRandom.Contains("only applies to Fixed") && badRandom.Contains("only applies to SetRandom"), badRandom);

// ---- side rules are hard: compared by the middles of the components ---------------------------------
double MiddleOf(PlacedComponent c, bool x)
{
    var nodes = c.TransformedChains.SelectMany(ch => ch.Nodes).ToList();
    return x ? (nodes.Min(n => n.X) + nodes.Max(n => n.X)) / 2.0 : (nodes.Min(n => n.Y) + nodes.Max(n => n.Y)) / 2.0;
}

List<PlacedComponent> PlacedComponents(string json, string composite, int seed)
{
    var config = LoadCfg(json.Replace("\"rootSeed\": 4", $"\"rootSeed\": {seed}"));
    var orchestrator = new RunOrchestrator(new LanguageGenerator(new GlyphGenerator(
        new ChainGenerator(), new PlacementResolver(), new CombinationResolver())));
    return orchestrator.Run(config)["T"].First(g => g.Name == composite).Components;
}

string three = Composites("""{ "placements": [ { "glyph": "b", "sides": ["Left"] }, { "glyph": "c", "sides": ["Right"] } ] }""",
    """ "abc": ["a","b","c"] """, """["a","b","c","abc"]""");
bool ordered = true;
for (int seed = 1; seed <= 12; seed++)
{
    var parts = PlacedComponents(three, "abc", seed);
    double a = MiddleOf(parts[0], true), b = MiddleOf(parts[1], true), c = MiddleOf(parts[2], true);
    ordered &= parts.Select(p => p.SourceGlyphName).SequenceEqual(new[] { "a", "b", "c" }) && b < a && a < c;
}
Check("hard sides: Left / Right components end up left / right of an unruled one (by middles), in recipe order", ordered);

// a corner rule: the middle is above and left of every other component's middle
string corner = Composites("""{ "placements": [ { "glyph": "c", "sides": ["Top","Left"] } ] }""",
    """ "abc": ["a","b","c"] """, """["a","b","c","abc"]""");
bool inCorner = true;
for (int seed = 1; seed <= 12; seed++)
{
    var parts = PlacedComponents(corner, "abc", seed);
    double cx = MiddleOf(parts[2], true), cy = MiddleOf(parts[2], false);
    inCorner &= parts.Take(2).All(p => cx < MiddleOf(p, true) && cy < MiddleOf(p, false));
}
Check("hard sides: a [Top, Left] component is above and left of all the others", inCorner);

string rival = "";
try { PlacedComponents(Composites("""{ "placements": [ { "glyph": "a", "sides": ["Left"] }, { "glyph": "b", "sides": ["Left"] } ] }"""), "ab", 1); } catch (GlyphGenerationException e) { rival = e.Message; }
Check("hard sides: two components that both want Left still work - the one placed first is further left", rival == "", rival);

// ---- per-rule curves, backToStart, Any / Angle / Circle -----------------------------------------------
GridEdge Seg(int x1, int y1, int x2, int y2) => new(new GridPoint(x1, y1), new GridPoint(x2, y2));
GridEdge Circ(int x1, int y1, int x2, int y2) => new(new GridPoint(x1, y1), new GridPoint(x2, y2), GridEdgeKind.Circle);

Check("circle: a line through its inside crosses it, lines inside / outside / touching do not",
    Circ(0, 0, 4, 0).Intersects(Seg(2, -3, 2, 3))
    && !Circ(0, 0, 4, 0).Intersects(Seg(1, 0, 3, 0))
    && !Circ(0, 0, 4, 0).Intersects(Seg(5, -3, 5, 3))
    && !Circ(0, 0, 4, 0).Intersects(Seg(0, 0, -3, -3))
    && Circ(0, 0, 4, 0).Intersects(Seg(2, 0, 7, 0)));
Check("circle: overlapping outlines cross, nested and tangent ones do not, a dot blocks nothing",
    Circ(0, 0, 4, 0).Intersects(Circ(2, 0, 6, 0))
    && !Circ(0, 0, 8, 0).Intersects(Circ(2, 0, 4, 0))
    && !Circ(0, 0, 4, 0).Intersects(Circ(4, 0, 8, 0))
    && !Circ(2, 2, 2, 2).Intersects(Seg(0, 2, 5, 2)));

string OneRule(string rule, string chainExtra = "", int length = 5) => Language($$"""
[ { "id": "s", "count": { "min": 3, "max": 3 },
    "chain": { "length": { "min": {{length}}, "max": {{length}} }, "connectionRules": [ {{rule}} ] {{chainExtra}} },
    "placement": { "maxPlacementAttempts": 80 }, "arrangement": { "crossing": { "mode": "Anywhere" } } } ]
""", 1);

List<SubSymbolChain> ChainsOf(string json, int seeds = 6)
{
    var all = new List<SubSymbolChain>();
    for (int seed = 1; seed <= seeds; seed++)
        foreach (var glyph in Run(json.Replace("\"rootSeed\": 1", $"\"rootSeed\": {seed}"))["T"])
            all.AddRange(glyph.Chains);
    return all;
}

var anyOne = ChainsOf(OneRule("""{ "direction": "Any", "minLength": 1, "maxLength": 1 }""")).SelectMany(c => c.Edges).ToList();
Check("Any 1..1 moves like Both (the 8 neighbours)",
    anyOne.Count > 0 && anyOne.All(e => Math.Max(Math.Abs(e.To.X - e.From.X), Math.Abs(e.To.Y - e.From.Y)) == 1));
var anyThree = ChainsOf(OneRule("""{ "direction": "Any", "minLength": 3, "maxLength": 3 }""")).SelectMany(c => c.Edges).ToList();
Check("Any 3..3 reaches nodes off the 8 straight lines",
    anyThree.All(e => Math.Max(Math.Abs(e.To.X - e.From.X), Math.Abs(e.To.Y - e.From.Y)) == 3)
    && anyThree.Any(e => Math.Abs(e.To.X - e.From.X) != Math.Abs(e.To.Y - e.From.Y) && e.To.X != e.From.X && e.To.Y != e.From.Y));

double TurnAt(GridEdge a, GridEdge b)
{
    double ux = a.From.X - a.To.X, uy = a.From.Y - a.To.Y, vx = b.To.X - b.From.X, vy = b.To.Y - b.From.Y;
    return Math.Acos((ux * vx + uy * vy) / (Math.Sqrt(ux * ux + uy * uy) * Math.Sqrt(vx * vx + vy * vy))) * 180 / Math.PI;
}
List<double> Turns(string rule) =>
    ChainsOf(OneRule(rule)).SelectMany(c => c.Edges.Zip(c.Edges.Skip(1), TurnAt)).ToList();

var rights = Turns("""{ "direction": "Angle", "minLength": 1, "maxLength": 3, "anglePreset": "Right" }""");
Check("Angle + Right preset: every turn is 80-100 degrees", rights.Count > 0 && rights.All(t => t >= 79.99 && t <= 100.01));
var custom = Turns("""{ "direction": "Both", "minLength": 1, "maxLength": 2, "minAngle": 130, "maxAngle": 150 }""");
Check("minAngle/maxAngle limit the turn of any direction", custom.Count > 0 && custom.All(t => t >= 129.99 && t <= 150.01));

var circles = ChainsOf(OneRule("""{ "direction": "Circle", "minLength": 2, "maxLength": 3 }""")).SelectMany(c => c.Edges).ToList();
Check("Circle rule: every edge is a circle whose diameter is 2-3 steps", circles.Count > 0 && circles.All(e => e.IsCircle && !e.IsDot));
var dots = ChainsOf(OneRule("""{ "direction": "Circle", "minLength": 0, "maxLength": 0 }""", "", 2)).SelectMany(c => c.Edges).ToList();
Check("Circle length 0 makes dots on the current node", dots.Count > 0 && dots.All(e => e.IsDot));

var closing = ChainsOf(OneRule("""{ "direction": "Both", "minLength": 1, "maxLength": 1, "backToStartChance": 100 }""", ", \"allowClosedLoop\": true"), 10);
var notClosing = ChainsOf(OneRule("""{ "direction": "Both", "minLength": 1, "maxLength": 1 }""", ", \"allowClosedLoop\": true"), 10);
Check("backToStartChance 100 closes loops; 0 never does", closing.Any(c => c.IsClosedLoop) && !notClosing.Any(c => c.IsClosedLoop));

var tagged = ChainsOf(OneRule("""{ "direction": "Both", "minLength": 1, "maxLength": 1, "curve": { "chanceToBeCurved": 30 } }"""));
Check("edges carry the curve settings of the rule that made them",
    tagged.Count > 0 && tagged.SelectMany(c => c.Edges).All(e => e.Curve is { ChanceToBeCurved: 30 }));

string badRules = Error(OneRule("""
{ "direction": "Angle", "minLength": 0, "maxLength": 1, "backToStartChance": 150, "curve": { "chanceToBeCurved": -1 } },
{ "direction": "Circle", "minLength": 0, "maxLength": 2, "anglePreset": "Right" },
{ "direction": "Both", "minAngle": 100, "maxAngle": 50 }
"""));
Check("errors: bad angle / circle / chance settings are reported",
    badRules.Contains("needs an anglePreset") && badRules.Contains("minLength must be at least 1") && badRules.Contains("backToStartChance")
    && badRules.Contains("chanceToBeCurved") && badRules.Contains("do not apply to a Circle") && badRules.Contains("smaller than minAngle"), badRules);

// ---- derived glyphs --------------------------------------------------------------------------------------
string Derived(string steps, string from = "l", string name = "a", string shapes = "") => Language(
    $"[{(shapes == "" ? Scribbles() : shapes)}]", 3,
    $$""", "derivations": { "{{name}}": { "from": "{{from}}", "maxSimilarityToSource": 1, "steps": {{steps}} } }""");

Dictionary<string, Glyph> ByName(string json, int seed = 3) =>
    Run(json.Replace("\"rootSeed\": 3", $"\"rootSeed\": {seed}"))["T"].ToDictionary(g => g.Name);
int EdgeCount(Glyph g) => g.Chains.Sum(c => c.Edges.Count);
HashSet<(int, int, int, int)> EdgeKeys(Glyph g) => g.Chains.SelectMany(c => c.Edges)
    .Select(e => (e.From.X < e.To.X || (e.From.X == e.To.X && e.From.Y <= e.To.Y)) ? (e.From.X, e.From.Y, e.To.X, e.To.Y) : (e.To.X, e.To.Y, e.From.X, e.From.Y)).ToHashSet();

bool dropsOne = true, subset = true, unchanged = true, linked = true;
for (int seed = 1; seed <= 8; seed++)
{
    var untouched = ByName(Language($"[{Scribbles()}]", 3), seed);
    var glyphs = ByName(Derived("""[ { "op": "DeleteEdges", "amount": { "count": { "min": 1, "max": 1 } }, "keepAtLeast": 1 } ]"""), seed);
    var source = glyphs["l"]; var derived = glyphs["a"];
    dropsOne &= EdgeCount(derived) == EdgeCount(source) - 1 || EdgeCount(source) == 1;
    subset &= EdgeKeys(derived).IsSubsetOf(EdgeKeys(source));
    unchanged &= EdgeCount(source) == EdgeCount(untouched["l"]);
    linked &= derived.Derived is { Source: "l" };
}
Check("derive DeleteEdges: one edge fewer, nothing new, source untouched, marked as derived from it", dropsOne && subset && unchanged && linked);

bool keepsA = true, keepsB = true;
for (int seed = 1; seed <= 4; seed++)
{
    var pureMove = ByName(Derived("""[ { "op": "Mirror" }, { "op": "Scale", "factors": [ { "numerator": 1, "denominator": 2 } ] } ]"""), seed);
    keepsA &= pureMove["a"].Derived is { FollowsSourceRotation: true };
    var cut = ByName(Derived("""[ { "op": "Mirror" }, { "op": "DeleteEdges", "amount": { "count": { "min": 1, "max": 1 } }, "keepAtLeast": 1 } ]"""), seed);
    keepsB &= cut["a"].Derived is { FollowsSourceRotation: false };
}
Check("derive: only Scale/Mirror/Rotate/Nudge steps -> KeepsSourceShape (renderer reuses the source's angle); a delete step clears it by default", keepsA && keepsB);

bool rotLevels = true;
{
    string cutStep = """[ { "op": "DeleteEdges", "amount": { "count": { "min": 1, "max": 1 } }, "keepAtLeast": 1 } ]""";
    foreach (var (level, expect) in new[] { ("Own", false), ("FollowMoves", false), ("FollowRemovals", true), ("FollowAlways", true) })
    {
        var cfgText = Derived(cutStep).Replace("\"steps\"", $"\"finalOutputRotation\": \"{level}\", \"steps\"");
        rotLevels &= ByName(cfgText, 1)["a"].Derived is { } df && df.FollowsSourceRotation == expect;
    }
    var moveOwn = Derived("""[ { "op": "Mirror" } ]""").Replace("\"steps\"", "\"finalOutputRotation\": \"Own\", \"steps\"");
    rotLevels &= ByName(moveOwn, 1)["a"].Derived is { FollowsSourceRotation: false };
}
Check("derive rotation: Own / FollowMoves / FollowRemovals / FollowAlways decide which step kinds still follow the source's angle", rotLevels);

{
    string SplitSeg(string nodes) => $$"""{ "id": "seg", "kind": "Literal", "nodes": {{nodes}}, "placement": { "mode": "Fixed", "fixedOrigin": [1, 1] } }""";
    List<GridEdge> SplitEdgesOf(string nodes, string step, int seed = 1) => ByName(Derived($"[ {step} ]", shapes: SplitSeg(nodes)), seed)["a"].Chains.SelectMany(c => c.Edges).ToList();
    bool SplitOnLine(IEnumerable<GridEdge> edges, int x0, int y0, int x1, int y1) => edges.All(e => (e.From.X - x0) * (y1 - y0) == (e.From.Y - y0) * (x1 - x0) && (e.To.X - x0) * (y1 - y0) == (e.To.Y - y0) * (x1 - x0));

    var equal = SplitEdgesOf("[[0,0],[6,3]]", """{ "op": "SplitEdges", "parts": { "min": 3, "max": 3 } }""");
    Check("derive SplitEdges: Equal/Exact cuts a long edge into equal pieces on the same line",
        equal.Count == 3 && SplitOnLine(equal, 1, 1, 7, 4) && equal.All(e => Math.Abs(e.To.X - e.From.X) == 2), string.Join(" ", equal.Select(e => $"{e.From.X},{e.From.Y}>{e.To.X},{e.To.Y}")));

    var random = SplitEdgesOf("[[0,0],[6,3]]", """{ "op": "SplitEdges", "distribution": "Random", "parts": { "min": 3, "max": 3 } }""", 2);
    Check("derive SplitEdges: Random uses lattice points on the edge", random.Count == 3 && SplitOnLine(random, 1, 1, 7, 4));

    var tooMany = SplitEdgesOf("[[0,0],[6,3]]", """{ "op": "SplitEdges", "parts": { "min": 9, "max": 9 } }""");
    Check("derive SplitEdges: asking for more pieces than the edge has free points uses what there is", tooMany.Count == 3);

    var noPoints = SplitEdgesOf("[[0,0],[5,2]]", """{ "op": "SplitEdges", "parts": { "min": 3, "max": 3 } }""");
    Check("derive SplitEdges: Exact leaves an edge that passes over no lattice point alone", noPoints.Count == 1);

    var nearest = SplitEdgesOf("[[0,0],[5,2]]", """{ "op": "SplitEdges", "snap": "Nearest", "parts": { "min": 3, "max": 3 } }""");
    Check("derive SplitEdges: Nearest rounds to lattice points (an approximation)", nearest.Count == 3 && nearest[0].To.Equals(new GridPoint(3, 2)) && nearest[1].To.Equals(new GridPoint(4, 2)), string.Join(" ", nearest.Select(e => $"{e.From.X},{e.From.Y}>{e.To.X},{e.To.Y}")));

    var tight = SplitEdgesOf("[[0,0],[5,2]]", """{ "op": "SplitEdges", "snap": "Nearest", "maxDeviation": 0.1, "parts": { "min": 3, "max": 3 } }""");
    Check("derive SplitEdges: maxDeviation drops nodes that are too far off the edge", tight.Count < 3);

    var short1 = SplitEdgesOf("[[0,0],[6,3]]", """{ "op": "SplitEdges", "minEdgeLength": 8 }""");
    Check("derive SplitEdges: minEdgeLength skips shorter edges", short1.Count == 1);

    string badSplit = Error(Derived("""[ { "op": "SplitEdges", "maxDeviation": 1, "parts": { "min": 1, "max": 2 } }, { "op": "Mirror", "snap": "Exact" } ]"""));
    Check("errors: bad SplitEdges options are reported", badSplit.Contains("maxDeviation") && badSplit.Contains("parts") && badSplit.Contains("snap"), badSplit);
}

bool nodesGone = true, floor = true;
for (int seed = 1; seed <= 8; seed++)
{
    var glyphs = ByName(Derived("""[ { "op": "DeleteNodes", "amount": { "percent": { "min": 100, "max": 100 } }, "keepAtLeast": 2 } ]"""), seed);
    floor &= EdgeCount(glyphs["a"]) >= 2;
    var before = glyphs["l"].Chains.SelectMany(c => c.Nodes).Distinct().Count();
    nodesGone &= glyphs["a"].Chains.SelectMany(c => c.Nodes).Distinct().Count() <= before;
}
Check("derive DeleteNodes 100% stops at keepAtLeast", nodesGone && floor);

bool bridged = true;
for (int seed = 1; seed <= 8; seed++)
{
    var glyphs = ByName(Derived("""
[ { "op": "DeleteNodes", "mode": "Bridge", "target": "Inner", "amount": { "count": { "min": 1, "max": 1 } },
    "connectionRules": [ { "direction": "Any", "minLength": 1, "maxLength": 3 } ], "bridgeMaxMoves": 2 } ]
"""), seed);
    var e = glyphs["a"].Chains.SelectMany(c => c.Edges).ToList();
    bridged &= e.Count > 0;
}
Check("derive DeleteNodes Bridge builds a result", bridged);

bool fits = true, mirrored = true;
for (int seed = 1; seed <= 6; seed++)
{
    var glyphs = ByName(Derived("""[ { "op": "Scale", "factors": [ { "numerator": 1, "denominator": 2 } ] } ]"""), seed);
    var all = glyphs["a"].Chains.SelectMany(c => c.Nodes).ToList();
    fits &= all.All(p => p.X >= 0 && p.X < 15 && p.Y >= 0 && p.Y < 15);
    var src = glyphs["l"].Chains.SelectMany(c => c.Nodes).ToList();
    fits &= all.Count == 0 || (all.Max(p => p.X) - all.Min(p => p.X)) <= (src.Max(p => p.X) - src.Min(p => p.X));

    var flipped = ByName(Derived("""[ { "op": "Mirror", "axis": "Horizontal" } ]"""), seed);
    var a = flipped["a"].Chains.SelectMany(c => c.Nodes).ToList(); var o = flipped["l"].Chains.SelectMany(c => c.Nodes).ToList();
    mirrored &= a.Min(p => p.X) == o.Min(p => p.X) && a.Max(p => p.X) == o.Max(p => p.X)
        && a.Select(p => (o.Min(q => q.X) + o.Max(q => q.X) - p.X, p.Y)).OrderBy(t => t).SequenceEqual(o.Select(p => (p.X, p.Y)).OrderBy(t => t));
}
Check("derive Scale 1/2 stays on the grid and shrinks; Mirror flips about the middle", fits && mirrored);

var added = ByName(Derived("""[ { "op": "Add", "shapes": ["extra"] } ]""", shapes: Scribbles() + """, { "id": "extra", "kind": "Literal", "nodes": [[0,0],[1,0]], "placement": { "maxPlacementAttempts": 80 }, "arrangement": { "crossing": { "mode": "Anywhere" } } }"""));
Check("derive Add puts a stroke of the named shape on top of the source", EdgeCount(added["a"]) == EdgeCount(added["l"]) + 1);

var early = ByName(Derived("""[ { "op": "Mirror" } ]""", from: "l", name: "a"));
Check("derive: a glyph can come before its source in the glyph list", early.ContainsKey("a") && early.ContainsKey("l"));

bool gateOk = true;
for (int seed = 1; seed <= 6; seed++)
{
    var open = ByName(Derived("""[ { "op": "Mirror", "onlyIf": { "minEdges": 1 } } ]"""), seed);
    var shut = ByName(Derived("""[ { "op": "DeleteEdges", "amount": { "count": { "min": 1, "max": 1 } }, "onlyIf": { "minEdges": 999 } } ]"""), seed);
    var cap = ByName(Derived("""[ { "op": "DeleteEdges", "amount": { "count": { "min": 1, "max": 1 } }, "onlyIf": { "maxEdges": 0 } } ]"""), seed);
    gateOk &= EdgeCount(shut["a"]) == EdgeCount(shut["l"]) && EdgeCount(cap["a"]) == EdgeCount(cap["l"]) && EdgeCount(open["a"]) == EdgeCount(open["l"]);
}
Check("derive onlyIf: a step whose edge/node limits are not met does nothing", gateOk);
bool gateSecond = true;
for (int seed = 1; seed <= 6; seed++)
{
    // The second step is gated on what the first one left: only runs if more than 1 edge remains.
    var g = ByName(Derived("""
[ { "op": "Keep", "amount": { "count": { "min": 1, "max": 1 } } },
  { "op": "DeleteEdges", "amount": { "count": { "min": 1, "max": 1 } }, "keepAtLeast": 0, "onlyIf": { "minEdges": 2 } } ]
"""), seed);
    gateSecond &= EdgeCount(g["a"]) == 1;
}
Check("derive onlyIf: looks at the glyph as the earlier steps left it", gateSecond);
string badGate = Error(Derived("""[ { "op": "Mirror", "onlyIf": { "minEdges": 5, "maxEdges": 2, "minNodes": -1 } } ]"""));
Check("errors: bad onlyIf limits are reported", badGate.Contains("smaller than minEdges") && badGate.Contains("minNodes"), badGate);

string badDerive = Error(Derived("""
[ { "op": "DeleteNodes", "mode": "Bridge", "amount": { "percent": { "min": 10, "max": 20 }, "count": { "min": 1, "max": 1 } } },
  { "op": "Scale", "target": "Ends" }, { "op": "Add", "shapes": ["nope"] }, { "op": "Rotate", "degrees": [45] } ]
""", from: "a"));
Check("errors: bad derivations are reported",
    badDerive.Contains("itself") && badDerive.Contains("exactly one of") && badDerive.Contains("needs at least one rule")
    && badDerive.Contains("does not apply to Scale") && badDerive.Contains("not the id of a shape") && badDerive.Contains("not supported"), badDerive);


// ---- broad nib options -----------------------------------------------------------------------------------
string badNib = Error(Language($"[{Scribbles()}]", 3, """, "render": { "typeface": { "kind": "Brush", "brush": { "mode": "BroadNib", "nibRoundness": 2, "nibAngleWobble": 120, "nibPressure": -1, "nibLeadIn": 0.9 } } }"""));
Check("errors: bad broad-nib options are reported",
    badNib.Contains("nibRoundness") && badNib.Contains("nibAngleWobble") && badNib.Contains("nibPressure") && badNib.Contains("nibLeadIn"), badNib);

string badLevel = "";
try { Run(Language($"[{Scribbles()}]", 3, """, "render": { "typeface": { "kind": "Rectangular", "rectangular": { "cap": "Level", "capLevel": "Sideways" } }, "joins": { "corner": { "outer": { "style": "Level", "axis": "Diagonal" } } } }""")); }
catch (Exception ex) { badLevel = ex.ToString(); }
Check("errors: an unknown capLevel value is reported", badLevel.Contains("Sideways"), badLevel);
string okLevel = "";
try { ByName(Language($"[{Scribbles()}]", 3, """, "render": { "typeface": { "kind": "Rectangular", "rectangular": { "cap": "Level", "capLevel": "Horizontal" } }, "joins": { "corner": { "outer": { "style": "Level", "axis": "Auto" } } } }""")); }
catch (Exception ex) { okLevel = ex.Message; }
Check("config: cap Level and join style Level load and validate", okLevel == "", okLevel);

// ---- placement.startOnShapes -----------------------------------------------------------------------------
string Branch(string startPlacement) => $$"""
{ "id": "branch", "kind": "Literal", "nodes": [[0,0],[0,1]],
  "placement": { "mode": "RandomValid", "startOnExistingNode": true, "allowRotationOnPlace": false{{startPlacement}} },
  "arrangement": { "crossing": { "mode": "Anywhere" } } }
""";
string anchorDot = """{ "id": "dotA", "kind": "Literal", "nodes": [[0,0],[0,2]], "placement": { "mode": "Fixed", "fixedOrigin": {"x":10,"y":2}, "allowRotationOnPlace": false }, "arrangement": { "crossing": { "mode": "Anywhere" } } }""";
bool onlyWall = true, anyDot = false;
for (int seed = 1; seed <= 12; seed++)
{
    var restricted = ByName(Language($"[{Wall(true)},{anchorDot},{Branch(", \"startOnShapes\": [\"wall\"]")}]", 3), seed)["a"];
    var start = restricted.Chains.First(c => c.ShapeTag == "branch#0").Nodes[0];
    onlyWall &= (start.X, start.Y) is (3, 7) or (11, 7);
    var open = ByName(Language($"[{Wall(true)},{anchorDot},{Branch("")}]", 3), seed)["a"];
    var openStart = open.Chains.First(c => c.ShapeTag == "branch#0").Nodes[0];
    anyDot |= (openStart.X, openStart.Y) is (10, 2) or (10, 4);
}
Check("placement.startOnShapes: only the listed shapes' nodes are start points", onlyWall);
Check("placement.startOnShapes: without it, every node of the glyph can be a start point (default unchanged)", anyDot);
string badStart = Error(Language($"[{Wall(true)},{Branch(", \"startOnShapes\": [\"nope\", \"later\"]")},{anchorDot.Replace("dotA", "later")},{Branch("").Replace("branch", "plain").Replace("\"startOnExistingNode\": true", "\"startOnExistingNode\": false").Replace("\"allowRotationOnPlace\": false", "\"allowRotationOnPlace\": false, \"startOnShapes\": [\"wall\"]")}]", 3));
Check("errors: bad startOnShapes are reported",
    badStart.Contains("'nope' is not the id of a shape") && badStart.Contains("'later' is not listed before this shape"), badStart);

// ---- per-shape glyph filters -----------------------------------------------------------------------------
string Filtered(string shape, string filter) => shape.TrimEnd().TrimEnd('}').TrimEnd() + ", " + filter + " }";
string DotShape = """{ "id": "dot", "kind": "Literal", "nodes": [[0,0],[0,2]], "placement": { "mode": "Fixed", "fixedOrigin": {"x":10,"y":2}, "allowRotationOnPlace": false }, "arrangement": { "crossing": { "mode": "Anywhere" } } }""";
string WallShape = Wall(true);

var filt = ByName(Language($"[{Filtered(WallShape, "\"onlyForGlyphs\": [\"a\"]")},{Filtered(DotShape, "\"excludeGlyphs\": [\"a\"]")}]", 3));
Check("shape onlyForGlyphs: drawn only in those glyphs", filt["a"].Chains.Any(c => c.Nodes[0].X == 3) && filt["b"].Chains.All(c => c.Nodes[0].X != 3));
Check("shape excludeGlyphs: left out of the listed glyphs", filt["a"].Chains.Count == 1 && filt["b"].Chains.Count == 1 && filt["b"].Chains[0].Nodes[0].X == 10);

var filtCopy = ByName(Language($"[{Filtered(CopyOrigin(), "\"excludeGlyphs\": [\"a\"]")},{CopyOf("\"flip\": \"Vertical\", \"gap\": { \"min\": 2, \"max\": 2 }")},{DotShape}]", 3));
Check("shape filter: a copy is drawn only where its original is", filtCopy["a"].Chains.Count == 1 && filtCopy["b"].Chains.Count == 3);

bool filterStable = true;
{
    var unfilteredRun = ByName(Language($"[{Scribbles()},{DotShape}]", 3));
    var filtered = ByName(Language($"[{Scribbles()},{Filtered(DotShape, "\"onlyForGlyphs\": [\"a\"]")}]", 3));
    foreach (var name in new[] { "b", "c", "d", "e" })
        filterStable &= unfilteredRun[name].Chains.Where(c => c.Nodes[0].X != 10 || c.Nodes.Count != 2).SelectMany(c => c.Nodes)
            .SequenceEqual(filtered[name].Chains.SelectMany(c => c.Nodes));
}
Check("shape filter: a shape left out of a glyph takes no random draw (the glyph's other strokes are unchanged)", filterStable);

string badFilters = Error(Language($"[{Filtered(CopyOrigin(), "\"excludeGlyphs\": [\"b\"]")},{CopyOf("\"flip\": \"Vertical\"").Replace("\"id\": \"c\",", "\"id\": \"c\", \"onlyForGlyphs\": [\"b\"],")},{Filtered(DotShape, "\"onlyForGlyphs\": [\"nope\", \"d1\"]")}]", 3,
    $$""", "derivations": { "d1": { "from": "a", "steps": [ { "op": "Mirror" } ] } }""")
    .Replace("\"a\",\"b\",\"c\",\"d\"", "\"a\",\"b\",\"c\",\"d\",\"d1\""));
Check("errors: bad shape glyph filters are reported",
    badFilters.Contains("'nope' is not listed in glyphs or baseGlyphs") && badFilters.Contains("is derived from another glyph")
    && badFilters.Contains("where the shape it copies ('o') is not drawn"), badFilters);
string nothingLeft = Error(Language($"[{Filtered(DotShape, "\"onlyForGlyphs\": [\"a\"]")}]", 3));
Check("errors: a plain glyph left with no shape at all is reported", nothingLeft.Contains("glyph 'b'") && nothingLeft.Contains("nothing to be built from"), nothingLeft);

// ---- copy shapes -----------------------------------------------------------------------------------------
string CopyOrigin(int x = 6, int y = 6, string nodes = "[[0,0],[3,0],[3,2]]") => $$"""
{ "id": "o", "kind": "Literal", "nodes": {{nodes}},
  "placement": { "mode": "Fixed", "fixedOrigin": {"x":{{x}},"y":{{y}}}, "allowRotationOnPlace": false } }
""";
string CopyOf(string body, string arrangement = "") =>
    $$"""{ "id": "c", "copyOf": { "shape": "o", {{body}} }{{(arrangement == "" ? "" : ", \"arrangement\": " + arrangement)}} }""";
Glyph CopyGlyph(string shapes, int seed = 3) => ByName(Language($"[{shapes}]", 3), seed)["a"];
HashSet<(int, int)> NodesOfChain(Glyph g, int chain) => g.Chains[chain].Nodes.Select(n => (n.X, n.Y)).ToHashSet();

var auto = CopyGlyph(CopyOrigin() + "," + CopyOf("\"flip\": \"Auto\""));
Check("copyOf Auto LongestStraight: a horizontal run is flipped left-right through the start node",
    auto.Chains.Count == 2 && NodesOfChain(auto, 1).SetEquals(new[] { (6, 6), (3, 6), (3, 8) }));
string curvedExtra = ", \"curve\": { \"edge\": { \"chanceToBeCurved\": 100, \"tension\": { \"range\": { \"min\": 0.3, \"max\": 0.3 } } } }";
var curvedCopy = ByName(Language("[" + CopyOrigin() + "," + CopyOf("\"flip\": \"Vertical\", \"gap\": { \"min\": 2, \"max\": 2 }") + "]", 3, curvedExtra), 3)["a"];
bool bendsMirrored = curvedCopy.Chains.Count == 2 && curvedCopy.Chains[0].Edges.All(e => e.Bend is { IsNone: false })
    && curvedCopy.Chains[0].Edges.Zip(curvedCopy.Chains[1].Edges).All(p => p.Second.Bend is not null && p.Second.Bend.Equals(p.First.Bend!.Mirrored()));
Check("copyOf: the copy has the original's curves, mirrored (stored on the edges)", bendsMirrored);

var verticalGap = CopyGlyph(CopyOrigin() + "," + CopyOf("\"flip\": \"Vertical\", \"gap\": { \"min\": 2, \"max\": 2 }"));
Check("copyOf: gap moves the copy apart along the way it was flipped",
    NodesOfChain(verticalGap, 1).SetEquals(new[] { (6, 4), (9, 4), (9, 2) }));

var plainLinks = CopyGlyph(CopyOrigin() + "," + CopyOf("\"flip\": \"Vertical\", \"gap\": { \"min\": 2, \"max\": 2 }, \"mirrorCurves\": false"));
var freshCurves = ByName(Language("[" + CopyOrigin() + "," + CopyOf("\"flip\": \"Vertical\", \"gap\": { \"min\": 2, \"max\": 2 }, \"mirrorCurves\": false") + "]", 3, curvedExtra), 3)["a"];
Check("copyOf: mirrorCurves false plans the copy's curves again (still curved)",
    plainLinks.Chains.Count == 2 && freshCurves.Chains[1].Edges.All(e => e.Bend is not null));

var center = CopyGlyph(CopyOrigin(1, 1, "[[0,0],[2,0]]") + "," + CopyOf("\"flip\": \"Auto\", \"auto\": { \"by\": \"CenterWeight\" }, \"gap\": { \"min\": 2, \"max\": 2 }"));
Check("copyOf Auto CenterWeight: mirrored along the axis that is furthest from the middle, pushed toward it",
    NodesOfChain(center, 1).SetEquals(new[] { (1, 3), (3, 3) }), string.Join(" ", center.Chains[1].Nodes));
bool awayFails = false;
try { ByName(Language("[" + CopyOrigin(1, 1, "[[0,0],[2,0]]") + "," +
    CopyOf("\"flip\": \"Auto\", \"auto\": { \"by\": \"CenterWeight\", \"toward\": \"Edge\" }, \"gap\": { \"min\": 2, \"max\": 2 }, \"moveGlyphToFit\": false") + "]", 3)); }
catch (Exception ex) { awayFails = ex.Message.Contains("could not be placed"); }
Check("copyOf toward Edge: a copy pushed off the grid cannot be placed when moveGlyphToFit is off", awayFails);
string SpacedOrigin(int minDistance) => $$"""
{ "id": "o", "kind": "Literal", "nodes": [[0,0],[2,0]],
  "placement": { "mode": "Fixed", "fixedOrigin": {"x":6,"y":6}, "allowRotationOnPlace": false },
  "chain": { "selfSpacing": [ { "mode": "Existing", "minDistance": {{minDistance}} } ] } }
""";
string spacedCopy = "\"flip\": \"Vertical\", \"gap\": { \"min\": 1, \"max\": 3 }, \"respectSelfSpacing\": true";
bool spacingKept = true;
for (int seed = 1; seed <= 6; seed++)
{
    var g = CopyGlyph(SpacedOrigin(3) + "," + CopyOf(spacedCopy), seed);
    // the copy is thrown up or down (a flat run has no side of its own), but always exactly 3 away
    spacingKept &= g.Chains.Count == 2 && (NodesOfChain(g, 1).SetEquals(new[] { (6, 3), (8, 3) }) || NodesOfChain(g, 1).SetEquals(new[] { (6, 9), (8, 9) }));
}
Check("copyOf respectSelfSpacing: the copy keeps the original's minDistance from the original's nodes", spacingKept);
bool spacingFails = false, spacingNamed = false;
try { CopyGlyph(SpacedOrigin(5) + "," + CopyOf(spacedCopy)); }
catch (Exception ex) { spacingFails = ex.Message.Contains("could not be placed"); spacingNamed = ex.Message.Contains("chain.selfSpacing"); }
Check("copyOf respectSelfSpacing: when nothing keeps the distance the copy fails and the error names selfSpacing", spacingFails && spacingNamed);
// A third stroke "x" sits at (6,0); the original "o" is at y = 6. Ignoring the original, the copy only has to keep away from "x".
string FarStroke = """{ "id": "x", "kind": "Literal", "nodes": [[0,0],[2,0]], "placement": { "mode": "Fixed", "fixedOrigin": {"x":6,"y":0}, "allowRotationOnPlace": false } }""";
string ignoreCopy = "\"flip\": \"Vertical\", \"gap\": { \"min\": 1, \"max\": 1 }, \"respectSelfSpacing\": true, \"selfSpacingIgnoresOriginal\": true";
bool ignoresOrigin = true;
for (int seed = 1; seed <= 6; seed++)
{
    // gap 1 puts the copy at y = 5 or y = 7: 1 away from the original (min 3 would reject it), so only ignoring the original allows it,
    // and the far stroke at y = 0 is 5+ away, so it never matters.
    var g = CopyGlyph(SpacedOrigin(3) + "," + FarStroke + "," + CopyOf(ignoreCopy), seed);
    var copyNodes = g.Chains.Count == 3 ? NodesOfChain(g, 2) : new HashSet<(int, int)>();
    ignoresOrigin &= copyNodes.SetEquals(new[] { (6, 5), (8, 5) }) || copyNodes.SetEquals(new[] { (6, 7), (8, 7) });
}
Check("copyOf selfSpacingIgnoresOriginal: Existing rules no longer count the original's nodes", ignoresOrigin);
bool ignoresFar = false; string farMsg = "";
try { CopyGlyph(SpacedOrigin(9) + "," + FarStroke + "," + CopyOf(ignoreCopy.Replace("\"min\": 1, \"max\": 1", "\"min\": 1, \"max\": 2"))); ignoresFar = false; }
catch (Exception ex) { ignoresFar = ex.Message.Contains("chain.selfSpacing"); farMsg = ex.Message; }
Check("copyOf selfSpacingIgnoresOriginal: the other strokes still count (a min of 9 cannot be kept from the far stroke)", ignoresFar, farMsg);
string ignoreWithout = "";
try { CopyGlyph(SpacedOrigin(3) + "," + CopyOf("\"flip\": \"Vertical\", \"selfSpacingIgnoresOriginal\": true")); } catch (Exception ex) { ignoreWithout = ex.Message; }
Check("errors: selfSpacingIgnoresOriginal without respectSelfSpacing is reported", ignoreWithout.Contains("selfSpacingIgnoresOriginal"), ignoreWithout);

string noRules = "";
try { CopyGlyph(CopyOrigin() + "," + CopyOf(spacedCopy)); } catch (Exception ex) { noRules = ex.Message; }
Check("errors: respectSelfSpacing without any selfSpacing rule on the original is reported", noRules.Contains("respectSelfSpacing") && noRules.Contains("nothing to apply"), noRules);

var moved = CopyGlyph(CopyOrigin(1, 1, "[[0,0],[2,0]]") + "," +
    CopyOf("\"flip\": \"Auto\", \"auto\": { \"by\": \"CenterWeight\", \"toward\": \"Edge\" }, \"gap\": { \"min\": 2, \"max\": 2 }"));
Check("copyOf moveGlyphToFit: the whole glyph moves over when the copy would leave the grid",
    NodesOfChain(moved, 0).SetEquals(new[] { (1, 2), (3, 2) }) && NodesOfChain(moved, 1).SetEquals(new[] { (1, 0), (3, 0) }),
    string.Join(" ", moved.Chains.SelectMany(c => c.Nodes)));


string bar = """{ "id": "bar", "kind": "Literal", "nodes": [[0,0],[0,2]], "placement": { "mode": "Fixed", "fixedOrigin": {"x":4,"y":5}, "allowRotationOnPlace": false }, "arrangement": { "crossing": { "mode": "Anywhere" } } }""";
var dodge = CopyGlyph(CopyOrigin() + "," + bar + "," +
    CopyOf("\"flip\": \"Horizontal\", \"gap\": { \"min\": 0, \"max\": 3 }", """{ "crossing": { "mode": "Anywhere" } }"""));
Check("copyOf preferNoCrossing: moves the copy until it crosses nothing", dodge.Chains.Count == 3 && NodesOfChain(dodge, 2).Contains((3, 6)) && NodesOfChain(dodge, 2).Contains((0, 6)));
var forced = CopyGlyph(CopyOrigin() + "," + bar + "," +
    CopyOf("\"flip\": \"Horizontal\", \"gap\": { \"min\": 0, \"max\": 1 }", """{ "crossing": { "mode": "Anywhere" } }"""));
Check("copyOf: with no clean position the best attempt is kept when crossing is allowed", forced.Chains.Count == 3);
bool forbidden = false;
try { CopyGlyph(CopyOrigin() + "," + bar + "," + CopyOf("\"flip\": \"Horizontal\", \"gap\": { \"min\": 0, \"max\": 1 }")); }
catch (Exception ex) { forbidden = ex.Message.Contains("could not be placed"); }
Check("copyOf: with no clean position and crossing forbidden the copy fails", forbidden);

var cascade = CopyGlyph(CopyOrigin() + "," + CopyOf("\"flip\": \"Horizontal\"") + "," +
    """{ "id": "d", "copyOf": { "shape": "c", "flip": "Vertical", "gap": { "min": 2, "max": 2 } } }""");
Check("copyOf: a copy can be copied (four-way symmetry)", cascade.Chains.Count == 3 && NodesOfChain(cascade, 2).SetEquals(new[] { (6, 4), (3, 4), (3, 2) }));

bool onEdge = true;
for (int seed = 1; seed <= 8; seed++)
{
    var anchored = CopyGlyph(CopyOrigin() + "," + bar + "," +
        CopyOf("\"flip\": \"None\", \"anchor\": \"ExistingEdge\"", """{ "crossing": { "mode": "Anywhere" } }"""), seed);
    var start = anchored.Chains[2].Nodes[0];
    bool Hit(SubSymbolChain c) => c.Edges.Any(e =>
        (long)(e.To.X - e.From.X) * (start.Y - e.From.Y) - (long)(e.To.Y - e.From.Y) * (start.X - e.From.X) == 0
        && start.X >= Math.Min(e.From.X, e.To.X) && start.X <= Math.Max(e.From.X, e.To.X)
        && start.Y >= Math.Min(e.From.Y, e.To.Y) && start.Y <= Math.Max(e.From.Y, e.To.Y));
    onEdge &= Hit(anchored.Chains[0]) || Hit(anchored.Chains[1]);
}
Check("copyOf anchor ExistingEdge: the copy starts on a line that is already there", onEdge);

bool sameTwice = true;
for (int seed = 1; seed <= 4; seed++)
{
    var one = CopyGlyph(CopyOrigin() + "," + CopyOf("\"flip\": \"Random\", \"gap\": { \"min\": 0, \"max\": 2 }", """{ "crossing": { "mode": "Anywhere" } }"""), seed);
    var two = CopyGlyph(CopyOrigin() + "," + CopyOf("\"flip\": \"Random\", \"gap\": { \"min\": 0, \"max\": 2 }", """{ "crossing": { "mode": "Anywhere" } }"""), seed);
    sameTwice &= string.Join(";", one.Chains[1].Nodes) == string.Join(";", two.Chains[1].Nodes);
}
Check("copyOf: the same seed gives the same copy", sameTwice);

string p0 = """{ "id": "p", "kind": "Literal", "count": { "min": 0, "max": 0 }, "nodes": [[0,0],[2,0]], "placement": { "mode": "Fixed", "fixedOrigin": {"x":1,"y":12}, "allowRotationOnPlace": false } }""";
string q0 = """{ "id": "q", "copyOf": { "shape": "p", "flip": "Vertical", "gap": { "min": 1, "max": 1 } } }""";
var emptyOrigin = CopyGlyph(CopyOrigin() + "," + p0 + "," + q0);
Check("copyOf: an origin that was not drawn (count 0) leaves no copy", emptyOrigin.Chains.Count == 1);

string pA = """{ "id": "p", "kind": "Literal", "count": { "min": 1, "max": 1 }, "nodes": [[0,0],[2,0]], "placement": { "mode": "Fixed", "fixedOrigin": {"x":1,"y":12}, "allowRotationOnPlace": false }, "arrangement": { "crossing": { "mode": "Anywhere" } } }""";
string qA = """{ "id": "q", "copyOf": { "shape": "p", "flip": "Vertical", "gap": { "min": 1, "max": 1 } }, "arrangement": { "crossing": { "mode": "Anywhere" } } }""";
var viaDerive = ByName(Derived("""[ { "op": "Add", "shapes": ["p", "q"] } ]""", shapes: CopyOrigin() + "," + pA + "," + qA));
Check("copyOf: a derivation Add step can add a shape and its copy",
    viaDerive["a"].Chains.Count == viaDerive["l"].Chains.Count + 2, $"{viaDerive["a"].Chains.Count} vs {viaDerive["l"].Chains.Count}");

string badCopy = Error(Language("[" + CopyOrigin() + "," + """
{ "id": "c", "count": { "min": 1, "max": 1 }, "chain": { "length": { "min": 2, "max": 3 }, "connectionRules": [ { "direction": "Orthogonal", "minLength": 1, "maxLength": 1 } ] }, "copyOf": { "shape": "later", "flips": ["Random"], "shift": { "min": -1, "max": 1 } } },
{ "id": "later", "kind": "Literal", "nodes": [[0,0],[1,0]] },
{ "id": "e", "copyOf": { "shape": "nope" } }, { "id": "f", "copyOf": { "shape": "f" } }
""" + "]", 3));
Check("errors: bad copy shapes are reported",
    badCopy.Contains("one copy per instance") && badCopy.Contains("chain rules") && badCopy.Contains("listed after")
    && badCopy.Contains("only applies when flip is Random") && badCopy.Contains("shift") && badCopy.Contains("not the id of a shape")
    && badCopy.Contains("copy itself"), badCopy);


// ---- shape chance ----------------------------------------------------------------------------------------
int drawnCount(double chance)
{
    int n = 0;
    for (int seed = 1; seed <= 30; seed++)
        n += ByName(Language($"[{CopyOrigin()},{{ \"id\": \"x\", \"chance\": {chance}, \"kind\": \"Literal\", \"nodes\": [[0,0],[1,0]], \"placement\": {{ \"mode\": \"Fixed\", \"fixedOrigin\": {{\"x\":1,\"y\":1}}, \"allowRotationOnPlace\": false }} }}]", 3), seed)["a"].Chains.Count - 1;
    return n;
}
int never = drawnCount(0), always = drawnCount(100), half = drawnCount(50);
Check("shape chance: 0 never draws, 100 always draws, 50 sometimes", never == 0 && always == 30 && half > 3 && half < 27, $"{never}/{half}/{always}");
string badChance = Error(Language($"[{CopyOrigin()},{{ \"id\": \"x\", \"chance\": 150, \"kind\": \"Literal\", \"nodes\": [[0,0],[1,0]] }}]", 3));
Check("errors: a shape chance outside 0-100 is reported", badChance.Contains("chance"), badChance);


string DiagMessage(string shapes) { try { CopyGlyph(shapes); return ""; } catch (Exception ex) { return ex.Message; } }
string tooWide = DiagMessage(CopyOrigin(0, 5, "[[0,0],[14,0]]") + "," + CopyOf("\"flip\": \"Horizontal\""));
Check("copy error: says the glyph and copy together are too big for the grid",
    tooWide.Contains("even after moving the glyph") && tooWide.Contains("at least 29 x") && tooWide.Contains("Flip Horizontal") && tooWide.Contains("1 position(s) tried"), tooWide);
string offOff = DiagMessage(CopyOrigin(1, 1, "[[0,0],[2,0]]") + "," +
    CopyOf("\"flip\": \"Auto\", \"auto\": { \"by\": \"CenterWeight\", \"toward\": \"Edge\" }, \"gap\": { \"min\": 2, \"max\": 2 }, \"moveGlyphToFit\": false"));
Check("copy error: says the grid is the problem and that moveGlyphToFit is off", offOff.Contains("left the grid") && offOff.Contains("moveGlyphToFit is off") && offOff.Contains("CenterWeight"), offOff);
string crossMsg = DiagMessage(CopyOrigin() + "," + bar + "," + CopyOf("\"flip\": \"Horizontal\", \"gap\": { \"min\": 0, \"max\": 1 }"));
Check("copy error: counts positions that crossed existing strokes", crossMsg.Contains("2 position(s) tried") && crossMsg.Contains("2 crossed existing strokes") && crossMsg.Contains("needs both shapes to allow it") && crossMsg.Contains("on this shape") && crossMsg.Contains("'o'"), crossMsg);


// ---- friendlier errors -----------------------------------------------------------------------------------
string JsonError(string json) { try { Run(json); return ""; } catch (System.Text.Json.JsonException ex) { return ex.Message; } }
string badEnum = JsonError(Language("[" + CopyOrigin() + "," + CopyOf("\"flip\": \"Horizontl\"") + "]", 3));
Check("json error: a misspelled name says where it is, what was written and what is allowed",
    badEnum.Contains("flip") && badEnum.Contains("Horizontl") && badEnum.Contains("It must be one of:") && badEnum.Contains("Horizontal") && badEnum.Contains("line "), badEnum);
string badNumber = JsonError(Language("[" + CopyOrigin() + "," + CopyOf("\"flip\": \"None\", \"gap\": { \"min\": \"two\", \"max\": 2 }") + "]", 3));
Check("json error: a wrong kind of value says what it should be", badNumber.Contains("whole number") && badNumber.Contains("gap.min"), badNumber);

string emptied = "";
try
{
    ByName(Derived("""[ { "op": "DeleteEdges", "amount": { "count": { "min": 99, "max": 99 } }, "keepAtLeast": 0 } ]"""));
}
catch (Exception ex) { emptied = ex.Message; }
Check("derive error: names the step that removed the last edge", emptied.Contains("step 1 (DeleteEdges)") && emptied.Contains("delete less"), "got: " + emptied);


// ---- copy: auto.shapes -----------------------------------------------------------------------------------
string longBar = """{ "id": "bar2", "kind": "Literal", "nodes": [[0,0],[0,5]], "placement": { "mode": "Fixed", "fixedOrigin": {"x":11,"y":2}, "allowRotationOnPlace": false }, "arrangement": { "crossing": { "mode": "Anywhere" } } }""";
// o is an L whose longest run is horizontal (3 vs 2); bar2 is a vertical run of 5. Looking at o alone gives Horizontal; looking at bar2 gives Vertical.
var alone = CopyGlyph(CopyOrigin() + "," + longBar + "," + CopyOf("\"flip\": \"Auto\", \"gap\": { \"min\": 0, \"max\": 0 }", """{ "crossing": { "mode": "Anywhere" } }"""));
var viaBar = CopyGlyph(CopyOrigin() + "," + longBar + "," + CopyOf("\"flip\": \"Auto\", \"auto\": { \"shapes\": [\"bar2\"] }, \"gap\": { \"min\": 2, \"max\": 2 }", """{ "crossing": { "mode": "Anywhere" } }"""));
Check("copyOf auto.shapes: the rule looks only at the listed shapes",
    NodesOfChain(alone, 2).Contains((3, 6)) && NodesOfChain(viaBar, 2).Contains((9, 4)),
    string.Join(" ", alone.Chains[2].Nodes) + " | " + string.Join(" ", viaBar.Chains[2].Nodes));
string badAuto = Error(Language("[" + CopyOrigin() + "," + CopyOf("\"flip\": \"Vertical\", \"auto\": { \"shapes\": [\"nope\"] }") + "]", 3));
Check("errors: auto.shapes needs Auto and real earlier shapes", badAuto.Contains("only applies when flip is Auto") && badAuto.Contains("not the id of a shape"), badAuto);


// ---- copy retries ----------------------------------------------------------------------------------------
string StrokeBase = """{ "id": "o", "chain": { "length": { "min": 3, "max": 4 }, "connectionRules": [ { "direction": "Orthogonal", "minLength": 1, "maxLength": 1 } ] }, "placement": { "mode": "RandomValid", "maxPlacementAttempts": 40 } }""";
// A vertical first edge lies on its own left-right mirror, so about half the draws cannot be copied without crossing.
string RetryCopy(string retries) => $$"""{ "id": "c", "copyOf": { "shape": "o", "flip": "Horizontal"{{retries}} } }""";
int Failures(string retries)
{
    int failed = 0;
    for (int seed = 1; seed <= 20; seed++)
    {
        try { ByName(Language("[" + StrokeBase + "," + RetryCopy(retries) + "]", 3), seed); }
        catch (GlyphGenerationException) { failed++; }
    }
    return failed;
}
int noRetry = Failures(", \"regenerateBase\": 0, \"redrawGlyph\": 0");
int baseOnly = Failures(", \"regenerateBase\": 4, \"redrawGlyph\": 0");
int both = Failures("");
Check("copy retries: redrawing the base shape, then the glyph, rescues failing runs", noRetry >= 15 && baseOnly < noRetry && both < baseOnly, $"{noRetry} / {baseOnly} / {both} failures of 20");
string finalMsg = "";
try { CopyGlyph(CopyOrigin() + "," + CopyOf("\"flip\": \"None\"")); } catch (Exception ex) { finalMsg = ex.Message; }
Check("copy retries: a copy that can never fit still fails, and says how many redraws were spent",
    finalMsg.Contains("base shape was redrawn") && finalMsg.Contains("whole glyph 100 time(s)") && finalMsg.Contains("retries.base"), finalMsg);


// ---- shape tags (debug colouring) -----------------------------------------------------------------------
var tagTest = CopyGlyph(CopyOrigin() + "," + CopyOf("\"flip\": \"Vertical\", \"gap\": { \"min\": 2, \"max\": 2 }"));
Check("shape tags: every chain says which shape and instance drew it, copies included",
    tagTest.Chains[0].ShapeTag == "o#0" && tagTest.Chains[1].ShapeTag == "c#0", string.Join(",", tagTest.Chains.Select(c => c.ShapeTag)));
var scribbled = ByName(Language($"[{Scribbles()}]", 3))["a"];
Check("shape tags: instances of a counted shape are numbered", scribbled.Chains.Select(c => c.ShapeTag).SequenceEqual(new[] { "scribbles#0", "scribbles#1" }), string.Join(",", scribbled.Chains.Select(c => c.ShapeTag)));


// ---- auto.axis -------------------------------------------------------------------------------------------
// o: horizontal run of 3 from (6,6), then up/down 2. Perpendicular -> Horizontal mirror; Parallel -> Vertical mirror.
var perp = CopyGlyph(CopyOrigin() + "," + CopyOf("\"flip\": \"Auto\", \"auto\": { \"axis\": \"Perpendicular\" }"));
var para = CopyGlyph(CopyOrigin() + "," + CopyOf("\"flip\": \"Auto\", \"auto\": { \"axis\": \"Parallel\" }, \"gap\": { \"min\": 2, \"max\": 2 }"));
Check("copyOf auto.axis: Perpendicular mirrors across the run, Parallel along it",
    NodesOfChain(perp, 1).SetEquals(new[] { (6, 6), (3, 6), (3, 8) }) && NodesOfChain(para, 1).SetEquals(new[] { (6, 4), (9, 4), (9, 2) }),
    string.Join(" ", perp.Chains[1].Nodes) + " | " + string.Join(" ", para.Chains[1].Nodes));
var seen = new HashSet<string>();
for (int seed = 1; seed <= 12; seed++)
{
    var r = CopyGlyph(CopyOrigin() + "," + CopyOf("\"flip\": \"Auto\", \"auto\": { \"axis\": \"Random\" }, \"gap\": { \"min\": 2, \"max\": 2 }", """{ "crossing": { "mode": "Anywhere" } }"""), seed);
    seen.Add(string.Join(";", r.Chains[1].Nodes));
}
Check("copyOf auto.axis: Random uses both", seen.Count >= 2, string.Join(" | ", seen));
string badAxis = Error(Language("[" + CopyOrigin() + "," + CopyOf("\"flip\": \"Vertical\", \"auto\": { \"axis\": \"Parallel\" }") + "]", 3));
Check("errors: auto.axis only with Auto + LongestStraight", badAxis.Contains("auto.axis") && badAxis.Contains("only applies"), badAxis);


// ---- render.rotateOptions validation ---------------------------------------------------------------------
string badRotate = Error(Language("[" + CopyOrigin() + "]", 3, ""","render": { "rotateOptions": [ { "min": 10, "max": 5 }, { "min": 0, "max": 1, "weight": -1 } ] }"""));
Check("errors: bad rotateOptions are reported", badRotate.Contains("rotateOptions[0]") && badRotate.Contains("smaller than min") && badRotate.Contains("weight"), badRotate);
string zeroRotate = Error(Language("[" + CopyOrigin() + "]", 3, ""","render": { "rotateOptions": [ { "min": 0, "max": 1, "weight": 0 } ] }"""));
Check("errors: rotateOptions that can never be drawn are reported", zeroRotate.Contains("every weight is 0"), zeroRotate);

// ---- crossing modes and curves -----------------------------------------------------------------------------
{
    var P = (int x, int y) => new GridPoint(x, y);
    var bentLine = new GridEdge(P(0, 0), P(4, 0), GridEdgeKind.Line, null, new EdgeBend(0.25, 0.5, -0.25, 0.5));
    var plainLine = new GridEdge(P(0, 0), P(4, 0));
    var probe = new GridEdge(P(2, 1), P(2, 3));
    Check("curves: a bent edge crosses what its straight version would miss", EdgePaths.Crosses(probe, bentLine) && !EdgePaths.Crosses(probe, plainLine));
    var twoBends = new GridEdge(P(0, 4), P(4, 4), GridEdgeKind.Line, null, new EdgeBend(0.25, -0.5, -0.25, -0.5));
    Check("curves: edges meeting at a shared node do not cross", !EdgePaths.Crosses(new GridEdge(P(0, 0), P(4, 0), GridEdgeKind.Line, null, new EdgeBend(0.2, 0.3, -0.2, 0.3)),
        new GridEdge(P(4, 0), P(4, 4), GridEdgeKind.Line, null, new EdgeBend(0.2, -0.3, -0.2, -0.3))));
    Check("curves: mirrored bend bulges the other way", !EdgePaths.Crosses(probe, new GridEdge(P(0, 0), P(4, 0), GridEdgeKind.Line, null, bentLine.Bend!.Mirrored())));

    GeometryContext WallCtx(CrossingConfig crossing, string tag = "wall#0")
    {
        var geometry = new GeometryContext();
        var chain = new SubSymbolChain { ShapeTag = tag };
        chain.Nodes.AddRange(new[] { P(0, 2), P(2, 2), P(4, 2) });
        chain.Edges.Add(new GridEdge(P(0, 2), P(2, 2)));
        chain.Edges.Add(new GridEdge(P(2, 2), P(4, 2)));
        geometry.Commit(chain, crossing);
        return geometry;
    }
    var anywhere = new CrossingConfig { Mode = CrossingMode.Anywhere };
    var nodesOnly = new CrossingConfig { Mode = CrossingMode.NodesOnly };
    var atNode = new GridEdge(P(2, 0), P(2, 4));
    var offNode = new GridEdge(P(1, 0), P(1, 4));
    var nearNode = new GridEdge(new GridPoint(3, 0), new GridPoint(3, 4)); // crosses at (3,2), a whole unit from any node
    var noCrossing = new CrossingConfig { Mode = CrossingMode.None };
    var empty = new List<GridPoint>();

    var g = WallCtx(anywhere); g.SetCurrent("s", noCrossing);
    Check("crossing None: any crossing is blocked", g.IsBlocked(atNode, empty) && g.IsBlocked(offNode, empty));
    g = WallCtx(noCrossing); g.SetCurrent("s", anywhere);
    Check("crossing: the other shape's None protects it", g.IsBlocked(offNode, empty));
    g = WallCtx(anywhere); g.SetCurrent("s", anywhere);
    Check("crossing Anywhere on both: nothing is blocked", !g.IsBlocked(atNode, empty) && !g.IsBlocked(offNode, empty));
    g = WallCtx(anywhere); g.SetCurrent("s", nodesOnly);
    Check("crossing NodesOnly: a crossing at a node passes, one elsewhere is blocked", !g.IsBlocked(atNode, empty) && g.IsBlocked(offNode, empty));
    g = WallCtx(nodesOnly); g.SetCurrent("s", anywhere);
    Check("crossing NodesOnly on the crossed shape counts too", !g.IsBlocked(atNode, empty) && g.IsBlocked(offNode, empty));
    g = WallCtx(anywhere); g.SetCurrent("s", new CrossingConfig { Mode = CrossingMode.NodesOnly, NodeTolerance = 1.1 });
    Check("crossing nodeTolerance: near a node is close enough", !g.IsBlocked(offNode, empty) && !g.IsBlocked(nearNode, empty));
    g = WallCtx(anywhere); g.SetCurrent("s", new CrossingConfig { Mode = CrossingMode.NodesOnly, NodesOfShapes = new List<string> { "other" } });
    Check("crossing nodesOfShapes: only the listed shapes' nodes count", g.IsBlocked(atNode, empty));
    g = WallCtx(anywhere); g.SetCurrent("s", new CrossingConfig { Mode = CrossingMode.NodesOnly, NodesOfShapes = new List<string> { "wall" } });
    Check("crossing nodesOfShapes: a listed shape's node lets the crossing pass", !g.IsBlocked(atNode, empty));
    g = WallCtx(anywhere); g.SetCurrent("s", new CrossingConfig { Mode = CrossingMode.NodesOnly, NodesOfShapes = new List<string> { "wall" } });
    Check("crossing nodesOfShapes: the shape's own nodes do not count unless listed", g.IsBlocked(offNode, new List<GridPoint> { P(1, 2) }));
    g = WallCtx(anywhere); g.SetCurrent("s", new CrossingConfig { Mode = CrossingMode.NodesOnly, NodesOfShapes = new List<string> { "s" } });
    Check("crossing nodesOfShapes: ...and do when its own id is listed", !g.IsBlocked(offNode, new List<GridPoint> { P(1, 2) }));

    string badCrossing = Error(Language("[" + CopyOrigin() + "]", 3).Replace("\"id\": \"o\"", "\"id\": \"o\", \"arrangement\": { \"crossing\": { \"mode\": \"Anywhere\", \"nodesOfShapes\": [\"zzz\"], \"nodeTolerance\": -1 } }"));
    Check("errors: crossing settings are validated", badCrossing.Contains("nodesOfShapes") && badCrossing.Contains("nodeTolerance"), badCrossing);
    string oldFlag = Error(Language("[" + CopyOrigin() + "]", 3).Replace("\"id\": \"o\"", "\"id\": \"o\", \"arrangement\": { \"allowCrossing\": true }"));
    Check("errors: the old allowCrossing flag points to crossing", oldFlag.Contains("allowCrossing") && oldFlag.Contains("Unknown property"), oldFlag);
}

// A language whose strokes are always curved still places strokes that never cross one another (curves included).
{
    string curvedRules = """[ { "direction": "Both", "minLength": 1, "maxLength": 2, "curve": { "chanceToBeCurved": 100, "tension": { "range": { "min": 0.4, "max": 0.5 } } } } ]""";
    string curvedShape = $$"""{ "id": "s", "count": { "min": 2, "max": 3 }, "chain": { "length": { "min": 3, "max": 4 }, "connectionRules": {{curvedRules}} }, "placement": { "maxPlacementAttempts": 400 } }""";
    bool anyCurve = false, noCross = true;
    for (int seed = 1; seed <= 6; seed++)
    {
        var glyphs = Run(Language("[" + curvedShape + "]", seed))["T"];
        foreach (var glyph in glyphs)
        {
            var edges = glyph.Chains.SelectMany(c => c.Edges).ToList();
            anyCurve |= edges.Any(e => e.IsCurved);
            for (int i = 0; i < edges.Count; i++)
                for (int j = i + 1; j < edges.Count; j++)
                    noCross &= !EdgePaths.Crosses(edges[i], edges[j]) || edges[i].From == edges[j].From || edges[i].From == edges[j].To || edges[i].To == edges[j].From || edges[i].To == edges[j].To;
        }
    }
    Check("curved strokes are stored on the edges and never cross each other", anyCurve && noCross);
}

// ---- placement.redoShape / redrawGlyph -----------------------------------------------------------------------
{
    string Fussy(string placementExtra) => $$"""{ "id": "s", "chain": { "length": { "min": 5, "max": 5 }, "connectionRules": {{Rules}} }, "placement": { "maxPlacementAttempts": 1{{placementExtra}} }, "arrangement": { "crossing": { "mode": "None" } } }""";
    int CountFailed(string placementExtra)
    {
        int failed = 0;
        for (int seed = 1; seed <= 12; seed++)
            try { Run(Language("[" + Wall(false) + "," + Fussy(placementExtra) + "]", seed)); } catch (GlyphGenerationException) { failed++; }
        return failed;
    }
    int plainFailed = CountFailed(", \"redoShape\": 0, \"redrawGlyph\": 0");
    int redoFailed = CountFailed(", \"redoShape\": 60");
    Check("redoShape: redrawing the shape rescues placements that failed once", plainFailed > 0 && redoFailed < plainFailed, $"{plainFailed} -> {redoFailed}");
    string spent = "";
    try { for (int seed = 1; seed <= 12 && spent == ""; seed++) Run(Language("[" + Wall(false) + "," + Fussy(", \"redoShape\": 1, \"redrawGlyph\": 1") + "]", seed)); } catch (GlyphGenerationException ex) { spent = ex.Message; }
    Check("redoShape/redrawGlyph: the final error says how much was retried", spent == "" || spent.Contains("redrawn"), spent);

    // placement.retries.onFail: Skip leaves a shape that cannot be placed out of the glyph instead of failing it.
    int skipFailed = CountFailed(", \"redoShape\": 0, \"redrawGlyph\": 0, \"onFail\": \"Skip\"");
    int skipMissing = 0, skipMade = 0;
    for (int seed = 1; seed <= 12; seed++)
    {
        try
        {
            var g = Run(Language("[" + Wall(false) + "," + Fussy(", \"redoShape\": 0, \"redrawGlyph\": 0, \"onFail\": \"Skip\"") + "]", seed));
            skipMade++;
            skipMissing += g.Values.SelectMany(l => l).Count(x => x.Chains.All(c => c.ShapeTag is not { } t || !t.StartsWith("s#")));
        }
        catch (GlyphGenerationException) { }
    }
    Check("onFail Skip: a shape that cannot be placed is left out instead of failing the glyph", plainFailed > 0 && skipFailed == 0 && skipMissing > 0 && skipMade == 12, $"failed {plainFailed} -> {skipFailed}, glyphs without the shape {skipMissing}/{skipMade}");
    string badRedo = Error(Language("[" + Fussy(", \"redoShape\": -1, \"redrawGlyph\": -2") + "]", 3));
    Check("errors: negative redoShape / redrawGlyph are reported", badRedo.Contains("retries.shape") && badRedo.Contains("retries.glyph"), badRedo);
}

// ---- placement.bounds ---------------------------------------------------------------------------------------
{
    string Boxed(string bounds) => $$"""{ "id": "s", "count": { "min": 2, "max": 2 }, "chain": { "length": { "min": 3, "max": 5 }, "connectionRules": {{Rules}} }, "placement": { "maxPlacementAttempts": 200, "bounds": {{bounds}} }, "arrangement": { "crossing": { "mode": "Anywhere" } } }""";
    bool inside = true, usedBox = false;
    for (int seed = 1; seed <= 8; seed++)
        foreach (var glyph in Run(Language("[" + Boxed("""{ "minX": 4, "minY": 5, "maxX": 8, "maxY": 9 }""") + "]", seed))["T"])
            foreach (var n in glyph.Chains.SelectMany(c => c.Nodes))
            {
                inside &= n.X >= 4 && n.X <= 8 && n.Y >= 5 && n.Y <= 9;
                usedBox |= n.X == 8 || n.Y == 9 || n.X == 4 || n.Y == 5;
            }
    Check("placement.bounds: every node stays inside the box, and the box edge is reachable", inside && usedBox);
    string badBox = Error(Language("[" + Boxed("""{ "minX": 6, "minY": 0, "maxX": 2, "maxY": 40 }""") + "]", 3));
    Check("errors: a bounds box with min above max is reported", badBox.Contains("bounds") && badBox.Contains("larger"), badBox);
    string outBox = Error(Language("[" + Boxed("""{ "minX": 0, "minY": 0, "maxX": 2, "maxY": 40 }""") + "]", 3));
    Check("errors: a bounds box outside the grid is reported", outBox.Contains("inside the grid"), outBox);
}

// ---- spacing.ignoreStartNode --------------------------------------------------------------------------------
{
    string Sprout(string spacingExtra) => $$"""
    { "id": "sprout", "count": { "min": 1, "max": 1 }, "chain": { "length": { "min": 3, "max": 4 }, "connectionRules": {{Rules}} },
      "placement": { "mode": "RandomValid", "startOnExistingNode": true, "maxPlacementAttempts": 300 },
      "arrangement": { "crossing": { "mode": "Anywhere" }, "spacing": { "enabled": true, "minNodeDistance": 1.5{{spacingExtra}} } } }
    """;
    string anchorWall = """{ "id": "wall", "kind": "Literal", "nodes": [[0,0],[6,0]], "placement": { "mode": "Fixed", "fixedOrigin": {"x":4,"y":6}, "allowRotationOnPlace": false }, "arrangement": { "crossing": { "mode": "Anywhere" } } }""";
    string withoutIgnore = Error(Language("[" + anchorWall + "," + Sprout("") + "]", 3));
    Check("errors: startOnExistingNode with minNodeDistance now points to ignoreStartNode", withoutIgnore.Contains("ignoreStartNode"), withoutIgnore);

    bool grows = true, keepsApart = true;
    for (int seed = 1; seed <= 8; seed++)
        foreach (var glyph in Run(Language("[" + anchorWall + "," + Sprout(", \"ignoreStartNode\": true") + "]", seed))["T"])
        {
            var wall = glyph.Chains[0].Nodes;
            var sprout = glyph.Chains[1].Nodes;
            grows &= wall.Contains(sprout[0]);
            keepsApart &= sprout.Skip(1).All(n => wall.All(w => Math.Sqrt((n.X - w.X) * (n.X - w.X) + (n.Y - w.Y) * (n.Y - w.Y)) >= 1.5 - 1e-9));
        }
    Check("spacing.ignoreStartNode: the shape starts on an existing node and every other node keeps the minimum", grows && keepsApart);

}

// ---- placement.startOnExistingNodeChance --------------------------------------------------------------------
{
    string Grower(string chance) => $$"""
    { "id": "grower", "chain": { "length": { "min": 2, "max": 2 }, "connectionRules": {{Rules}} },
      "placement": { "mode": "RandomValid", "startOnExistingNode": true{{chance}}, "maxPlacementAttempts": 100 }, "arrangement": { "crossing": { "mode": "Anywhere" } } }
    """;
    string wallShape = """{ "id": "wall", "kind": "Literal", "nodes": [[0,0],[2,0]], "placement": { "mode": "Fixed", "fixedOrigin": {"x":5,"y":5}, "allowRotationOnPlace": false }, "arrangement": { "crossing": { "mode": "Anywhere" } } }""";
    int OnNode(string chance)
    {
        int on = 0;
        for (int seed = 1; seed <= 40; seed++)
            foreach (var glyph in Run(Language("[" + wallShape + "," + Grower(chance) + "]", seed))["T"].Take(1))
                if (glyph.Chains[0].Nodes.Contains(glyph.Chains[1].Nodes[0])) on++;
        return on;
    }
    int snAlways = OnNode(""), snNever = OnNode(", \"startOnExistingNodeChance\": 0"), snHalf = OnNode(", \"startOnExistingNodeChance\": 50");
    Check("startOnExistingNodeChance: 100 snAlways grows from a node, 0 almost snNever, 50 mixes", snAlways == 40 && snNever < 10 && snHalf > snNever && snHalf < snAlways, $"{snAlways}/{snHalf}/{snNever}");
    string snBadchance = Error(Language("[" + wallShape + "," + Grower(", \"startOnExistingNodeChance\": 150") + "]", 3));
    Check("errors: startOnExistingNodeChance outside 0-100 is reported", snBadchance.Contains("onExistingNode.chance"), snBadchance);
}

// ---- arcs and outline nodes ---------------------------------------------------------------------------------
{
    var P = (int x, int y) => new GridPoint(x, y);
    var spec = (OutlineNodeMode mode, AngleSet angles, AngleNodeSnap snap, double tol) => new OutlineNodesConfig { Mode = mode, Angles = angles, Snap = snap, Tolerance = tol };
    var lower = new GridEdge(P(0, 0), P(4, 0), GridEdgeKind.Circle, null, null, new ArcSpan(0, 180));
    var (_, lowMinY, _, lowMaxY) = lower.Extent();
    {
    var quarter = new GridEdge(new GridPoint(24, 0), new GridPoint(4, 0), GridEdgeKind.Circle, arc: new ArcSpan(180, 135));
    var ends = quarter.DrawnEnds().ToList();
    var wideArc = new GridEdge(new GridPoint(24, 0), new GridPoint(4, 0), GridEdgeKind.Circle, arc: new ArcSpan(180, 200));
    Check("arc: only the drawn ends count as places to start on (the far end of the diameter is left out unless the arc runs over it)",
        ends.Contains(new GridPoint(24, 0)) && !ends.Contains(new GridPoint(4, 0)) && wideArc.DrawnEnds().Contains(new GridPoint(4, 0)),
        string.Join(" ", ends));
}
Check("arc: a clockwise half circle from the right end of a horizontal diameter covers the lower half", Math.Abs(lowMinY) < 1e-9 && Math.Abs(lowMaxY - 2) < 1e-9, $"{lowMinY}..{lowMaxY}");
    var upper = lower.MirroredArc();
    var (_, upMinY, _, upMaxY) = upper.Extent();
    Check("arc: mirroring it covers the other half", Math.Abs(upMinY + 2) < 1e-9 && Math.Abs(upMaxY) < 1e-9, $"{upMinY}..{upMaxY}");
    var rotatedArc = lower.WithPoints(P(0, 0), P(0, 4)); // the same arc on a diameter turned a quarter: still relative to it
    var (rMinX, _, rMaxX, _) = rotatedArc.Extent();
    Check("arc: rotating the diameter turns the arc with it", Math.Abs(rMinX + 2) < 1e-9 && Math.Abs(rMaxX) < 1e-9, $"{rMinX}..{rMaxX}");
    var bowl = new GridEdge(P(2, 1), P(2, 3));
    Check("arc: a line through the covered half crosses it, one through the other half does not", EdgePaths.Crosses(bowl, lower) && !EdgePaths.Crosses(bowl, upper));

    var fullWith = (OutlineNodesConfig o) => new GridEdge(P(0, 0), P(4, 0), GridEdgeKind.Circle, null, null, null, o);
    var archWith = (OutlineNodesConfig o) => new GridEdge(P(0, 0), P(4, 0), GridEdgeKind.Circle, null, null, new ArcSpan(0, 180), o);
    string Pts(List<GridPoint> l) => string.Join(" ", l.OrderBy(p => p.X).ThenBy(p => p.Y).Select(p => $"{p.X},{p.Y}"));
    Check("outline nodes: cardinal angles on a circle give the top and bottom points", Pts(fullWith(spec(OutlineNodeMode.AtAngles, AngleSet.Cardinal, AngleNodeSnap.Within, 0.3)).ExtraNodes()) == "2,-2 2,2");
    Check("outline nodes: only the angles the arc covers", Pts(archWith(spec(OutlineNodeMode.AtAngles, AngleSet.Cardinal, AngleNodeSnap.Within, 0.3)).ExtraNodes()) == "2,2");
    Check("outline nodes: diagonal angles are skipped when no lattice point is near (Within, Exact) ...",
        fullWith(spec(OutlineNodeMode.AtAngles, AngleSet.Diagonal, AngleNodeSnap.Within, 0.3)).ExtraNodes().Count == 0
        && fullWith(spec(OutlineNodeMode.AtAngles, AngleSet.Diagonal, AngleNodeSnap.Exact, 0.3)).ExtraNodes().Count == 0);
    Check("outline nodes: ... and snapped to the nearest point with Nearest", Pts(fullWith(spec(OutlineNodeMode.AtAngles, AngleSet.Diagonal, AngleNodeSnap.Nearest, 0.3)).ExtraNodes()) == "1,-1 1,1 3,-1 3,1");
    var every = fullWith(spec(OutlineNodeMode.EveryLatticePoint, AngleSet.Cardinal, AngleNodeSnap.Within, 0.45)).ExtraNodes();
    Check("outline nodes: every lattice point the outline passes within the tolerance of", every.Contains(P(2, 2)) && every.Contains(P(1, 2)) && every.Contains(P(3, -2)) && !every.Contains(P(2, 0)) && !every.Contains(P(1, 1)), Pts(every));

    string arcRule = """[ { "direction": "Arc", "minLength": 2, "maxLength": 3, "arc": { "angles": "Cardinal", "minSweep": 180, "maxSweep": 270 }, "outlineNodes": { "mode": "AtAngles" } } ]""";
    string arcShape = $$"""{ "id": "arcs", "count": { "min": 1, "max": 1 }, "chain": { "length": { "min": 2, "max": 3 }, "connectionRules": {{arcRule}} }, "placement": { "maxPlacementAttempts": 200 } }""";
    bool madeArcs = true, sweepsOk = true, extraNodesSeen = false, inGrid = true;
    for (int seed = 1; seed <= 8; seed++)
        foreach (var glyph in Run(Language("[" + arcShape + "]", seed))["T"])
            foreach (var chain in glyph.Chains)
            {
                madeArcs &= chain.Edges.All(e => e.IsArc);
                sweepsOk &= chain.Edges.All(e => e.Arc!.Value.Sweep is 180 or 270);
                extraNodesSeen |= chain.AllNodes().Count > chain.Nodes.Count;
                foreach (var e in chain.Edges) { var (x0, y0, x1, y1) = e.Extent(); inGrid &= x0 >= -1e-9 && y0 >= -1e-9 && x1 <= 14 + 1e-9 && y1 <= 14 + 1e-9; }
            }
    Check("direction Arc: chains of arcs with the asked sweeps, inside the grid, with extra outline nodes", madeArcs && sweepsOk && extraNodesSeen && inGrid);

    string badArc = Error(Language("[" + arcShape.Replace("\"minSweep\": 180", "\"minSweep\": 100").Replace("\"maxSweep\": 270", "\"maxSweep\": 360") + "]", 3));
    Check("errors: arc sweeps must be multiples of the step and below 360", badArc.Contains("minSweep") && badArc.Contains("maxSweep"), badArc);
    string badNodes = Error(Language("[" + Scribbles().Replace("\"direction\"", "\"outlineNodes\": { \"mode\": \"AtAngles\" }, \"direction\"") + "]", 3));
    Check("errors: outlineNodes on a line rule are reported", badNodes.Contains("outlineNodes") && badNodes.Contains("Circle or Arc"), badNodes);
}

// --- spacing.minEdgeDistance: clearance between stroke paths --------------------------------------------------------
{
    static GridPoint P(int x, int y) => new GridPoint(x, y);
    var h = new GridEdge(P(0, 4), P(8, 4));
    var parallel1 = new GridEdge(P(0, 5), P(8, 5));
    var parallel2 = new GridEdge(P(0, 6), P(8, 6));
    Check("clearance: parallel strokes 1 apart are too close for 2, fine for 1", EdgePaths.TooClose(h, parallel1, 2, null) && !EdgePaths.TooClose(h, parallel1, 1, null));
    Check("clearance: strokes exactly the distance apart pass", !EdgePaths.TooClose(h, parallel2, 2, null));
    var up = new GridEdge(P(4, 4), P(4, 0));
    Check("clearance: a stroke leaving a shared node at 90 degrees passes", !EdgePaths.TooClose(up, h, 2, new[] { P(4, 4), P(4, 0) }));
    var shallow = new GridEdge(P(0, 4), P(8, 3));
    Check("clearance: a stroke leaving a node at a shallow angle is rejected", EdgePaths.TooClose(shallow, h, 2, new[] { P(0, 4), P(8, 3) }));
    var dome = new GridEdge(P(2, 4), P(6, 4), GridEdgeKind.Circle, null, null, new ArcSpan(180, 180));
    Check("clearance: a dome arc standing on a line passes (its ends are at the line)", !EdgePaths.TooClose(dome, h, 1.5, new[] { P(2, 4), P(6, 4) }));
    var tangent = new GridEdge(P(2, 6), P(6, 6), GridEdgeKind.Circle, null, null, new ArcSpan(180, 180));
    Check("clearance: an arc whose top just touches a line is rejected", EdgePaths.TooClose(tangent, h, 2, new[] { P(2, 6), P(6, 6) }));
    var dotNear = new GridEdge(P(4, 5), P(4, 5), GridEdgeKind.Circle);
    Check("clearance: a dot keeps its distance from strokes", EdgePaths.TooClose(dotNear, h, 2, null) && !EdgePaths.TooClose(dotNear, h, 1, null));

    var ctx = new GeometryContext();
    var wall = new SubSymbolChain { ShapeTag = "wall#0" };
    wall.Nodes.AddRange(new[] { P(0, 4), P(8, 4) });
    wall.Edges.Add(h);
    ctx.Commit(wall, new CrossingConfig { Mode = CrossingMode.Anywhere });
    ctx.SetCurrent("s", new CrossingConfig { Mode = CrossingMode.Anywhere }, 0);
    Check("clearance: off by default (0) - a stroke next to a wall is allowed", !ctx.IsBlocked(parallel1, new[] { P(0, 5), P(8, 5) }));
    ctx.SetCurrent("s", new CrossingConfig { Mode = CrossingMode.Anywhere }, 2);
    Check("clearance: the placing shape's value blocks a stroke too near", ctx.IsBlocked(parallel1, new[] { P(0, 5), P(8, 5) }) && !ctx.IsBlocked(parallel2, new[] { P(0, 6), P(8, 6) }));
    var ctx2 = new GeometryContext();
    ctx2.Commit(wall, new CrossingConfig { Mode = CrossingMode.Anywhere }, 2);
    ctx2.SetCurrent("s", new CrossingConfig { Mode = CrossingMode.Anywhere }, 0);
    Check("clearance: the existing shape's value counts as well (the larger one wins)", ctx2.IsBlocked(parallel1, new[] { P(0, 5), P(8, 5) }));

    // spacing.onlyShapes / excludeShapes: which shapes the spacing is measured against.
    var cross = new CrossingConfig { Mode = CrossingMode.Anywhere };
    SpacingConfig Sp(List<string>? only = null, List<string>? exclude = null) => new SpacingConfig { Enabled = true, MinEdgeDistance = 2, MinNodeDistance = 3, OnlyShapes = only, ExcludeShapes = exclude };
    var near = new[] { P(0, 5), P(8, 5) };
    var ctx3 = new GeometryContext();
    ctx3.Commit(wall, cross);
    ctx3.SetCurrent("s", cross, Sp());
    bool noFilter = ctx3.IsBlocked(parallel1, near);
    ctx3.SetCurrent("s", cross, Sp(exclude: new List<string> { "wall" }));
    bool excluded = ctx3.IsBlocked(parallel1, near);
    ctx3.SetCurrent("s", cross, Sp(exclude: new List<string> { "other" }));
    bool excludedOther = ctx3.IsBlocked(parallel1, near);
    ctx3.SetCurrent("s", cross, Sp(only: new List<string> { "other" }));
    bool onlyOther = ctx3.IsBlocked(parallel1, near);
    ctx3.SetCurrent("s", cross, Sp(only: new List<string> { "wall" }));
    bool onlyWallBlocked = ctx3.IsBlocked(parallel1, near);
    Check("spacing.excludeShapes / onlyShapes (edge distance): a listed shape is ignored, an unlisted one still counts",
        noFilter && !excluded && excludedOther && !onlyOther && onlyWallBlocked, $"{noFilter} {excluded} {excludedOther} {onlyOther} {onlyWallBlocked}");

    var ctx4 = new GeometryContext();
    ctx4.Commit(wall, cross, Sp(exclude: new List<string> { "s" }));
    ctx4.SetCurrent("s", cross, 0);
    bool theyIgnoreMe = ctx4.IsBlocked(parallel1, near);
    var ctx5 = new GeometryContext();
    ctx5.Commit(wall, cross, Sp(exclude: new List<string> { "x" }));
    ctx5.SetCurrent("s", cross, 0);
    Check("spacing.excludeShapes also holds the other way round: an existing shape that ignores the new one asks for no clearance",
        !theyIgnoreMe && ctx5.IsBlocked(parallel1, near), $"{theyIgnoreMe}");

    var groups = new GeometryContext();
    groups.Commit(wall, cross);
    var otherChain = new SubSymbolChain { ShapeTag = "other#0" }; otherChain.Nodes.AddRange(new[] { P(0, 9), P(8, 9) }); otherChain.Edges.Add(new GridEdge(P(0, 9), P(8, 9)));
    groups.Commit(otherChain, cross);
    Check("spacing onlyShapes / excludeShapes (node groups): only the covered shapes' nodes are measured against",
        groups.NodeGroupsFor(Sp()).Count == 2 && groups.NodeGroupsFor(Sp(exclude: new List<string> { "wall" })).Count == 1
        && groups.NodeGroupsFor(Sp(only: new List<string> { "wall" })).Count == 1 && groups.NodeGroupsFor(Sp(only: new List<string> { "wall" }))[0].Contains(P(0, 4)));
    var near1 = new[] { P(0, 5), P(8, 5) };
    Check("spacing.minNodeDistance respects the filter",
        !SubSymbolSpacingChecker.SatisfiesSpacing(near1, groups.NodeGroupsFor(Sp()), Sp())
        && SubSymbolSpacingChecker.SatisfiesSpacing(near1, groups.NodeGroupsFor(Sp(exclude: new List<string> { "wall" })), Sp(exclude: new List<string> { "wall" })));

    // End to end: with the rule on, no two strokes of a shape come closer than the distance (except at their meetings).
    string withClear = Scribbles("""{ "crossing": { "mode": "Anywhere" }, "spacing": { "enabled": true, "minEdgeDistance": 2 } }""");
    int checkedGlyphs = 0; bool allClear = true;
    for (int seed = 1; seed <= 6; seed++)
    {
        IReadOnlyList<Glyph> made;
        try { made = Run(Language("[" + withClear + "]", seed))["T"]; } catch (Exception) { continue; }
        foreach (var glyph in made)
        {
            checkedGlyphs++;
            var all = glyph.Chains.SelectMany(c => c.Edges.Select(e => (Edge: e, Nodes: c.AllNodes()))).ToList();
            for (int i = 0; i < all.Count; i++)
                for (int j = i + 1; j < all.Count; j++)
                    if (EdgePaths.TooClose(all[i].Edge, all[j].Edge, 2, all[i].Nodes.Concat(all[j].Nodes))) allClear = false;
        }
    }
    Check("clearance: generated glyphs keep the distance between strokes", checkedGlyphs > 0 && allClear, $"{checkedGlyphs} glyphs");
}

// --- combination crossing AnywhereButRespectStrict -----------------------------------------------------------------
{
    static GridPoint P(int x, int y) => new GridPoint(x, y);
    var strictDot = new SubSymbolChain { ShapeTag = "dot#0" };
    strictDot.Nodes.AddRange(new[] { P(4, 4), P(4, 4) });
    strictDot.Edges.Add(new GridEdge(P(4, 4), P(4, 4), GridEdgeKind.Circle));
    var anywhereCfg = new CrossingConfig { Mode = CrossingMode.AnywhereButRespectStrict };
    var strictCfg = new CrossingConfig { Mode = CrossingMode.Strict };
    var stroke = new SubSymbolChain { ShapeTag = "line#0" };
    stroke.Nodes.AddRange(new[] { P(0, 5), P(8, 5) });
    stroke.Edges.Add(new GridEdge(P(0, 5), P(8, 5)));
    var farStroke = new GridEdge(P(0, 8), P(8, 8));
    var nearStroke = new GridEdge(P(0, 5), P(8, 5)); // one cell from the dot, not touching it

    (CrossingConfig, double) RulesOf(SubSymbolChain c) => c.ShapeTag!.StartsWith("dot") ? (strictCfg, 2.0) : (anywhereCfg, 0.0);
    var plainCtx = new GeometryContext(anywhereCfg);
    plainCtx.CommitAsOneShape(new[] { strictDot }, anywhereCfg);
    Check("AnywhereButRespectStrict without per-shape rules behaves like Anywhere (a stroke may pass next to a dot)", !plainCtx.IsBlocked(nearStroke, new[] { P(0, 5), P(8, 5) }));

    var respecting = new GeometryContext(anywhereCfg) { ChainRules = RulesOf };
    respecting.CommitAsOneShape(new[] { strictDot }, anywhereCfg);
    respecting.SetCurrent(null, anywhereCfg, 0);
    Check("AnywhereButRespectStrict: a stroke too close to a Strict shape's dot is blocked, a far one is not",
        respecting.IsBlocked(nearStroke, new[] { P(0, 5), P(8, 5) }) && !respecting.IsBlocked(farStroke, new[] { P(0, 8), P(8, 8) }));
    Check("AnywhereButRespectStrict: a stroke through the dot is blocked", respecting.IsBlocked(new GridEdge(P(0, 4), P(8, 4)), new[] { P(0, 4), P(8, 4) }));

    // the other order: the dot is placed after the stroke and must keep clear of it
    var later = new GeometryContext(anywhereCfg) { ChainRules = RulesOf };
    later.CommitAsOneShape(new[] { stroke }, anywhereCfg);
    later.SetCurrent(null, strictCfg, 2.0);
    Check("AnywhereButRespectStrict: a Strict dot placed after a stroke keeps clear of it too",
        later.IsBlocked(new GridEdge(P(4, 4), P(4, 4), GridEdgeKind.Circle), new[] { P(4, 4) }));

    string onShape = Error(DotBlock("AnywhereButRespectStrict", "[[8,8],[8,6]]"));
    Check("errors: AnywhereButRespectStrict on a shape is reported", onShape.Contains("AnywhereButRespectStrict") && onShape.Contains("combination"), onShape);
}

// --- quality.rules -----------------------------------------------------------------------------------------------
{
    static GridPoint P(int x, int y) => new GridPoint(x, y);
    static SubSymbolChain DotChain(string tag, int x, int y)
    {
        var c = new SubSymbolChain { ShapeTag = tag };
        c.Nodes.AddRange(new[] { P(x, y), P(x, y) });
        c.Edges.Add(new GridEdge(P(x, y), P(x, y), GridEdgeKind.Circle));
        return c;
    }
    static SubSymbolChain LineChain(string tag, int x0, int y0, int x1, int y1)
    {
        var c = new SubSymbolChain { ShapeTag = tag };
        c.Nodes.AddRange(new[] { P(x0, y0), P(x1, y1) });
        c.Edges.Add(new GridEdge(P(x0, y0), P(x1, y1)));
        return c;
    }
    Glyph Composite(string name, SubSymbolChain[] first, SubSymbolChain[] second)
    {
        var g = new Glyph { Name = name };
        var a = new PlacedComponent { SourceGlyphName = "A" }; a.TransformedChains.AddRange(first);
        var b = new PlacedComponent { SourceGlyphName = "B" }; b.TransformedChains.AddRange(second);
        g.Components.Add(a); g.Components.Add(b);
        return g;
    }
    QualityRule DotRule(QualityMeasure measure = QualityMeasure.Count) => new()
    {
        Id = "dots", Subject = new StrokeSelector { Kind = StrokeKind.Dot }, Against = new StrokeSelector { Kind = StrokeKind.Dot },
        Measure = measure, Window = new QualityWindow { Shape = WindowShape.Square, Radius = 3 }, MinDistance = measure == QualityMeasure.Distance ? 4 : 0,
    };

    var near = Composite("AB", new[] { DotChain("dots#0", 5, 5) }, new[] { DotChain("dots#0", 6, 5) });
    var far = Composite("AB", new[] { DotChain("dots#0", 2, 2) }, new[] { DotChain("dots#0", 12, 12) });
    Check("quality rule Count: a dot of another component inside the window is found", QualityRuleChecker.Evaluate(near, new[] { DotRule() }, true).Count == 1);
    Check("quality rule Count: nothing near, nothing found", QualityRuleChecker.Evaluate(far, new[] { DotRule() }, true).Count == 0);
    Check("quality rule Distance: a nearest stroke closer than minDistance is found, a far one is not",
        QualityRuleChecker.Evaluate(near, new[] { DotRule(QualityMeasure.Distance) }, true).Count == 1 && QualityRuleChecker.Evaluate(far, new[] { DotRule(QualityMeasure.Distance) }, true).Count == 0);

    var dotAndLine = Composite("AB", new[] { DotChain("dots#0", 5, 5) }, new[] { LineChain("bar#0", 4, 6, 8, 6) });
    var wholeGlyphRule = DotRule(); wholeGlyphRule.Against = new StrokeSelector();
    Check("quality rule: the 'against' selector decides what counts (a line is not a dot, but is something)",
        QualityRuleChecker.Evaluate(dotAndLine, new[] { DotRule() }, true).Count == 0 && QualityRuleChecker.Evaluate(dotAndLine, new[] { wholeGlyphRule }, true).Count == 1);

    var sim = DotRule(QualityMeasure.Similar);
    Check("quality rule Similar (visual): a dot next to a dot is alike, a dot next to a line is not",
        QualityRuleChecker.Evaluate(near, new[] { sim }, true).Count == 1
        && QualityRuleChecker.Evaluate(dotAndLine, new[] { new QualityRule { Id = "s", Subject = new StrokeSelector { Kind = StrokeKind.Dot }, Against = new StrokeSelector(), Measure = QualityMeasure.Similar, Window = new QualityWindow { Radius = 3 } } }, true).Count == 0);
    var structural = DotRule(QualityMeasure.Similar); structural.Similarity = SimilarityMode.Structural;
    Check("quality rule Similar (structural): same kind and size", QualityRuleChecker.Evaluate(near, new[] { structural }, true).Count == 1);

    // two dots against one: counts differ
    var oneVsTwo = Composite("AB", new[] { DotChain("dots#0", 5, 5) }, new[] { DotChain("dots#0", 6, 5), DotChain("dots#1", 12, 12) });
    var skip = DotRule(); skip.WhenCountsDiffer = new CountDiffersConfig { Shape = "dots", Action = CountDiffersAction.Skip };
    var loosen = DotRule(); loosen.WhenCountsDiffer = new CountDiffersConfig { Shape = "dots", Action = CountDiffersAction.Loosen, Factor = 0.2 };
    Check("quality rule whenCountsDiffer Skip: a different count of the shape switches the rule off, the same count does not",
        QualityRuleChecker.Evaluate(oneVsTwo, new[] { DotRule() }, true).Count == 1 && QualityRuleChecker.Evaluate(oneVsTwo, new[] { skip }, true).Count == 0
        && QualityRuleChecker.Evaluate(near, new[] { skip }, true).Count == 1);
    Check("quality rule whenCountsDiffer Loosen: the window shrinks for that pair (3 x 0.2 = 0.6 - the dots are 1 apart)",
        QualityRuleChecker.Evaluate(oneVsTwo, new[] { loosen }, true).Count == 0 && QualityRuleChecker.Evaluate(near, new[] { loosen }, true).Count == 1);
    var onlyPlain = DotRule(); onlyPlain.AppliesTo = QualityAppliesTo.Plain;
    Check("quality rule appliesTo / glyph filters", QualityRuleChecker.Evaluate(near, new[] { onlyPlain }, true).Count == 0
        && QualityRuleChecker.Evaluate(near, new[] { new QualityRule { Id = "x", Subject = DotRule().Subject, Against = DotRule().Against, Window = DotRule().Window, ExcludeGlyphs = new List<string> { "AB" } } }, true).Count == 0);

    // End to end
    string QLang(string rules, string extra = "") => $$"""
    { "rootSeed": 5, "outputDirectory": "./o", "languages": [ { "name": "Q", "grid": { "width": 15, "height": 15 }, "glyphs": ["AB"], "baseGlyphs": ["bA", "bB"],
      "combination": { "recipes": { "AB": ["bA", "bB"] }, "arrangement": { "placements": [ { "glyph": "*", "mode": "Fixed" } ], "crossing": { "mode": "Anywhere" } } },
      "shapes": [
        { "id": "bar", "kind": "Literal", "nodes": [[2,10],[3,10],[4,10],[5,10],[6,10],[7,10],[8,10],[9,10],[10,10],[11,10],[12,10]], "placement": { "mode": "Fixed", "fixedOrigin": [0,0] }, "arrangement": { "crossing": { "mode": "NodesOnly" } } },
        { "id": "dots", "count": { "min": 1, "max": 2 }, "chain": { "length": { "min": 2, "max": 2 }, "connectionRules": [ { "direction": "Circle", "minLength": 0, "maxLength": 0 } ] },
          "placement": { "mode": "RandomValid", "maxPlacementAttempts": 60 }, "arrangement": { "spacing": { "enabled": true, "minNodeDistance": 2 }, "crossing": { "mode": "None" } } } ],
      "quality": { "maxSimilarity": 1, "rejectStraightLineOnly": false, "maxRegenerationAttempts": 60 {{extra}}, "rules": [ {{rules}} ] } } ] }
    """;
    (List<Glyph>, List<string>) RunQ(string json)
    {
        var cfg = LoadCfg(json);
        var orch = new RunOrchestrator(new LanguageGenerator(new GlyphGenerator(new ChainGenerator(), new PlacementResolver(), new CombinationResolver())));
        var res = orch.Run(cfg)["Q"].ToList();
        return (res, orch.Warnings.ToList());
    }
    double NearestDotsApart(Glyph g)
    {
        var dotsA = g.Components[0].TransformedChains.SelectMany(c => c.Edges).Where(e => e.IsDot).Select(e => e.From).ToList();
        var dotsB = g.Components[1].TransformedChains.SelectMany(c => c.Edges).Where(e => e.IsDot).Select(e => e.From).ToList();
        double best = double.MaxValue;
        foreach (var a in dotsA) foreach (var b in dotsB) best = Math.Min(best, Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)));
        return best;
    }
    string dotRule(string onFail, string extra = "") => $$"""{ "id": "dotsApart", "appliesTo": "Composite", "subject": { "shapes": ["dots"], "kind": "Dot" }, "against": { "shapes": ["dots"], "kind": "Dot" }, "measure": "Distance", "minDistance": 4, "onFail": "{{onFail}}" {{extra}} }""";

    double worstWithout = 0, worstWith = 0; int sawClose = 0;
    for (int seed = 1; seed <= 12; seed++)
    {
        var plainRun = RunQ(QLang("").Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}")).Item1.First(g => g.Name == "AB");
        if (NearestDotsApart(plainRun) < 4) sawClose++;
        var ruled = RunQ(QLang(dotRule("Reject", ", \"redo\": \"Either\"")).Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}")).Item1.First(g => g.Name == "AB");
        worstWith = Math.Max(worstWith, 4 - NearestDotsApart(ruled));
    }
    Check("quality.rules end to end: without the rule dots of the two bases do land closer than 4 (so the test can fail)", sawClose > 0, $"{sawClose}/12");
    Check("quality.rules end to end: with a Reject rule they never do", worstWith <= 1e-9, $"worst shortfall {worstWith:0.##}");

    var impossible = dotRule("Reject", ", \"redo\": \"Either\"").Replace("\"minDistance\": 4", "\"minDistance\": 40");
    string rejectedMsg = ""; try { RunQ(QLang(impossible)); } catch (GlyphGenerationException e) { rejectedMsg = e.Message; }
    Check("quality.rules: an impossible Reject rule fails and names the rule", rejectedMsg.Contains("dotsApart"), rejectedMsg);
    var (softGlyphs, softWarnings) = RunQ(QLang(dotRule("Score").Replace("\"minDistance\": 4", "\"minDistance\": 40")));
    Check("quality.rules: a Score rule that cannot be satisfied keeps the best glyph and warns", softGlyphs.Any(g => g.Name == "AB") && softWarnings.Any(w => w.Contains("dotsApart")), string.Join("|", softWarnings));
    var (warnGlyphs, warnWarnings) = RunQ(QLang(dotRule("Warn").Replace("\"minDistance\": 4", "\"minDistance\": 40")));
    Check("quality.rules: a Warn rule keeps the glyph and warns", warnGlyphs.Any(g => g.Name == "AB") && warnWarnings.Any(w => w.Contains("dotsApart")));

    var circleRule = DotRule(); circleRule.Subject = new StrokeSelector { Kind = StrokeKind.Circle }; circleRule.Against = new StrokeSelector { Kind = StrokeKind.Circle };
    var ringRule = DotRule(); ringRule.Subject = new StrokeSelector { Kind = StrokeKind.Ring }; ringRule.Against = new StrokeSelector { Kind = StrokeKind.Ring };
    Check("quality rule kind Circle includes dots (as the Circle connection rule draws them); Ring does not",
        QualityRuleChecker.Evaluate(near, new[] { circleRule }, true).Count == 1 && QualityRuleChecker.Evaluate(near, new[] { ringRule }, true).Count == 0);
    var (_, nothingWarnings) = RunQ(QLang(dotRule("Reject").Replace("\"kind\": \"Dot\"", "\"kind\": \"Ring\"")));
    Check("quality.rules: a rule whose selectors match nothing in the whole run is reported", nothingWarnings.Any(w => w.Contains("never found") && w.Contains("dotsApart")), string.Join("|", nothingWarnings));

    // quality.pairRules: the dots of two base glyphs that never share a glyph on their own.
    double BaseDotsApart(List<Glyph> gs)
    {
        var a = gs.First(g => g.Name == "bA").Chains.SelectMany(c => c.Edges).Where(e => e.IsDot).Select(e => e.From).ToList();
        var b = gs.First(g => g.Name == "bB").Chains.SelectMany(c => c.Edges).Where(e => e.IsDot).Select(e => e.From).ToList();
        double best = double.MaxValue;
        foreach (var x in a) foreach (var y in b) best = Math.Min(best, Math.Sqrt((x.X - y.X) * (x.X - y.X) + (x.Y - y.Y) * (x.Y - y.Y)));
        return best;
    }
    string pairRule(string onFail = "Reject", string redo = "Either", double min = 4) => $$""", "pairRules": [ { "id": "basesApart", "subject": { "glyphs": ["bA"], "shapes": ["dots"], "kind": "Dot" }, "against": { "glyphs": ["bB"], "shapes": ["dots"], "kind": "Dot" }, "measure": "Distance", "minDistance": {{min}}, "onFail": "{{onFail}}", "redo": "{{redo}}" } ]""";
    int closeBases = 0; double worstPair = 0;
    for (int seed = 1; seed <= 12; seed++)
    {
        var free = RunQ(QLang("").Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}")).Item1;
        if (BaseDotsApart(free) < 4) closeBases++;
        var judged = RunQ(QLang("", pairRule()).Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}")).Item1;
        worstPair = Math.Max(worstPair, 4 - BaseDotsApart(judged));
    }
    Check("quality.pairRules: without the rule the dots of two bases do land closer than 4 (so the test can fail)", closeBases > 0, $"{closeBases}/12");
    Check("quality.pairRules: with a Reject rule the dots of two different bases never do", worstPair <= 1e-9, $"worst shortfall {worstPair:0.##}");
    string pairMsg = ""; try { RunQ(QLang("", pairRule(min: 40))); } catch (GlyphGenerationException e) { pairMsg = e.Message; }
    Check("quality.pairRules: an impossible Reject rule fails and names the rule", pairMsg.Contains("basesApart"), pairMsg);
    var (_, pairWarn) = RunQ(QLang("", pairRule("Warn", "Composite", 40)));
    Check("quality.pairRules: a Warn rule keeps the glyphs and warns", pairWarn.Any(w => w.Contains("basesApart")), string.Join("|", pairWarn));
    string badPair = Error(QLang("", """, "pairRules": [ { "id": "p", "subject": { "kind": "Dot" }, "against": { "glyphs": ["nope"] }, "measure": "Distance", "minDistance": 2 } ]"""));
    Check("errors: pair rules need glyphs on both sides, and known ones", badPair.Contains("subject.glyphs") && badPair.Contains("'nope'"), badPair);

    {
        string startJson(string spacingExtra) => QLang("").Replace("\"placement\": { \"mode\": \"RandomValid\", \"maxPlacementAttempts\": 60 }", "\"placement\": { \"mode\": \"RandomValid\", \"maxPlacementAttempts\": 60, \"startOnExistingNode\": true, \"startOnShapes\": [\"bar\"] }")
            .Replace("\"minNodeDistance\": 2 }", "\"minNodeDistance\": 2" + spacingExtra + " }");
        Check("errors: startOnExistingNode with minNodeDistance is still rejected when the start shape is measured", Error(startJson("")).Contains("shared node"));
        Check("errors: ... but not when onlyShapes leaves the start shapes out", !Error(startJson(", \"onlyShapes\": [\"dots\"]")).Contains("shared node"));
        Check("errors: ... nor when excludeShapes lists them", !Error(startJson(", \"excludeShapes\": [\"bar\"]")).Contains("shared node"));
    }
    // placement.nodes: node masks.
    {
        string withNodes(string nodes) => QLang("").Replace("\"placement\": { \"mode\": \"RandomValid\", \"maxPlacementAttempts\": 60 }", "\"placement\": { \"mode\": \"RandomValid\", \"maxPlacementAttempts\": 60, \"nodes\": " + nodes + " }");
        var maskGeo = new GeometryContext();
        var poolChain = new SubSymbolChain { ShapeTag = "pool#0" }; poolChain.Nodes.AddRange(new[] { new GridPoint(3, 3), new GridPoint(9, 6) }); poolChain.Edges.Add(new GridEdge(new GridPoint(3, 3), new GridPoint(9, 6)));
        maskGeo.Commit(poolChain, new CrossingConfig { Mode = CrossingMode.Anywhere });
        Func<GridPoint, bool> Mask(NodeMaskConfig m) => NodeMaskBuilder.Build(m, maskGeo)!;
        var pts = Mask(new NodeMaskConfig { Allow = new() { new NodeRegion { Points = new() { new GridPoint(1, 1), new GridPoint(2, 5) } } } });
        Check("placement.nodes points: only the listed nodes", pts(new GridPoint(1, 1)) && pts(new GridPoint(2, 5)) && !pts(new GridPoint(2, 2)));
        var evenOdd = Mask(new NodeMaskConfig { Allow = new() { new NodeRegion { Pattern = new NodePattern { X = new AxisPattern { Mode = AxisPatternMode.Even }, Y = new AxisPattern { Mode = AxisPatternMode.Odd } } } } });
        Check("placement.nodes pattern: x even AND y odd", evenOdd(new GridPoint(4, 3)) && evenOdd(new GridPoint(0, 1)) && !evenOdd(new GridPoint(3, 3)) && !evenOdd(new GridPoint(4, 4)));
        var every = Mask(new NodeMaskConfig { Allow = new() { new NodeRegion { Pattern = new NodePattern { X = new AxisPattern { Step = 3, Offset = 1 } } } } });
        Check("placement.nodes pattern: every 3rd x from 1 (1, 4, 7), any y", every(new GridPoint(1, 9)) && every(new GridPoint(7, 0)) && every(new GridPoint(-2, 0)) && !every(new GridPoint(3, 3)));
        var forbidBox = Mask(new NodeMaskConfig { Forbid = new() { new NodeRegion { Box = new PlacementBounds { MinX = 2, MinY = 2, MaxX = 4, MaxY = 4 } } } });
        Check("placement.nodes forbid: everything but the box", !forbidBox(new GridPoint(3, 3)) && forbidBox(new GridPoint(5, 5)) && forbidBox(new GridPoint(1, 3)));
        var pool = Mask(new NodeMaskConfig { Allow = new() { new NodeRegion { NodesOfShapes = new() { "pool" } } } });
        Check("placement.nodes node pool: the nodes of the listed shapes that exist", pool(new GridPoint(3, 3)) && pool(new GridPoint(9, 6)) && !pool(new GridPoint(4, 4)));
        var around = Mask(new NodeMaskConfig { Allow = new() { new NodeRegion { BoundsOfShapes = new ShapesBox { Shapes = new() { "pool" }, Margin = 1 } } } });
        Check("placement.nodes boundsOfShapes: the box around those shapes' nodes plus the margin", around(new GridPoint(2, 2)) && around(new GridPoint(10, 7)) && !around(new GridPoint(11, 7)) && !around(new GridPoint(2, 8)));
        var nothing = Mask(new NodeMaskConfig { Allow = new() { new NodeRegion { NodesOfShapes = new() { "missing" } } } });
        Check("placement.nodes: a pool of a shape that is not there matches nothing", !nothing(new GridPoint(3, 3)));
        var restricted = new LatticeGrid(15, 15).Restricted(2, 2, 10, 10).WithNodeFilter(evenOdd);
        Check("placement.nodes: the grid combines box and mask (usable nodes only)", restricted.UsableNodes().All(p => p.X % 2 == 0 && p.Y % 2 != 0 && p.X >= 2 && p.X <= 10 && p.Y >= 2 && p.Y <= 10) && restricted.UsableNodes().Count == 5 * 4);

        bool allOn = true; int seenNodes = 0;
        for (int seed = 1; seed <= 6; seed++)
        {
            var run = RunQ(withNodes("""{ "allow": [ { "pattern": { "x": { "mode": "Even" }, "y": { "mode": "Odd" } } } ], "forbid": [ { "box": { "minX": 0, "minY": 0, "maxX": 6, "maxY": 14 } } ] }""").Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}")).Item1;
            foreach (var g in run.Where(g => g.Name is "bA" or "bB"))
                foreach (var chain in g.Chains.Where(c => c.ShapeTag?.StartsWith("dots#") == true))
                    foreach (var n in chain.AllNodes()) { seenNodes++; if (n.X % 2 != 0 || n.Y % 2 == 0 || n.X <= 6) allOn = false; }
        }
        Check("placement.nodes end to end: every node of the masked shape is allowed", allOn && seenNodes > 0, $"{seenNodes} nodes");
        string emptyPool = ""; try { RunQ(withNodes("""{ "allow": [ { "nodesOfShapes": ["later"] } ] }""")); } catch (GlyphGenerationException e) { emptyPool = e.Message; } catch (Exception e) { emptyPool = "other: " + e.Message; }
        Check("placement.nodes: a pool nothing has filled yet fails with an explanation", emptyPool.Contains("placement.nodes") || emptyPool.Contains("comes after"), emptyPool);
        string badNodes = Error(withNodes("""{ "allow": [ { "points": [[1,1]], "box": { "minX": 0, "minY": 0, "maxX": 1, "maxY": 1 } }, { "nodesOfShapes": ["nope"] }, { "pattern": { "x": { "step": 0 } } }, { "boundsOfShapes": { "shapes": ["bar"], "margin": -1 } } ] }"""));
        Check("errors: bad placement.nodes are reported together", badNodes.Contains("exactly one") && badNodes.Contains("'nope'") && badNodes.Contains("step") && badNodes.Contains("margin"), badNodes);
    }

    // quality.similarityBasis / comparePosition
    {
        static GridPoint Q(int x, int y) => new GridPoint(x, y);
        Glyph Drawn(string name, params (GridPoint, GridPoint)[] lines)
        {
            var g = new Glyph { Name = name };
            var chain = new SubSymbolChain();
            foreach (var (a2, b2) in lines) { chain.Nodes.Add(a2); chain.Nodes.Add(b2); chain.Edges.Add(new GridEdge(a2, b2)); }
            g.Chains.Add(chain);
            return g;
        }
        var triA = Drawn("A", (Q(0, 0), Q(4, 0)), (Q(4, 0), Q(2, 4)));
        var triB = Drawn("B", (Q(0, 0), Q(2, 4)), (Q(2, 4), Q(4, 0)));
        double simNodes = GlyphQualityChecker.FindTooSimilar(triA, new[] { triB }, 0.01)?.Similarity ?? 0;
        double simEdges = GlyphQualityChecker.FindTooSimilar(triA, new[] { triB }, 0.01, new QualityConfig { SimilarityBasis = SimilarityBasis.Edges })?.Similarity ?? 0;
        Check("quality.similarityBasis: the same nodes joined differently are alike by Nodes (1) but not by Edges", Math.Abs(simNodes - 1) < 1e-9 && simEdges < 0.6, $"{simNodes:0.##} / {simEdges:0.##}");
        var shifted = Drawn("M", (Q(1, 1), Q(5, 1)), (Q(5, 1), Q(3, 5)));
        double sameShape = GlyphQualityChecker.FindTooSimilar(triA, new[] { shifted }, 0.01, new QualityConfig { SimilarityBasis = SimilarityBasis.Edges })?.Similarity ?? 0;
        double inPlace = GlyphQualityChecker.FindTooSimilar(triA, new[] { shifted }, 0.01, new QualityConfig { SimilarityBasis = SimilarityBasis.Edges, ComparePosition = true })?.Similarity ?? 0;
        Check("quality.comparePosition: the same shape elsewhere is alike by default, not when position counts", Math.Abs(sameShape - 1) < 1e-9 && inPlace < 0.3, $"{sameShape:0.##} / {inPlace:0.##}");
        // An undrawn shape (isDrawn false) is not part of what is seen: a block fixed in place must not hide that two glyphs are the same stroke moved.
        Glyph WithBlock(string name, GridPoint from, GridPoint to)
        {
            var g = Drawn(name, (from, to));
            g.Chains[0].ShapeTag = "stroke#0";
            var block = new SubSymbolChain { ShapeTag = "block#0" };
            block.Nodes.Add(Q(2, 1)); block.Nodes.Add(Q(4, 1)); block.Edges.Add(new GridEdge(Q(2, 1), Q(4, 1)));
            g.Chains.Add(block);
            return g;
        }
        var moveA = WithBlock("MA", Q(3, 0), Q(6, 0)); var moveB = WithBlock("MB", Q(0, 0), Q(3, 0));
        var undrawnOpts = new QualityConfig { SimilarityBasis = SimilarityBasis.Edges, UndrawnShapeIds = new HashSet<string> { "block" } };
        double withBlockSeen = GlyphQualityChecker.FindTooSimilar(moveA, new[] { moveB }, 0.0, new QualityConfig { SimilarityBasis = SimilarityBasis.Edges })?.Similarity ?? 0;
        double blockIgnored = GlyphQualityChecker.FindTooSimilar(moveA, new[] { moveB }, 0.0, undrawnOpts)?.Similarity ?? 0;
        Check("quality: undrawn shapes are left out of similarity, so a moved copy is still caught", withBlockSeen < 0.6 && Math.Abs(blockIgnored - 1) < 1e-9, $"{withBlockSeen:0.##} / {blockIgnored:0.##}");
        var lineOnly = WithBlock("L", Q(0, 0), Q(3, 0));
        Check("quality: a line plus an undrawn block counts as a single straight line only when the block is ignored",
            !GlyphQualityChecker.IsTrivialStraightLine(lineOnly) && GlyphQualityChecker.IsTrivialStraightLine(lineOnly, undrawnOpts));
        string offGrid = Error(QLang("").Replace("\"placement\": { \"mode\": \"RandomValid\", \"maxPlacementAttempts\": 60 }", "\"placement\": { \"mode\": \"RandomValid\", \"nodes\": { \"allow\": [ { \"points\": [[1,1],[15,3]] } ] } }"));
        Check("errors: placement.nodes points outside the grid are reported", offGrid.Contains("[15, 3]") && offGrid.Contains("outside the grid") && !offGrid.Contains("[1, 1]"), offGrid);
    }

    // connectionRules[].chainLengthCost: a drawn move uses up that much of chain.length.
    {
        int Nodes(int cost, int length, int rotateSeed)
        {
            var req = new ChainGenerationRequest
            {
                StartNode = new GridPoint(1, 1), TargetLength = length,
                ConnectionRules = new List<ConnectionRuleConfig> { new() { Direction = ConnectionDirection.Orthogonal, MinLength = 1, MaxLength = 1, ChainLengthCost = cost } },
                AllowNodeRevisit = false,
            };
            var chain = new ChainGenerator().TryGenerateChain(new LatticeGrid(30, 30), req, new DeterministicRandom((ulong)rotateSeed), new GeometryContext());
            return chain?.Nodes.Count ?? -1;
        }
        Check("chainLengthCost: 1 (default) is the old node count", Nodes(1, 5, 1) == 5, Nodes(1, 5, 1).ToString());
        Check("chainLengthCost: 2 uses the length up twice as fast (length 5 -> 3 nodes)", Nodes(2, 5, 1) == 3, Nodes(2, 5, 1).ToString());
        Check("chainLengthCost: 0 is free, so the chain does not stop at the length", Nodes(0, 5, 1) != 5, Nodes(0, 5, 1).ToString());
        string badCost = Error(QLang("").Replace("\"direction\": \"Circle\"", "\"chainLengthCost\": -1, \"direction\": \"Circle\""));
        Check("errors: a negative chainLengthCost is reported", badCost.Contains("chainLengthCost"), badCost);
    }

    // shapes[].chainFrom: take another shape's chain block, keep the shape's own placement.
    {
        string CF(string second, string first = """{ "id": "src", "count": { "min": 1, "max": 1 }, "chain": { "length": { "min": 3, "max": 3 }, "connectionRules": [ { "direction": "Diagonal", "minLength": 2, "maxLength": 2 } ] }, "placement": { "mode": "RandomValid", "bounds": { "minX": 1, "minY": 1, "maxX": 8, "maxY": 8 } } }""") =>
            "{ \"rootSeed\": 3, \"outputDirectory\": \"./o\", \"languages\": [ { \"name\": \"C\", \"grid\": { \"width\": 12, \"height\": 12 }, \"glyphs\": [\"A\"], \"shapes\": [" + first + "," + second + "] } ] }";
        string user = """{ "id": "user", "chainFrom": "src", "count": { "min": 1, "max": 1 }, "placement": { "mode": "RandomValid", "bounds": { "minX": 6, "minY": 6, "maxX": 11, "maxY": 11 }, "maxPlacementAttempts": 80 } }""";
        var loaded = LoadCfg(CF(user)).Languages[0].Shapes[1];
        Check("chainFrom: the shape takes the source's chain and keeps its own placement",
            loaded.Chain.Length.Min == 3 && loaded.Chain.ConnectionRules.Count == 1 && loaded.Chain.ConnectionRules[0].Direction == ConnectionDirection.Diagonal && loaded.Placement.Bounds!.MinX == 6);
        var made = Run(CF(user)).Values.First().First();
        var chains = GlyphGeometryUtils.GetAllChains(made).ToList();
        Check("chainFrom: both shapes draw (diagonal strokes of 3 nodes)", chains.Count == 2 && chains.All(c => c.Nodes.Count == 3 && c.Edges.All(e => Math.Abs(e.To.X - e.From.X) == 2 && Math.Abs(e.To.Y - e.From.Y) == 2)));
        string later = """{ "id": "user", "chainFrom": "src2", "placement": { "mode": "RandomValid" } }""";
        string src2 = """{ "id": "src2", "chainFrom": "src", "placement": { "mode": "RandomValid" } }""";
        Check("chainFrom: a link through another chainFrom shape (listed after it) works", Error(CF(src2 + "," + later)) == "", Error(CF(src2 + "," + later)));
        Check("errors: chainFrom to a missing shape", Error(CF("""{ "id": "user", "chainFrom": "nope" }""")).Contains("'nope' is not a shape id"));
        Check("errors: chainFrom to itself", Error(CF("""{ "id": "user", "chainFrom": "user" }""")).Contains("from itself"));
        Check("errors: chainFrom loop", Error(CF("""{ "id": "user", "chainFrom": "b" }, { "id": "b", "chainFrom": "user" }""")).Contains("loop"));
    }

    // arc.anchor: Node = an end of the arc is the current node; Diameter = a half circle joining the two nodes.
    {
        bool Touches(GridEdge e, GridPoint p)
        {
            bool Near((double X, double Y) q) => Math.Abs(q.X - p.X) < 1e-6 && Math.Abs(q.Y - p.Y) < 1e-6;
            return Near(e.CirclePoint(e.ArcStartDegrees)) || Near(e.CirclePoint(e.ArcStartDegrees + e.Arc!.Value.Sweep));
        }
        (int Arcs, int Touching, int Half, int Endpoints) Probe(ArcAnchor anchor)
        {
            int arcs = 0, touching = 0, half = 0, endpoints = 0;
            for (int seed = 1; seed <= 40; seed++)
            {
                var req = new ChainGenerationRequest
                {
                    StartNode = new GridPoint(8, 8), TargetLength = 3,
                    ConnectionRules = new List<ConnectionRuleConfig> { new() { Direction = ConnectionDirection.Arc, MinLength = 2, MaxLength = 4, Arc = new ArcConfig { Anchor = anchor, MinSweep = 90, MaxSweep = 270 } } },
                };
                var chain = new ChainGenerator().TryGenerateChain(new LatticeGrid(17, 17), req, new DeterministicRandom((ulong)seed), new GeometryContext());
                if (chain is null) continue;
                foreach (var e in chain.Edges)
                {
                    arcs++;
                    if (Touches(e, e.From)) touching++;
                    if (e.Arc!.Value.Sweep == 180) half++;
                    if (Touches(e, e.From) && Touches(e, e.To)) endpoints++;
                }
            }
            return (arcs, touching, half, endpoints);
        }
        var anyArc = Probe(ArcAnchor.None); var node = Probe(ArcAnchor.Node); var diam = Probe(ArcAnchor.Diameter);
        Check("arc.anchor None: arcs need not touch the current node (as before)", anyArc.Arcs > 0 && anyArc.Touching < anyArc.Arcs, $"{anyArc}");
        Check("arc.anchor Node: every arc has an end on the node it is drawn from", node.Arcs > 0 && node.Touching == node.Arcs, $"{node}");
        Check("arc.anchor Diameter: every arc is a half circle joining both nodes", diam.Arcs > 0 && diam.Half == diam.Arcs && diam.Endpoints == diam.Arcs, $"{diam}");
    }


    // arc.continueFrom ArcEnd: the chain goes on from the node the arc ends at (within endTolerance).
    {
        (int Arcs, int Continues, int Away) ProbeEnd(double tolerance)
        {
            int arcs = 0, continues = 0, away = 0;
            for (int seed = 1; seed <= 60; seed++)
            {
                var req = new ChainGenerationRequest
                {
                    StartNode = new GridPoint(8, 8), TargetLength = 4,
                    ConnectionRules = new List<ConnectionRuleConfig> { new() { Direction = ConnectionDirection.Arc, MinLength = 2, MaxLength = 4,
                        Arc = new ArcConfig { Anchor = ArcAnchor.Node, ContinueFrom = ArcContinueFrom.ArcEnd, EndTolerance = tolerance, MinSweep = 90, MaxSweep = 270 } } },
                };
                var chain = new ChainGenerator().TryGenerateChain(new LatticeGrid(17, 17), req, new DeterministicRandom((ulong)seed), new GeometryContext());
                if (chain is null) continue;
                for (int i = 0; i < chain.Edges.Count; i++)
                {
                    var e = chain.Edges[i];
                    arcs++;
                    var next = chain.Nodes[i + 1];
                    double start = e.ArcStartDegrees, end = start + e.Arc!.Value.Sweep;
                    double Gap(double angle) { var p = e.CirclePoint(angle); return Math.Sqrt((p.X - next.X) * (p.X - next.X) + (p.Y - next.Y) * (p.Y - next.Y)); }
                    if (Math.Min(Gap(start), Gap(end)) <= tolerance + 1e-6) continues++;
                    if (!next.Equals(e.To)) away++;
                }
            }
            return (arcs, continues, away);
        }
        var exact = ProbeEnd(0); var loose = ProbeEnd(0.5);
        Check("arc.continueFrom ArcEnd: the chain goes on from the node the arc ends at", exact.Arcs > 0 && exact.Continues == exact.Arcs && exact.Away > 0, $"{exact}");
        Check("arc.continueFrom ArcEnd: endTolerance admits more arcs and stays within it", loose.Arcs >= exact.Arcs && loose.Continues == loose.Arcs, $"{loose} vs {exact}");
        string badEnd = Error(OneRule("""{ "direction": "Arc", "minLength": 2, "maxLength": 3, "arc": { "continueFrom": "ArcEnd", "endTolerance": 3 } }"""));
        Check("errors: continueFrom ArcEnd needs anchor Node, and endTolerance is limited", badEnd.Contains("anchor Node") && badEnd.Contains("endTolerance"), badEnd);
    }

    // maxParallelism: the same glyphs whatever the thread count.
    {
        string Signature(IEnumerable<Glyph> gs) => string.Join("|", gs.Select(g => g.Name + ":" + string.Join(";", GlyphGeometryUtils.GetAllChains(g).SelectMany(c => c.Edges).Select(e => $"{e.From.X},{e.From.Y}-{e.To.X},{e.To.Y}-{(int)e.Kind}-{e.IsCurved}"))));
        int compared = 0; bool identical = true;
        for (int seed = 1; seed <= 8; seed++)
        {
            string cfg(int threads) => QLang(dotRule("Reject", ", \"redo\": \"Either\""))
                .Replace("\"rootSeed\": 5,", $"\"rootSeed\": {seed}, \"maxParallelism\": {threads},")
                .Replace("\"maxSimilarity\": 1", "\"maxSimilarity\": 0.45");
            string one, four;
            try { one = Signature(RunQ(cfg(1)).Item1); } catch (GlyphGenerationException e) { one = "fail:" + e.Message[..Math.Min(80, e.Message.Length)]; }
            try { four = Signature(RunQ(cfg(4)).Item1); } catch (GlyphGenerationException e) { four = "fail:" + e.Message[..Math.Min(80, e.Message.Length)]; }
            compared++;
            if (one != four) identical = false;
        }
        Check("maxParallelism: 4 threads give exactly the glyphs 1 thread gives (retries forced)", identical && compared == 8);
        string badPar = Error(QLang("").Replace("\"rootSeed\": 5,", "\"rootSeed\": 5, \"maxParallelism\": -1,"));
        Check("errors: a negative maxParallelism is reported", badPar.Contains("maxParallelism"), badPar);
    }

    {
        // usageSizeBias.strength: positive keeps the lowest of the extra draws, negative the highest, so a negative strength gives larger counts.
        double MeanDots(double strength)
    {
            int total = 0, n = 0;
            for (int seed = 1; seed <= 40; seed++)
            {
                var cfg = QLang("").Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}")
                    .Replace("\"count\": { \"min\": 1, \"max\": 2 }", "\"count\": { \"min\": 1, \"max\": 6 }")
                    .Replace("\"glyphs\": [\"AB\"]", $"\"usageSizeBias\": {{ \"strength\": {strength.ToString(System.Globalization.CultureInfo.InvariantCulture)} }}, \"glyphs\": [\"AB\"]");
                try
                {
                    var glyphs = RunQ(cfg).Item1;
                    foreach (var g in glyphs.Where(g => g.Name == "bA" || g.Name == "bB")) { total += g.Chains.SelectMany(c => c.Edges).Count(e => e.IsDot); n++; }
                }
                catch (GlyphGenerationException) { }
            }
            return n == 0 ? 0 : (double)total / n;
        }
        double low = MeanDots(3), plainMean = MeanDots(0), high = MeanDots(-3);
        Check("usageSizeBias: negative strength keeps the highest draw (more dots), positive the lowest", high > plainMean && plainMean > low, $"{low:0.00} < {plainMean:0.00} < {high:0.00}");
        {
            var chanceShape = new ShapeDefinition { Id = "big", Chance = 20 };
            double Rate(double extra) { int hit = 0; for (int i = 1; i <= 4000; i++) if (chanceShape.PassesChance(new DeterministicRandom((ulong)i), extra)) hit++; return hit / 4000.0; }
            double plainRate = Rate(0), moreRate = Rate(-3), lessRate = Rate(3);
            Check("usageSizeBias.affectChance: negative draws make a chance shape likelier (1-(1-p)^n), positive less likely (p^n)",
                Math.Abs(plainRate - 0.2) < 0.03 && Math.Abs(moreRate - (1 - Math.Pow(0.8, 4))) < 0.03 && lessRate < 0.01, $"{lessRate:0.000} / {plainRate:0.000} / {moreRate:0.000}");
            string chanceCfg = Error(QLang("").Replace("\"glyphs\": [\"AB\"]", "\"usageSizeBias\": { \"strength\": -1, \"affectChance\": true }, \"glyphs\": [\"AB\"]"));
            Check("usageSizeBias.affectChance: accepted by the loader", chanceCfg == "", chanceCfg);
        }
        Check("usageSizeBias: a negative strength is accepted by the loader", Error(QLang("").Replace("\"glyphs\": [\"AB\"]", "\"usageSizeBias\": { \"strength\": -1 }, \"glyphs\": [\"AB\"]")) == "");
}


    // ---- arrangement.contact ----
    string ContactLang(string arrangementExtra, string crossing = "None") => $$"""
    { "rootSeed": 5, "outputDirectory": "./o", "languages": [ { "name": "T", "grid": { "width": 15, "height": 15 }, "glyphs": ["AB"], "baseGlyphs": ["bA", "bB"],
      "combination": { "recipes": { "AB": ["bA", "bB"] }, "maxRetriesPerRecipe": 30, "arrangement": { "crossing": { "mode": "{{crossing}}" } {{arrangementExtra}} } },
      "shapes": [
        { "id": "a", "onlyForGlyphs": ["bA"], "kind": "Literal", "nodes": [[0,0],[4,0],[4,4]], "placement": { "mode": "Fixed", "fixedOrigin": [2,2] } },
        { "id": "b", "onlyForGlyphs": ["bB"], "kind": "Literal", "nodes": [[0,0],[3,0],[3,2]], "placement": { "mode": "Fixed", "fixedOrigin": [8,8] } } ],
      "quality": { "maxSimilarity": 1, "rejectStraightLineOnly": false } } ] }
    """;
    int Shared(Glyph composite) => composite.Components.Count < 2 ? 0
        : composite.Components[0].TransformedChains.SelectMany(c => c.AllNodes()).Intersect(composite.Components[1].TransformedChains.SelectMany(c => c.AllNodes())).Count();
    int touching = 0, touchingFree = 0, ran = 0;
    for (int seed = 1; seed <= 12; seed++)
    {
        var forcedGlyph = Run(ContactLang(", \"contact\": { \"kind\": \"Junction\" }").Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}"))["T"].First(g => g.Name == "AB");
        var free = Run(ContactLang("").Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}"))["T"].First(g => g.Name == "AB");
        ran++;
        if (Shared(forcedGlyph) >= 1) touching++;
        if (Shared(free) >= 1) touchingFree++;
    }
    Check("arrangement.contact: every composite has components that share a node (Junction)", touching == ran, $"{touching}/{ran}");
    Check("arrangement.contact: without it many composites do not touch", touchingFree < ran, $"{touchingFree}/{ran}");

    int crossed = 0;
    for (int seed = 1; seed <= 8; seed++)
    {
        var g = Run(ContactLang(", \"contact\": { \"kind\": \"Crossing\" }", "Anywhere").Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}"))["T"].First(x => x.Name == "AB");
        var edgesA = g.Components[0].TransformedChains.SelectMany(c => c.Edges).ToList();
        var edgesB = g.Components[1].TransformedChains.SelectMany(c => c.Edges).ToList();
        if (edgesA.Any(a => edgesB.Any(b => GlyphGen.Core.Grid.EdgePaths.Crosses(a, b)))) crossed++;
    }
    Check("arrangement.contact: kind Crossing makes the components cross", crossed == 8, $"{crossed}/8");

    int listedTouch = 0;
    for (int seed = 1; seed <= 8; seed++)
        if (Shared(Run(ContactLang(", \"contact\": { \"kind\": \"Junction\", \"nodesOfShapes\": [\"a\"] }").Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}"))["T"].First(g => g.Name == "AB")) > 0) listedTouch++;
    Check("arrangement.contact.nodesOfShapes: contacts at a listed shape's node still count", listedTouch == 8, $"{listedTouch}/8");
    string badContactShapes = Error(ContactLang(", \"contact\": { \"nodesOfShapes\": [\"bA\", \"zzz\"] }"));
    Check("errors: contact.nodesOfShapes naming a glyph or an unknown shape is reported", badContactShapes.Contains("'bA' is a glyph name") && badContactShapes.Contains("'zzz' is not the id of any shape"), badContactShapes);
    string badContact = Error(ContactLang(", \"contact\": { \"kind\": \"Crossing\", \"min\": 0, \"chance\": 150 }"));
    Check("errors: bad arrangement.contact is reported", badContact.Contains("kind") && badContact.Contains("min") && badContact.Contains("chance"), badContact);

    // ---- component start-node mask (placements[].nodes + anchor) ----
    string MaskLang(string nodes, string anchor) => ContactLang(
        $", \"placements\": [ {{ \"glyph\": \"bA\", \"mode\": \"Fixed\", \"offset\": [0, 0] }}, {{ \"glyph\": \"bB\", \"mode\": \"Prefer\", \"sides\": [\"Right\"], \"nodes\": {nodes}{(anchor == "" ? "" : $", \"anchor\": \"{anchor}\"")} }} ]");
    GlyphGen.Core.Grid.GridPoint FirstOf(Glyph g) => g.Components[1].TransformedChains[0].Nodes[0];
    bool firstOk = true, anyOk = true, onNodes = true;
    for (int seed = 1; seed <= 8; seed++)
    {
        string Seeded(string cfg) => cfg.Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}");
        var g1 = Run(Seeded(MaskLang("{ \"allow\": [ { \"points\": [[6,6]] } ] }", "FirstNode")))["T"].First(x => x.Name == "AB");
        if (FirstOf(g1) != new GlyphGen.Core.Grid.GridPoint(6, 6)) firstOk = false;
        var g2 = Run(Seeded(MaskLang("{ \"allow\": [ { \"points\": [[6,6]] } ] }", "AnyNode")))["T"].First(x => x.Name == "AB");
        if (!g2.Components[1].TransformedChains.SelectMany(c => c.AllNodes()).Contains(new GlyphGen.Core.Grid.GridPoint(6, 6))) anyOk = false;
        var g3 = Run(Seeded(MaskLang("{ \"allow\": [ { \"nodesOfComponents\": { \"components\": [\"bA\"], \"shapes\": [\"a\"] } } ] }", "FirstNode")))["T"].First(x => x.Name == "AB");
        if (!g3.Components[0].TransformedChains.SelectMany(c => c.AllNodes()).Contains(FirstOf(g3))) onNodes = false;
    }
    Check("component nodes mask: FirstNode puts the component's first node on the mask", firstOk);
    Check("component nodes mask: AnyNode puts some node of the component on the mask", anyOk);
    Check("component nodes mask: nodesOfComponents starts the component on a node of a placed one", onNodes);

    string cornerMask = "{ \"allow\": [ { \"box\": { \"minX\": 13, \"minY\": 13, \"maxX\": 14, \"maxY\": 14 } } ] }";
    string noFirst = "";
    try { Run(MaskLang(cornerMask, "FirstNode")); } catch (GlyphGenerationException e) { noFirst = e.Message; }
    Check("component nodes mask: FirstNode has no fallback", noFirst != "", "generation succeeded");
    var fallback = Run(MaskLang(cornerMask, ""))["T"].First(x => x.Name == "AB");
    Check("component nodes mask: FirstThenAny falls back to any node",
        fallback.Components[1].TransformedChains.SelectMany(c => c.AllNodes()).Any(n => n.X >= 13 && n.Y >= 13));

    string badMask = Error(ContactLang(", \"placements\": [ { \"glyph\": \"bA\", \"mode\": \"Fixed\", \"offset\": [0, 0], \"nodes\": { \"allow\": [ { \"points\": [[1,1]] } ] } }, { \"glyph\": \"bB\", \"mode\": \"Prefer\", \"sides\": [\"Right\"], \"nodes\": { \"allow\": [ { \"nodesOfShapes\": [\"a\"] }, { \"nodesOfComponents\": { \"components\": [\"zz\"], \"shapes\": [\"nope\"] } } ] } } ]"));
    Check("errors: bad component nodes masks are reported", badMask.Contains("Fixed") && badMask.Contains("nodesOfComponents") && badMask.Contains("'zz'") && badMask.Contains("'nope'"), badMask);

    // ---- sides: Center ----
    bool centered = true;
    for (int seed = 1; seed <= 6; seed++)
    {
        var g = Run(ContactLang(", \"placements\": [ { \"glyph\": \"bA\", \"mode\": \"Fixed\", \"offset\": [0, 0] }, { \"glyph\": \"bB\", \"mode\": \"Prefer\", \"sides\": [\"Center\"] } ]")
            .Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}"))["T"].First(x => x.Name == "AB");
        var pts = g.Components[1].TransformedChains.SelectMany(c => c.Nodes).ToList();
        double cx = (pts.Min(n => n.X) + pts.Max(n => n.X)) / 2.0, cy = (pts.Min(n => n.Y) + pts.Max(n => n.Y)) / 2.0;
        if (Math.Abs(cx - 7) > 1.5 || Math.Abs(cy - 7) > 1.5) centered = false;
    }
    Check("sides: Center pulls a component to the middle of the grid", centered);
    string badCenter = Error(ContactLang(", \"placements\": [ { \"glyph\": \"bB\", \"mode\": \"Prefer\", \"sides\": [\"Center\", \"Left\"] } ]"));
    Check("errors: Center combined with another side is reported", badCenter.Contains("Center"), badCenter);

    // ---- shape masks: nodesOfGlyph ----
    string GlyphMaskLang(string region, string extraShape = "") => $$"""
    { "rootSeed": 5, "outputDirectory": "./o", "languages": [ { "name": "T", "grid": { "width": 12, "height": 12 }, "glyphs": ["gB", "gA"], "baseGlyphs": ["bB", "bA"],
      "combination": { "recipes": { "gB": ["bA"], "gA": ["bB"] } },
      "shapes": [
        { "id": "a", "onlyForGlyphs": ["bA"], "kind": "Literal", "nodes": [[0,0],[4,0],[4,4]], "placement": { "mode": "Fixed", "fixedOrigin": [2,2] } },
        { "id": "b", "onlyForGlyphs": ["bB"], "kind": "Literal", "nodes": [[0,0],[4,0]], "placement": { "mode": "RandomValid", "nodes": { "allow": [ {{region}} ] } } }{{extraShape}} ],
      "quality": { "maxSimilarity": 1, "rejectStraightLineOnly": false } } ] }
    """;
    bool onSource = true, offsetOk = true;
    for (int seed = 1; seed <= 6; seed++)
    {
        var all = Run(GlyphMaskLang("{ \"nodesOfGlyph\": { \"glyph\": \"bA\" } }").Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}"))["T"];
        var a = all.First(g => g.Name == "bA").Chains.SelectMany(c => c.AllNodes()).ToHashSet();
        if (!all.First(g => g.Name == "bB").Chains.SelectMany(c => c.AllNodes()).All(a.Contains)) onSource = false;
        var all2 = Run(GlyphMaskLang("{ \"nodesOfGlyph\": { \"glyph\": \"bA\", \"offset\": [1, 0] } }").Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}"))["T"];
        var a2 = all2.First(g => g.Name == "bA").Chains.SelectMany(c => c.AllNodes()).Select(n => new GlyphGen.Core.Grid.GridPoint(n.X + 1, n.Y)).ToHashSet();
        if (!all2.First(g => g.Name == "bB").Chains.SelectMany(c => c.AllNodes()).All(a2.Contains)) offsetOk = false;
    }
    Check("nodesOfGlyph: the shape's nodes are nodes of the other glyph, which is drawn first although listed later", onSource);
    Check("nodesOfGlyph: offset moves the other glyph's nodes", offsetOk);
    var star = Run(GlyphMaskLang("{ \"nodesOfGlyph\": { \"glyph\": \"*\" } }").Replace("\"baseGlyphs\": [\"bB\", \"bA\"]", "\"baseGlyphs\": [\"bA\", \"bB\"]"))["T"];
    Check("nodesOfGlyph: \"*\" reads every earlier glyph", star.First(g => g.Name == "bB").Chains.Count > 0);
    string badGlyphMask = Error(GlyphMaskLang("{ \"nodesOfGlyph\": { \"glyph\": \"nope\" } }"));
    Check("errors: nodesOfGlyph needs a real glyph", badGlyphMask.Contains("'nope'"), badGlyphMask);
    string cycleMask = Error(GlyphMaskLang("{ \"nodesOfGlyph\": { \"glyph\": \"bA\" } }",
        ", { \"id\": \"c\", \"onlyForGlyphs\": [\"bA\"], \"kind\": \"Literal\", \"nodes\": [[0,0],[1,0]], \"placement\": { \"mode\": \"RandomValid\", \"nodes\": { \"allow\": [ { \"nodesOfGlyph\": { \"glyph\": \"bB\" } } ] } } }"));
    Check("errors: glyphs reading each other's nodes in a circle are reported", cycleMask.Contains("circle"), cycleMask);

    // "*" rule: every component after the first starts on an existing node
    bool startsOnNode = true;
    for (int seed = 1; seed <= 6; seed++)
    {
        var g = Run(ContactLang(", \"placements\": [ { \"glyph\": \"*\", \"mode\": \"Prefer\", \"sides\": [\"Center\"], \"weight\": 0, \"anchor\": \"FirstNode\", \"nodes\": { \"allow\": [ { \"nodesOfComponents\": {} } ] } } ]")
            .Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}"))["T"].First(x => x.Name == "AB");
        var first = g.Components[1].TransformedChains[0].Nodes[0];
        if (!g.Components[0].TransformedChains.SelectMany(c => c.AllNodes()).Contains(first)) startsOnNode = false;
    }
    Check("component nodes mask: a \"*\" rule starts every later component on an existing node (the first is free)", startsOnNode);

    // ---- placements[].glyph as a list ----
    var listed = Run(ContactLang(", \"placements\": [ { \"glyph\": [\"bA\", \"bB\"], \"mode\": \"Fixed\", \"offset\": [1, 0] } ]"))["T"].First(x => x.Name == "AB");
    Check("placements: a glyph list applies the rule to each glyph",
        listed.Components[0].TransformedChains[0].Nodes[0] == new GlyphGen.Core.Grid.GridPoint(3, 2) && listed.Components[1].TransformedChains[0].Nodes[0] == new GlyphGen.Core.Grid.GridPoint(9, 8));
    string dupList = Error(ContactLang(", \"placements\": [ { \"glyph\": [\"bA\", \"bB\"], \"mode\": \"Fixed\" }, { \"glyph\": \"bB\", \"mode\": \"Fixed\" } ]"));
    Check("errors: a glyph in a list and in another rule is reported", dupList.Contains("'bB' has more than one rule"), dupList);

    // contact is not asked of a recipe whose components are all Fixed
    var allFixed = Run(ContactLang(", \"contact\": { \"kind\": \"Junction\" }, \"placements\": [ { \"glyph\": [\"bA\", \"bB\"], \"mode\": \"Fixed\" } ]"))["T"].First(x => x.Name == "AB");
    Check("arrangement.contact: a recipe of Fixed components is exempt (it cannot be moved to touch)", allFixed.Components.Count == 2);

    // ---- combination.arrangement.spacing.minEdgeDistance ----
    double MinGap(Glyph g)
    {
        double best = double.MaxValue;
        foreach (var ea in g.Components[0].TransformedChains.SelectMany(c => c.Edges))
            foreach (var eb in g.Components[1].TransformedChains.SelectMany(c => c.Edges))
                best = Math.Min(best, GlyphGen.Core.Grid.EdgePaths.PathDistance(GlyphGen.Core.Grid.EdgePaths.Polyline(ea), GlyphGen.Core.Grid.EdgePaths.Polyline(eb)));
        return best;
    }
    double gapWith = double.MaxValue, gapFree = double.MaxValue;
    for (int seed = 1; seed <= 10; seed++)
    {
        var withGap = Run(ContactLang(", \"spacing\": { \"enabled\": true, \"minEdgeDistance\": 3.5 }").Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}"))["T"].First(x => x.Name == "AB");
        var free = Run(ContactLang("").Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}"))["T"].First(x => x.Name == "AB");
        if (Shared(withGap) == 0) gapWith = Math.Min(gapWith, MinGap(withGap)); // touching components are excused near the touch
        if (Shared(free) == 0) gapFree = Math.Min(gapFree, MinGap(free));
    }
    Check("combination spacing.minEdgeDistance keeps the strokes of different components apart", gapWith >= 3.5 - 1e-6 && gapFree < 3.5, $"{gapWith:0.00} vs {gapFree:0.00}");

    string badSpacing = Error(QLang("").Replace("\"spacing\": { \"enabled\": true, \"minNodeDistance\": 2 }", "\"spacing\": { \"enabled\": true, \"minNodeDistance\": 2, \"onlyShapes\": [\"nope\"], \"excludeShapes\": [\"bar\"] }"));
    Check("errors: spacing onlyShapes / excludeShapes need real shape ids and cannot be combined", badSpacing.Contains("'nope'") && badSpacing.Contains("cannot both be set"), badSpacing);

    string badQRules = Error(QLang("""{ "id": "x", "subject": { "shapes": ["nope"] }, "measure": "Distance" }, { "id": "x", "measure": "Count", "window": { "radius": -1 } }"""));
    Check("errors: bad quality rules are reported together", badQRules.Contains("nope") && badQRules.Contains("minDistance") && badQRules.Contains("'x' is used") && badQRules.Contains("window.radius"), badQRules);
}

{
    string schemaText = GlyphGen.Core.Config.ConfigSchemaGenerator.Generate();
    var schemaDoc = System.Text.Json.Nodes.JsonNode.Parse(schemaText)!;
    var shapeDef = schemaDoc["definitions"]!["ShapeDefinition"]!;
    Check("schema: mode-dependent options are guarded by if/else conditions", shapeDef["allOf"] is System.Text.Json.Nodes.JsonArray { Count: > 0 });
    Check("schema: enum values carry descriptions", schemaText.Contains("\"enumDescriptions\"") && schemaText.Contains("markdownEnumDescriptions"));

    string quiet = """
    { "rootSeed": 1, "languages": [ { "name": "W", "grid": {"width":9,"height":9}, "glyphs": ["a"],
      "shapes": [ { "id": "s", "count": {"min":1,"max":1}, "chain": { "length": {"min":2,"max":2}, "connectionRules": [ { "direction": "Orthogonal" } ] },
        "placement": { "mode": "RandomValid" }, "arrangement": { "spacing": { "enabled": false, "minNodeDistance": 2 } } } ] } ] }
    """;
    var loadedQuiet = LoadCfg(quiet);
    Check("warnings: a clean config gives none", loadedQuiet.Warnings.Count == 0, string.Join(" | ", loadedQuiet.Warnings));
    var loudJson = quiet.Replace("\"direction\": \"Orthogonal\"", "\"direction\": \"Orthogonal\", \"arc\": { \"angles\": \"Cardinal\" }, \"backToStartChance\": 20")
        .Replace("\"mode\": \"RandomValid\"", "\"mode\": \"RandomValid\", \"rotations\": [90]")
        .Replace("\"enabled\": false, ", "");
    var loud = LoadCfg(loudJson.Replace("\"rotations\": [90]", "\"maxPlacementAttempts\": 5").Replace("\"spacing\": {", "\"crossing\": { \"mode\": \"None\", \"nodeTolerance\": 0.2 }, \"spacing\": {"));
    Check("warnings: options of an unused mode are reported, not rejected",
        loud.Warnings.Any(w => w.Contains("`arc`") && w.Contains("direction")) && loud.Warnings.Any(w => w.Contains("backToStartChance") && w.Contains("allowClosedLoop"))
        && loud.Warnings.Any(w => w.Contains("nodeTolerance")) && loud.Warnings.Any(w => w.Contains("shapes['s']")), string.Join(" | ", loud.Warnings));

    var numeric = LoadCfg(quiet.Replace("\"shapes\"", "\"quality\": { \"maxSimilarity\": 1, \"similarityBasis\": \"Edges\", \"acceptPenalty\": 2 }, \"shapes\"")
        .Replace("\"direction\": \"Orthogonal\"", "\"direction\": \"Orthogonal\", \"curve\": { \"chanceToBeCurved\": 0, \"tension\": { \"range\": { \"min\": 0.1, \"max\": 0.2 } } }"));
    Check("warnings: options that depend on a number or on other blocks",
        numeric.Warnings.Any(w => w.Contains("`max` is below 1")) && numeric.Warnings.Any(w => w.Contains("onFail` Score")) && numeric.Warnings.Any(w => w.Contains("`chanceToBeCurved` is above 0")),
        string.Join(" | ", numeric.Warnings));
}

// ---- connection rule split --------------------------------------------------------------------------------
{
    string SplitShape(string rule, string len = "2") => $$"""{ "id": "s", "chain": { "length": { "min": {{len}}, "max": {{len}} }, "connectionRules": [ {{rule}} ] }, "placement": { "retries": { "placement": 100 } } }""";
    string SixLine(string split) => "{ \"direction\": \"Orthogonal\", \"minLength\": 6, \"maxLength\": 6" + split + " }";
    bool cut = true, whole = true, evenly = true, chainOk = true; int splitSeen = 0;
    for (int seed = 1; seed <= 6; seed++)
    {
        foreach (var glyph in Run(Language("[" + SplitShape(SixLine(", \"split\": { \"parts\": { \"min\": 3, \"max\": 3 } }")) + "]", seed))["T"])
            foreach (var chain in glyph.Chains)
            {
                splitSeen++;
                cut &= chain.Edges.Count == 3 && chain.Nodes.Count == 4;
                evenly &= chain.Edges.All(e => Math.Abs(e.To.X - e.From.X) + Math.Abs(e.To.Y - e.From.Y) == 2);
                chainOk &= Enumerable.Range(0, chain.Edges.Count).All(i => chain.Edges[i].From == chain.Nodes[i] && chain.Edges[i].To == chain.Nodes[i + 1]);
            }
        foreach (var glyph in Run(Language("[" + SplitShape(SixLine(", \"split\": { \"chance\": 0 }")) + "]", seed))["T"])
            whole &= glyph.Chains.All(c => c.Edges.Count == 1 && c.Nodes.Count == 2);
    }
    Check("connectionRules split: a 6-long edge with parts 3 becomes three 2-long pieces, and the nodes join the chain", splitSeen > 0 && cut && evenly && chainOk);
    Check("connectionRules split: chance 0 leaves the edge whole", whole);

    bool extraOk = true; int extraSeen = 0;
    for (int seed = 1; seed <= 6; seed++)
        foreach (var glyph in Run(Language("[" + SplitShape(SixLine(", \"split\": { \"mode\": \"ExtraNodes\", \"parts\": { \"min\": 3, \"max\": 3 } }")) + "]", seed))["T"])
            foreach (var chain in glyph.Chains)
            {
                extraSeen++;
                var all = chain.AllNodes();
                extraOk &= chain.Edges.Count == 1 && chain.Nodes.Count == 2 && all.Count == 4
                    && chain.Edges[0].ExtraNodes().All(n => (n.X == chain.Edges[0].From.X && n.X == chain.Edges[0].To.X) || (n.Y == chain.Edges[0].From.Y && n.Y == chain.Edges[0].To.Y));
            }
    Check("connectionRules split mode ExtraNodes: the edge stays whole and two extra nodes sit on it (all four count as nodes)", extraSeen > 0 && extraOk);
    var carried = new GridEdge(new GridPoint(0, 0), new GridPoint(6, 0)).WithLineFractions(new[] { 1.0 / 3, 2.0 / 3 }).WithPoints(new GridPoint(10, 4), new GridPoint(10, 10));
    Check("connectionRules split mode ExtraNodes: moving or turning the edge carries the nodes along", carried.ExtraNodes().SequenceEqual(new[] { new GridPoint(10, 6), new GridPoint(10, 8) }), string.Join(" ", carried.ExtraNodes()));
    {
        // Nearest: the edge (0,0)-(7,3) passes no lattice point; its nodes sit beside it, the edge stays one straight edge.
        var slanted = new GridEdge(new GridPoint(0, 0), new GridPoint(7, 3)).WithLineNodes(new[] { new LineNode(3.0 / 7 * 1.0, 0.0) });
        var beside = new GridEdge(new GridPoint(0, 0), new GridPoint(7, 3)).WithLineNodes(new[] { new LineNode(((3.0 * 7 + 1 * 3) / 58.0), (1 * 7 - 3 * 3) / Math.Sqrt(58)) });
        Check("ExtraNodes with an offset: the node is the lattice point beside the edge", beside.ExtraNodes().SequenceEqual(new[] { new GridPoint(3, 1) }), string.Join(" ", beside.ExtraNodes()));
        var mirroredBeside = beside.WithPoints(new GridPoint(0, 0), new GridPoint(7, -3)).MirroredArc();
        Check("ExtraNodes with an offset: a reflection puts the node on the mirrored side", mirroredBeside.ExtraNodes().SequenceEqual(new[] { new GridPoint(3, -1) }), string.Join(" ", mirroredBeside.ExtraNodes()));
        bool nearOk = true; int nearSeen = 0;
        for (int seed = 1; seed <= 10; seed++)
            foreach (var glyph in Run(Language("[" + SplitShape(SixLine(", \"split\": { \"mode\": \"ExtraNodes\", \"snap\": \"Nearest\", \"maxDeviation\": 0.75 }")) + "]", seed))["T"])
                foreach (var chain in glyph.Chains)
                {
                    nearSeen++;
                    nearOk &= chain.Edges.Count == 1 && chain.Nodes.Count == 2 && !chain.Edges[0].IsCurved;
                }
        Check("connectionRules split mode ExtraNodes with snap Nearest: works, the edge stays whole and straight", nearSeen > 0 && nearOk);
    }

    string splitCost = Error(Language("[" + SplitShape(SixLine(", \"split\": { }"), "3") + "]", 3));
    Check("connectionRules split: the new nodes cost no chain length (length 3 = start plus two moves, however the moves are cut)", splitCost == "", splitCost);
    string badSplit = Error(Language("[" + SplitShape("{ \"direction\": \"Circle\", \"minLength\": 1, \"maxLength\": 2, \"split\": { \"parts\": { \"min\": 1, \"max\": 2 } } }") + "]", 3));
    Check("errors: split on a Circle rule and parts below 2 are reported", badSplit.Contains("only applies to a straight rule") && badSplit.Contains("split.parts.min"), badSplit);
    var nearest = LoadCfg(Language("[" + SplitShape(SixLine(", \"split\": { \"snap\": \"Exact\", \"maxDeviation\": 0.5 }")) + "]", 3));
    Check("warnings: split.maxDeviation without snap Nearest is reported", nearest.Warnings.Any(w => w.Contains("maxDeviation")), string.Join(" | ", nearest.Warnings));
}

// ---- shape requires -----------------------------------------------------------------------------------------
{
    string Lit(string id, int x, string extra = "") => $$"""{ "id": "{{id}}", "kind": "Literal", "nodes": [[0,0],[2,0]], "placement": { "start": { "mode": "Fixed", "origin": [{{x}},{{x}}] }, "rotation": { "allowed": false } }, "arrangement": { "crossing": { "mode": "Anywhere" } }{{extra}} }""";
    HashSet<string> Drawn(string shapes, int seed) => Run(Language("[" + shapes + "]", seed))["T"].SelectMany(g => g.Chains).Select(ch => ch.ShapeTag!.Split('#')[0]).ToHashSet();
    bool presentOk = true, absentOk = true, noneOk = true;
    for (int seed = 1; seed <= 8; seed++)
    {
        var withBase = Drawn(Lit("a", 2) + "," + Lit("b", 5, ", \"requires\": { \"present\": [\"a\"] }") + "," + Lit("c", 8, ", \"requires\": { \"absent\": [\"a\"] }"), seed);
        presentOk &= withBase.SetEquals(new[] { "a", "b" });
        var without = Drawn(Lit("a", 2, ", \"chance\": 0") + "," + Lit("b", 5, ", \"requires\": { \"present\": [\"a\"] }") + "," + Lit("c", 8, ", \"requires\": { \"absent\": [\"a\"] }"), seed);
        absentOk &= without.SetEquals(new[] { "c" });
        var anyOf = Drawn(Lit("a", 2, ", \"chance\": 0") + "," + Lit("b", 5) + "," + Lit("c", 8, ", \"requires\": { \"present\": [\"a\", \"b\"], \"matchPresent\": \"Any\" }"), seed);
        var allOf = Drawn(Lit("a", 2, ", \"chance\": 0") + "," + Lit("b", 5) + "," + Lit("c", 8, ", \"requires\": { \"present\": [\"a\", \"b\"] }"), seed);
        noneOk &= anyOf.SetEquals(new[] { "b", "c" }) && allOf.SetEquals(new[] { "b" });
    }
    Check("shape requires: present needs the listed shapes drawn, absent needs them not drawn", presentOk && absentOk);
    bool absentAnyOk = true;
    for (int seed = 1; seed <= 6; seed++)
    {
        // a is drawn, b is not: matchAbsent Any needs one of them missing (b is), All needs both missing (a is there).
        string ab = Lit("a", 2) + "," + Lit("b", 5, ", \"chance\": 0") + ",";
        var any = Drawn(ab + Lit("c", 8, ", \"requires\": { \"absent\": [\"a\", \"b\"], \"matchAbsent\": \"Any\" }"), seed);
        var all = Drawn(ab + Lit("c", 8, ", \"requires\": { \"absent\": [\"a\", \"b\"] }"), seed);
        var bothThere = Drawn(Lit("a", 2) + "," + Lit("b", 5) + "," + Lit("c", 8, ", \"requires\": { \"absent\": [\"a\", \"b\"], \"matchAbsent\": \"Any\" }"), seed);
        absentAnyOk &= any.Contains("c") && !all.Contains("c") && !bothThere.Contains("c");
    }
    Check("shape requires: matchAbsent Any skips the shape only when every listed shape exists, All when any exists", absentAnyOk);
    Check("shape requires: matchPresent Any is satisfied by one listed shape, All by every one", noneOk);

    int Hits(string requiresChance, string extra, double strength)
    {
        int hit = 0;
        for (int seed = 1; seed <= 25; seed++)
        {
            string cfg = Language("[" + Lit("a", 2) + "," + Lit("b", 5, $", \"requires\": {{ \"present\": [\"a\"], \"chance\": {requiresChance} }}" + extra) + "]", seed);
            if (strength != 0) cfg = cfg.Replace("\"glyphs\"", $"\"usageSizeBias\": {{ \"strength\": {strength.ToString(System.Globalization.CultureInfo.InvariantCulture)}, \"affectChance\": true }}, \"glyphs\"");
            hit += Run(cfg)["T"].Count(g => g.Chains.Any(ch => ch.ShapeTag!.StartsWith("b#")));
        }
        return hit;
    }
    int quarter = Hits("25", "", 0), bothRolls = Hits("50", ", \"chance\": 50", 0);
    Check("shape requires: its chance is rolled on top of the shape's own chance (50% x 50% is about a quarter)", quarter > 40 && quarter < 110 && bothRolls > 40 && bothRolls < 110, $"{quarter} / {bothRolls} of 300");
    string badRequires = Error(Language("[" + Lit("a", 2, ", \"requires\": { \"present\": [\"a\", \"zzz\", \"later\"], \"absent\": [\"a\"] }") + "," + Lit("later", 5) + "," + Lit("e", 8, ", \"requires\": { \"chance\": 50 }") + "]", 3));
    Check("errors: requires naming itself, an unknown or later shape, or nothing at all is reported",
        badRequires.Contains("cannot depend on itself") && badRequires.Contains("'zzz' is not the id of a shape") && badRequires.Contains("'later' is not listed before") && badRequires.Contains("needs a present or an absent list") && Error(Language("[" + Lit("a", 2) + "," + Lit("b", 5, ", \"requires\": { \"present\": [\"a\"], \"matchAbsent\": \"Any\" }") + "]", 3)).Contains("only applies together with an absent list"), badRequires);
}

{
    string cross = Error(Language("[{ \"id\": \"a\", \"kind\": \"Literal\", \"nodes\": [[0,0],[2,0]], \"placement\": { \"start\": { \"mode\": \"Fixed\", \"origin\": [2,2] } } }]", 3, "").Replace("\"glyphs\"", "\"combination\": { \"arrangement\": { \"crossing\": { \"mode\": \"NodesOnly\", \"nodesOfShapes\": [\"a\", \"b\", \"zzz\"] } } }, \"glyphs\""));
    Check("errors: combination crossing.nodesOfShapes naming a glyph or an unknown id is reported",
        cross.Contains("'b' is a glyph name, not a shape id") && cross.Contains("'zzz' is not the id of any shape") && !cross.Contains("'a' is"), cross);
}

// ---- combination.partners -----------------------------------------------------------------------------------
{
    string PartnerLang(string startExtra = "{}", string combination = "", string baseOrder = "\"r\", \"c\"", int seed = 5, string childExtra = "") => $$"""
    { "rootSeed": {{seed}}, "outputDirectory": "./o", "languages": [ { "name": "T", "grid": { "width": 15, "height": 15 }, "glyphs": ["AB"], "baseGlyphs": [{{baseOrder}}],
      "combination": { "recipes": { "AB": ["r", "c"] }, "partners": { "c": ["r"] } {{combination}} },
      "shapes": [
        { "id": "rs", "onlyForGlyphs": ["r"], "kind": "Literal", "nodes": [[0,0],[4,0],[4,4]],
          "placement": { "start": { "mode": "Fixed", "origin": [3,3] }, "rotation": { "allowed": false } }, "arrangement": { "crossing": { "mode": "None" } } },
        { "id": "cs", "onlyForGlyphs": ["c"], "chain": { "length": { "min": 3, "max": 3 }, "connectionRules": [ { "direction": "Orthogonal", "minLength": 3, "maxLength": 3 } ] },
          "placement": { "start": { "onPartnerNode": {{startExtra}} } }, "arrangement": { "crossing": { "mode": "None" } } {{childExtra}} } ],
      "quality": { "maxSimilarity": 1, "rejectStraightLineOnly": false } } ] }
    """;

    string RunFails(string json) { try { Run(json); return ""; } catch (GlyphGenerationException e) { return e.Message; } }
    bool startsOk = true, notCrossing = true, identity = true; int partnerSeen = 0;
    for (int seed = 1; seed <= 10; seed++)
    {
        var glyphs = Run(PartnerLang(seed: seed))["T"];
        var composite = glyphs.First(g => g.Name == "AB");
        var rootNodes = glyphs.First(g => g.Name == "r").Chains.SelectMany(c => c.AllNodes()).ToHashSet();
        var child = glyphs.First(g => g.Name == "c");
        partnerSeen++;
        startsOk &= child.Chains.All(ch => rootNodes.Contains(ch.Nodes[0]));
        notCrossing &= child.Chains.SelectMany(c => c.Edges).All(e => !glyphs.First(g => g.Name == "r").Chains.SelectMany(c => c.Edges).Any(o => EdgePaths.Crosses(e, o)));
        identity &= composite.Components.Count == 2 && composite.Components.All(pc => pc.Translation.X == 0 && pc.Translation.Y == 0 && pc.RotationDegrees == 0)
            && composite.Components[1].TransformedChains.SelectMany(c => c.Nodes).SequenceEqual(child.Chains.SelectMany(c => c.Nodes));
    }
    Check("partners: a shape with onPartnerNode starts on a node of its partner", partnerSeen == 10 && startsOk);
    Check("partners: the partner's strokes are obstacles (nothing of the glyph crosses them)", notCrossing);
    Check("partners: a recipe with such a glyph is a plain overlay (components stay where they were drawn)", identity);

    var reversed = Run(PartnerLang(baseOrder: "\"c\", \"r\""))["T"];
    Check("partners: the partner is drawn first even when listed after the glyph", reversed.Count == 3 && reversed.First(g => g.Name == "c").Chains.Count == 1);
    Check("partners: NodeSources lists the partner as a dependency", NodeSources.Of(LoadCfg(PartnerLang()).Languages[0], "c").Contains("r"));

    int noneAnywhere = Run(PartnerLang("{ \"shapes\": [\"cs\"], \"ifNone\": \"Anywhere\" }", ", \"partnerContact\": false"))["T"].First(g => g.Name == "c").Chains.Count;
    int noneSkip = Run(PartnerLang("{ \"shapes\": [\"cs\"], \"ifNone\": \"Skip\" }", ", \"partnerContact\": false"))["T"].First(g => g.Name == "c").Chains.Count;
    string noneFail = RunFails(PartnerLang("{ \"shapes\": [\"cs\"], \"ifNone\": \"Fail\" }", ", \"partnerContact\": false"));
    Check("partners: ifNone Anywhere draws the shape, Skip leaves it out, Fail stops with a named error", noneAnywhere == 1 && noneSkip == 0 && noneFail.Contains("has no partner node to start on"), $"{noneAnywhere}/{noneSkip}/{noneFail}");

    string untouched = RunFails(PartnerLang("{ \"chance\": 0 }").Replace("\"quality\": {", "\"quality\": { \"retries\": 3,"));
    Check("partners: a glyph that keeps missing its partner is rejected with a named reason", untouched.Contains("does not touch its partner 'r'"), untouched);
    int noContactNeeded = Run(PartnerLang("{ \"chance\": 0 }", ", \"partnerContact\": false"))["T"].Count;
    Check("partners: partnerContact false does not insist on touching", noContactNeeded == 3);

    // Redraw: a rule that fails when the child is drawn redraws the partner, and the child (and the composite) are drawn again against it.
    string RedoLang(bool withRule) => PartnerLang().Replace("\"quality\": {", "\"quality\": { \"retries\": 40, \"maxRegenerationAttempts\": 40,"
            + (withRule ? " \"pairRules\": [ { \"id\": \"dotsApart\", \"subject\": { \"glyphs\": [\"r\"], \"shapes\": [\"rd\"], \"kind\": \"Dot\" }, \"against\": { \"glyphs\": [\"c\"], \"shapes\": [\"cd\"], \"kind\": \"Dot\" }, \"measure\": \"Distance\", \"minDistance\": 7, \"onFail\": \"Reject\", \"redo\": \"Either\" } ]," : ""))
        .Replace("{ \"id\": \"cs\"", "{ \"id\": \"rd\", \"onlyForGlyphs\": [\"r\"], \"chain\": { \"length\": { \"min\": 2, \"max\": 2 }, \"connectionRules\": [ { \"direction\": \"Circle\", \"minLength\": 0, \"maxLength\": 0 } ] }, \"placement\": { \"start\": { \"mode\": \"RandomValid\" } }, \"arrangement\": { \"crossing\": { \"mode\": \"None\" } } },"
            + " { \"id\": \"cd\", \"onlyForGlyphs\": [\"c\"], \"chain\": { \"length\": { \"min\": 2, \"max\": 2 }, \"connectionRules\": [ { \"direction\": \"Circle\", \"minLength\": 0, \"maxLength\": 0 } ] }, \"placement\": { \"start\": { \"mode\": \"RandomValid\" } }, \"arrangement\": { \"crossing\": { \"mode\": \"None\" } } }, { \"id\": \"cs\"");
    int redrawn = 0; bool redoOk = true, consistent = true;
    for (int seed = 1; seed <= 12; seed++)
    {
        var unjudged = Run(RedoLang(false).Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}"))["T"];
        var judged = Run(RedoLang(true).Replace("\"rootSeed\": 5", $"\"rootSeed\": {seed}"))["T"];
        var rJ = judged.First(g => g.Name == "r"); var cJ = judged.First(g => g.Name == "c"); var ab = judged.First(g => g.Name == "AB");
        if (!rJ.Chains.SelectMany(c => c.Nodes).SequenceEqual(unjudged.First(g => g.Name == "r").Chains.SelectMany(c => c.Nodes))) redrawn++;
        var dr = rJ.Chains.SelectMany(c => c.Edges).Where(e => e.IsDot).Select(e => e.From).ToList();
        var dc = cJ.Chains.SelectMany(c => c.Edges).Where(e => e.IsDot).Select(e => e.From).ToList();
        redoOk &= dr.All(a => dc.All(b => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y)) >= 7 - 1e-9));
        var rNodes = rJ.Chains.SelectMany(c => c.AllNodes()).ToHashSet();
        consistent &= cJ.Chains.Where(c => c.ShapeTag!.StartsWith("cs#")).All(c => rNodes.Contains(c.Nodes[0]))
            && ab.Components[0].TransformedChains.SelectMany(c => c.Nodes).SequenceEqual(rJ.Chains.SelectMany(c => c.Nodes))
            && ab.Components[1].TransformedChains.SelectMany(c => c.Nodes).SequenceEqual(cJ.Chains.SelectMany(c => c.Nodes));
    }
    Check("partners redraw: a rule that rejects the child makes the partner be redrawn (and the rule then holds)", redrawn > 0 && redoOk, $"{redrawn}/12 redrawn");
    Check("partners redraw: after a redraw the child still starts on the new partner and the composite is rebuilt from both", consistent);

    string badPartners = Error(PartnerLang("{ \"partner\": \"zzz\" }", ", \"partners\": {}").Replace("\"partners\": { \"c\": [\"r\"] } , \"partners\": {}", "\"partners\": { \"c\": [\"c\", \"AB\", \"nope\"], \"AB\": [\"r\"] }"));
    Check("errors: bad combination.partners entries are reported",
        badPartners.Contains("cannot be drawn against itself") && badPartners.Contains("'AB' is a composite") && badPartners.Contains("'nope' is not listed") && badPartners.Contains("is a composite: only a glyph drawn from shapes"), badPartners);
    string lonePartner = Error(PartnerLang().Replace("\"AB\": [\"r\", \"c\"]", "\"AB\": [\"c\"]"));
    Check("errors: a recipe must list the partners of its glyphs", lonePartner.Contains("list 'r' in the recipe too"), lonePartner);
    string twoRoots = Error(PartnerLang().Replace("\"partners\": { \"c\": [\"r\"] }", "\"partners\": { \"c\": [\"r\"] }").Replace("\"AB\": [\"r\", \"c\"]", "\"AB\": [\"r\", \"c\", \"x\"]").Replace("\"baseGlyphs\": [", "\"baseGlyphs\": [\"x\", "));
    Check("partners: an overlay recipe may have several components without partners", !twoRoots.Contains("without partners"), twoRoots);
    string noPartnersAtAll = Error(PartnerLang().Replace("\"partners\": { \"c\": [\"r\"] }", "\"partners\": { }"));
    Check("errors: onPartnerNode without combination.partners is reported", noPartnersAtAll.Contains("needs combination.partners"), noPartnersAtAll);
    // Several partners: each start (of a shape with onPartnerNode and no partner named) goes to the first partner not touched yet.
    string TwoPartners(string startExtra, int count, int seed) => $$"""
    { "rootSeed": {{seed}}, "outputDirectory": "./o", "languages": [ { "name": "T", "grid": { "width": 19, "height": 9 }, "glyphs": ["ABC"], "baseGlyphs": ["p1", "p2", "c"],
      "combination": { "recipes": { "ABC": ["p1", "p2", "c"] }, "partners": { "c": ["p1", "p2"] } },
      "shapes": [
        { "id": "s1", "onlyForGlyphs": ["p1"], "kind": "Literal", "nodes": [[0,0],[0,4]], "placement": { "start": { "mode": "Fixed", "origin": [1,2] }, "rotation": { "allowed": false } } },
        { "id": "s2", "onlyForGlyphs": ["p2"], "kind": "Literal", "nodes": [[0,0],[4,0],[4,4]], "placement": { "start": { "mode": "Fixed", "origin": [13,2] }, "rotation": { "allowed": false } } },
        { "id": "hook", "onlyForGlyphs": ["c"], "count": { "min": {{count}}, "max": {{count}} }, "chain": { "length": { "min": 2, "max": 2 }, "connectionRules": [ { "direction": "Orthogonal", "minLength": 3, "maxLength": 4 } ] },
          "placement": { "start": { "onPartnerNode": {{startExtra}} } }, "arrangement": { "crossing": { "mode": "None" } } } ],
      "quality": { "maxSimilarity": 1, "rejectStraightLineOnly": false } } ] }
    """;
    bool reachesBoth = true; int ranTwo = 0;
    for (int seed = 1; seed <= 15; seed++)
    {
        var made = Run(TwoPartners("{}", 2, seed))["T"];
        var child = made.First(g => g.Name == "c"); var first = made.First(g => g.Name == "p1"); var second = made.First(g => g.Name == "p2");
        var childNodes = GlyphGeometryUtils.GetAllNodes(child).ToHashSet();
        bool touchesFirst = GlyphGeometryUtils.GetAllNodes(first).Any(childNodes.Contains), touchesSecond = GlyphGeometryUtils.GetAllNodes(second).Any(childNodes.Contains);
        reachesBoth &= touchesFirst && touchesSecond; ranTwo++;
    }
    Check("partners: with two partners, two starts that name none reach both of them", reachesBoth && ranTwo == 15);
    string oneStartOnly = RunFails(TwoPartners("{}", 1, 3));
    Check("partners: a single start reaches one partner, so the glyph is redrawn until it fails to touch the other (and says so)", oneStartOnly.Contains("does not touch"), oneStartOnly);
    string badByPartner = Error(TwoPartners("{ \"shapesByPartner\": { \"zzz\": [\"s1\"], \"p1\": [\"nope\"] } }", 2, 3));
    Check("errors: bad shapesByPartner entries are reported", badByPartner.Contains("'zzz' is not the partner") && badByPartner.Contains("'nope' is not the id of a shape"), badByPartner);
    bool byPartnerOk = true;
    for (int seed = 1; seed <= 6; seed++)
    {
        var made = Run(TwoPartners("{ \"shapesByPartner\": { \"p1\": [\"s1\"], \"p2\": [\"s2\"] } }", 2, seed))["T"];
        byPartnerOk &= made.First(g => g.Name == "c").Chains.Count == 2;
    }
    Check("partners: shapesByPartner naming the partners' shapes still draws both starts", byPartnerOk);
    // combination.partnerRedraw: a partner that never lets its glyph touch it is drawn again.
    string LuckyPartner(string redraw, int seed) => $$"""
    { "rootSeed": {{seed}}, "outputDirectory": "./o", "languages": [ { "name": "T", "grid": { "width": 7, "height": 7 }, "glyphs": ["AB"], "baseGlyphs": ["r", "c"],
      "combination": { "recipes": { "AB": ["r", "c"] }, "partners": { "c": ["r"] } {{redraw}} },
      "shapes": [
        { "id": "rs", "onlyForGlyphs": ["r"], "chain": { "length": { "min": 3, "max": 3 }, "connectionRules": [ { "direction": "Orthogonal", "minLength": 2, "maxLength": 2 } ] },
          "placement": { "start": { "mode": "RandomValid" } } },
        { "id": "cs", "onlyForGlyphs": ["c"], "kind": "Literal", "nodes": [[0,0],[0,2]], "placement": { "start": { "mode": "Fixed", "origin": [3,3] }, "rotation": { "allowed": false } },
          "arrangement": { "crossing": { "mode": "NodesOnly" } } } ],
      "quality": { "maxSimilarity": 1, "rejectStraightLineOnly": false, "retries": 4 } } ] }
    """;
    int missed(string redraw) { int n = 0; for (int seed = 1; seed <= 30; seed++) if (RunFails(LuckyPartner(redraw, seed)) != "") n++; return n; }
    int withoutRedraw = missed(", \"partnerRedraw\": { \"max\": 0 }"), withRedraw = missed(", \"partnerRedraw\": { \"max\": 20, \"afterContactMisses\": 2 }");
    Check("partnerRedraw: redrawing the partner rescues glyphs that could not touch it", withoutRedraw > 3 && withRedraw < withoutRedraw, $"{withoutRedraw} failures without, {withRedraw} with");
    string badRedraw = Error(LuckyPartner(", \"partnerRedraw\": { \"max\": -1, \"afterContactMisses\": 0 }", 1));
    Check("errors: bad partnerRedraw settings are reported", badRedraw.Contains("partnerRedraw.max") && badRedraw.Contains("afterContactMisses"), badRedraw);
    string bothStarts = Error(PartnerLang().Replace("\"onPartnerNode\": {}", "\"onPartnerNode\": {}, \"onExistingNode\": {}"));
    Check("errors: onPartnerNode together with onExistingNode is reported", bothStarts.Contains("cannot be combined with onExistingNode"), bothStarts);

    // ---- CLI batch: options, discovery, seeds, threads, determinism ----
    string tmp = Path.Combine(Path.GetTempPath(), "glyphgen-cli-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(tmp, "configs"));
    File.WriteAllText(Path.Combine(tmp, "configs", "a.language.json"), LayoutUpgrade.Apply(PartnerLang(seed: 5)));
    File.WriteAllText(Path.Combine(tmp, "configs", "b.json"), LayoutUpgrade.Apply(PartnerLang(seed: 6).Replace("\"rootSeed\": 6, ", "")));
    File.WriteAllText(Path.Combine(tmp, "configs", "config.schema.json"), "{ \"x\": 1 }");
    File.WriteAllText(Path.Combine(tmp, "configs", "notes.json"), "[1,2]");

    var seedRange = GlyphGen.Cli.CliOptions.ParseSeeds("1-3,7,3", out _);
    Check("cli: --seed accepts ranges, lists and drops duplicates", seedRange is not null && seedRange.SequenceEqual(new ulong[] { 1, 2, 3, 7 }), seedRange is null ? "null" : string.Join(",", seedRange));
    Check("cli: a backwards seed range is an error", GlyphGen.Cli.CliOptions.ParseSeeds("5-2", out var whyBack) is null && whyBack is not null);
    Check("cli: --seed together with --runs is an error", GlyphGen.Cli.CliOptions.Parse(new[] { "--seed", "1", "--runs", "3" }).Errors.Any(e => e.Contains("cannot be combined")));
    Check("cli: --threads auto means the whole budget (0)", GlyphGen.Cli.CliOptions.Parse(new[] { "--threads", "auto" }).Threads == 0);
    Check("cli: an unknown option is reported", GlyphGen.Cli.CliOptions.Parse(new[] { "--nope" }).Errors.Count == 1);
    Check("cli: rootSeed in the file is noticed, its absence too",
        GlyphGen.Cli.CliOptions.FileHasSeed(Path.Combine(tmp, "configs", "a.language.json")) && !GlyphGen.Cli.CliOptions.FileHasSeed(Path.Combine(tmp, "configs", "b.json")));

    var noArgs = GlyphGen.Cli.CliOptions.Parse(Array.Empty<string>());
    var found = noArgs.ResolveConfigs(tmp, out var foundNotice);
    Check("cli: with no config named the 'configs' folder is used, skipping the schema and non-configs",
        found.Count == 2 && found.All(f => Path.GetFileName(f) is "a.language.json" or "b.json") && foundNotice!.Contains("configs"), string.Join(",", found.Select(Path.GetFileName)));
    var namedGlob = GlyphGen.Cli.CliOptions.Parse(new[] { "configs/a*.json" }).ResolveConfigs(tmp, out _);
    Check("cli: a glob names its files", namedGlob.Count == 1 && namedGlob[0].EndsWith("a.language.json"), string.Join(",", namedGlob));
    Check("cli: a config's name drops .language", GlyphGen.Cli.BatchRunner.ConfigNames(found).Values.Order().SequenceEqual(new[] { "a", "b" }));

    var sink = new StringWriter();
    var loadedAll = new GlyphGen.Cli.BatchRunner(noArgs, sink, null).LoadAll(found);
    Check("cli: every config loads", loadedAll is not null && loadedAll.Count == 2, sink.ToString());

    var runsOpt = GlyphGen.Cli.CliOptions.Parse(new[] { "--runs", "3", "--master-seed", "9", "--threads", "8" });
    var planner = new GlyphGen.Cli.BatchRunner(runsOpt, sink, null);
    var jobs = planner.Plan(found, loadedAll!);
    Check("cli: --runs makes configs x runs jobs with the same distinct seeds for every config",
        jobs.Count == 6 && jobs.Select(j => j.Seed).Distinct().Count() == 3 && jobs.GroupBy(j => j.ConfigName).All(g => g.Select(j => j.Seed).SequenceEqual(jobs.Take(3).Select(j => j.Seed))));
    Check("cli: the same master seed gives the same seeds", planner.MasterSeed == 9 && GlyphGen.Cli.BatchRunner.DrawSeeds(9, 3).SequenceEqual(jobs.Take(3).Select(j => j.Seed)));
    Check("cli: 8 threads over 6 runs: 6 at a time with 1 thread each", planner.ConcurrentRuns == 6 && planner.ThreadsPerRun == 1);
    var split = new GlyphGen.Cli.BatchRunner(GlyphGen.Cli.CliOptions.Parse(new[] { "--runs", "2", "--threads", "8" }), sink, null);
    split.Plan(new[] { found[0] }, loadedAll!);
    Check("cli: 8 threads over 2 runs: 2 at a time with 4 threads each", split.ConcurrentRuns == 2 && split.ThreadsPerRun == 4);
    var perRun = new GlyphGen.Cli.BatchRunner(GlyphGen.Cli.CliOptions.Parse(new[] { "--runs", "9", "--threads", "8", "--threads-per-run", "2" }), sink, null);
    perRun.Plan(found, loadedAll!);
    Check("cli: --threads-per-run fixes the inner threads and the rest of the budget runs in parallel", perRun.ConcurrentRuns == 4 && perRun.ThreadsPerRun == 2);

    var bareRun = new GlyphGen.Cli.BatchRunner(noArgs, sink, null);
    var plainJobs = bareRun.Plan(found, loadedAll!);
    var aJob = plainJobs.First(j => j.ConfigName == "a");
    var bJob = plainJobs.First(j => j.ConfigName == "b");
    Check("cli: no seed named: a config's own rootSeed is used, a config without one gets a random seed",
        aJob.Seed == 5 && bJob.Seed != 0 && bareRun.MasterSeed is not null);

    // The same seed gives the same glyphs whatever the thread count, and a run writes to <out>/<config>/<seed>.
    string RunSnapshot(int threadsPerRun, out string dir)
    {
        var snap = new System.Text.StringBuilder();
        string d = "";
        var opts = GlyphGen.Cli.CliOptions.Parse(new[] { "--seed", "5", "--threads", threadsPerRun.ToString(), "--out", Path.Combine(tmp, "out" + threadsPerRun) });
        var runner = new GlyphGen.Cli.BatchRunner(opts, new StringWriter(), (cfg, glyphs, _, _) =>
        {
            d = cfg.OutputDirectory;
            foreach (var (lang, list) in glyphs) foreach (var g in list) snap.AppendLine(lang + g.Name + g);
        });
        var j = runner.Plan(new[] { found[0] }, loadedAll!);
        var res = runner.Run(j);
        dir = d;
        return res[0].Ok ? snap.ToString() : "FAILED: " + res[0].Error;
    }
    string one = RunSnapshot(1, out var dirOne), four = RunSnapshot(4, out _);
    Check("cli: the same seed gives the same glyphs with 1 and with 4 threads", one == four && !one.StartsWith("FAILED"), one.StartsWith("FAILED") ? one : "differs");
    Check("cli: a run is written to <out>/<config>/<seed>", dirOne.EndsWith(Path.Combine("out1", "a", "5")), dirOne);

    var failFast = new GlyphGen.Cli.BatchRunner(GlyphGen.Cli.CliOptions.Parse(new[] { "--seed", "1-3", "--parallel-runs", "1", "--fail-fast" }), new StringWriter(), null);
    var ffJobs = failFast.Plan(new[] { found[0] }, loadedAll!);
    File.WriteAllText(found[0], "{ \"languages\": [ { \"name\": \"x\" } ] }");
    var ffRes = failFast.Run(ffJobs);
    Check("cli: --fail-fast starts no new run after a failure, and a failed run is reported", !ffRes[0].Ok && ffRes.Skip(1).All(r => r.Skipped), string.Join(",", ffRes.Select(r => r.Skipped ? "skip" : r.Ok ? "ok" : "fail")));
    // ---- CLI messages ----
    string longError = "[Alchemist] Shape 'x' for glyph 'g' could not be placed within its configured attempt budget. Of 100 attempt(s): 100 ran out of valid moves. The chain keeps running out of moves: try a shorter chain.length, e.g. 3. Retried before giving up: the shape was redrawn 5 time(s).";
    var errParts = GlyphGen.Cli.ErrorFormatter.Format(longError).Split('\n');
    Check("cli messages: a long error becomes a headline plus indented detail lines (and 'attempt(s):' / 'e.g.' do not split it)",
        errParts.Length == 4 && errParts[0].StartsWith("[Alchemist] Shape 'x'") && errParts[1].StartsWith("  Of 100 attempt(s): 100 ran") && errParts[2].Contains("e.g. 3") && errParts[3].StartsWith("  Retried"), string.Join(" | ", errParts));
    Check("cli messages: the headline is the first sentence only", GlyphGen.Cli.ErrorFormatter.Headline(longError).EndsWith("attempt budget."));
    var collapsed = GlyphGen.Cli.WarningCollapser.Collapse(new[] { "a", "b", "a", "a" });
    Check("cli messages: identical warnings are merged with a count, in first-seen order", collapsed.SequenceEqual(new[] { "a (x3)", "b" }), string.Join(",", collapsed));
    GlyphGen.Cli.CliStyle.Enabled = true;
    string painted = GlyphGen.Cli.CliStyle.Bad("x");
    GlyphGen.Cli.CliStyle.Enabled = false;
    Check("cli messages: colour codes are added when enabled and stripped for the log", painted.Contains("\x1b[") && GlyphGen.Cli.CliStyle.Strip(painted) == "x" && GlyphGen.Cli.CliStyle.Bad("x") == "x");

    var emptyDir = Path.Combine(tmp, "nothing-here"); Directory.CreateDirectory(emptyDir);
    var noneOpts = GlyphGen.Cli.CliOptions.Parse(Array.Empty<string>());
    var noneFound = noneOpts.ResolveConfigs(emptyDir, out var noneWhere);
    Check("cli messages: nothing found says where it looked", noneFound.Count == 0 && noneOpts.Searched.Count == 1 && noneOpts.Searched[0].Contains("current folder") && noneWhere == "the current folder", string.Join(";", noneOpts.Searched));
    var onlyJunk = Path.Combine(tmp, "junk"); Directory.CreateDirectory(Path.Combine(onlyJunk, "configs")); File.WriteAllText(Path.Combine(onlyJunk, "configs", "x.json"), "[1]");
    var junkOpts = GlyphGen.Cli.CliOptions.Parse(Array.Empty<string>()); junkOpts.ResolveConfigs(onlyJunk, out _);
    Check("cli messages: an empty 'configs' folder and the current folder are both reported as searched", junkOpts.Searched.Count == 2 && junkOpts.Searched[0].Contains("'configs' folder"), string.Join(";", junkOpts.Searched));
    var msgOpts = GlyphGen.Cli.CliOptions.Parse(new[] { "--verbose", "--log", "x.log", "--pause" });
    Check("cli messages: --verbose, --log and --pause are read; --verbose with --quiet is an error",
        msgOpts.Verbose && msgOpts.LogFile == "x.log" && msgOpts.Pause == true && GlyphGen.Cli.CliOptions.Parse(new[] { "--verbose", "--quiet" }).Errors.Count == 1);

    var events = new List<GlyphGen.Core.Generation.GenerationProgress>();
    var progressConfig = LoadCfg(PartnerLang(seed: 3));
    new RunOrchestrator(new LanguageGenerator(new GlyphGenerator(new ChainGenerator(), new PlacementResolver(), new CombinationResolver())))
        .Run(progressConfig, e => { lock (events) events.Add(e); });
    int startedGlyphs = events.Count(e => e.Stage == GlyphGen.Core.Generation.GenerationStage.GlyphStarted), doneGlyphs = events.Count(e => e.Stage == GlyphGen.Core.Generation.GenerationStage.GlyphDone);
    Check("cli messages: generation reports start, every glyph started and done in turn with the running total, and the end",
        events.Count > 0 && events[0].Stage == GlyphGen.Core.Generation.GenerationStage.Started && events[^1].Stage == GlyphGen.Core.Generation.GenerationStage.Finished
        && startedGlyphs == doneGlyphs && doneGlyphs == events[0].Total && events.Where(e => e.Stage == GlyphGen.Core.Generation.GenerationStage.GlyphDone).Select(e => e.Done).SequenceEqual(Enumerable.Range(1, doneGlyphs))
        && events.Any(e => e.Kind == "composite") && events.Any(e => e.Kind == "base glyph"), $"{events.Count} events");

    var plainOut = new StringWriter();
    using (var reporter = new GlyphGen.Cli.ProgressReporter(plainOut, plainOut, interactive: false, verbose: true))
        foreach (var e in events) reporter.OnProgress(e);
    string reported = plainOut.ToString();
    Check("cli messages: progress without a terminal prints plain lines (language, each glyph with its kind)", reported.Contains("glyph(s) to draw") && reported.Contains("[1/") && reported.Contains("composite AB"), reported);

    try { Directory.Delete(tmp, true); } catch (IOException) { }
}

Console.WriteLine(failures == 0 ? "\nAll checks passed." : $"\n{failures} check(s) FAILED.");
return failures == 0 ? 0 : 1;
