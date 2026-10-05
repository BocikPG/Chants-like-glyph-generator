using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using static GlyphGen.Tests.TestHelpers;

namespace GlyphGen.Tests;

public class QualityTests
{
    private static GlyphGen.Core.Model.Glyph Line(string name, int length) =>
        MakeGlyph(name, Chain(Enumerable.Range(0, length).Select(i => (i, 0)).ToArray()));

    [Test]
    public void TheMostSimilarGlyphIsReportedWithItsScore()
    {
        // 6-node row compared with: a 6-node row (identical), a 4-node row (4/6) and a column.
        var candidate = Line("candidate", 6);
        var identical = Line("identical", 6);
        var shorter = Line("shorter", 4);

        var hit = GlyphQualityChecker.FindTooSimilar(candidate, new[] { shorter, identical }, 0.5);
        Assert.True(hit is not null);
        Assert.Equal("identical", hit!.Value.Other.Name);
        Assert.Near(1.0, hit.Value.Similarity, 1e-9);

        var partial = GlyphQualityChecker.FindTooSimilar(candidate, new[] { shorter }, 0.5);
        Assert.Near(4.0 / 6.0, partial!.Value.Similarity, 1e-9);
    }

    [Test]
    public void BelowTheLimitNothingIsReported() =>
        Assert.True(GlyphQualityChecker.FindTooSimilar(Line("a", 6), new[] { Line("b", 3) }, 0.9) is null);

    [Test]
    public void ALimitOfOneOrMoreDisablesTheCheck() =>
        Assert.False(GlyphQualityChecker.IsTooSimilar(Line("a", 6), new[] { Line("b", 6) }, 1.0));

    [Test]
    public void IsTooSimilarAgreesWithFindTooSimilar()
    {
        var a = Line("a", 6);
        var others = new[] { Line("b", 5), Line("c", 2) };
        Assert.Equal(GlyphQualityChecker.FindTooSimilar(a, others, 0.8) is not null, GlyphQualityChecker.IsTooSimilar(a, others, 0.8));
    }

    [Test]
    public void StraightLinesAreRecognisedEvenWhenSplitIntoSeveralChains()
    {
        Assert.True(GlyphQualityChecker.IsTrivialStraightLine(MakeGlyph("x", Chain((0, 0), (2, 0)), Chain((2, 0), (5, 0)))));
        Assert.False(GlyphQualityChecker.IsTrivialStraightLine(MakeGlyph("y", Chain((0, 0), (2, 0), (2, 2)))));
    }

    // ---- through the whole generator ---------------------------------------------------------

    private static Exception? GenerateWith(Action<System.Text.Json.Nodes.JsonObject> tweak, ulong seed = 1)
    {
        var config = LoadBase(l =>
        {
            // Two composites from the same two components: identical unless a retry rearranges one.
            l["glyphs"] = new System.Text.Json.Nodes.JsonArray("a", "b", "ab", "ba");
            l["combination"]!["recipes"] = new System.Text.Json.Nodes.JsonObject
            {
                ["ab"] = new System.Text.Json.Nodes.JsonArray("a", "b"),
                ["ba"] = new System.Text.Json.Nodes.JsonArray("a", "b"),
            };
            tweak(l);
        });
        config.RootSeed = seed;

        try { Generate(config); return null; }
        catch (GlyphGenerationException ex) { return ex; }
    }

    [Test]
    public void CompositesAreNotComparedByDefault() =>
        Assert.True(GenerateWith(_ => { }) is null, "identical composites are fine unless a limit is set");

    [Test]
    public void AStrictCompositeLimitWithNoRetriesRejectsIdenticalComposites()
    {
        var error = GenerateWith(l => l["quality"] = new System.Text.Json.Nodes.JsonObject
        {
            ["maxSimilarity"] = 1.0, ["compositeMaxSimilarity"] = 0.9, ["maxRegenerationAttempts"] = 1,
        });

        Assert.True(error is not null, "the second composite is identical to the first, so a single attempt must fail");
        Assert.Contains("composite", error!.Message);
        Assert.Contains("too similar to 'ab'", error.Message, "should name the composite it clashed with");
    }

    [Test]
    public void RetriesRearrangeAComposite()
    {
        int succeeded = 0;
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var error = GenerateWith(l => l["quality"] = new System.Text.Json.Nodes.JsonObject
            {
                ["maxSimilarity"] = 1.0, ["compositeMaxSimilarity"] = 0.9, ["maxRegenerationAttempts"] = 25,
            }, seed);
            if (error is null) succeeded++;
        }

        Assert.True(succeeded >= 10, $"only {succeeded}/12 seeds found a distinct arrangement - retries aren't varying the placement");
    }

    [Test]
    public void ThePlainGlyphGateNamesTheGlyphItClashedWith()
    {
        var error = GenerateWith(l =>
        {
            // A 2x1 grid has exactly one possible stroke, so every glyph comes out identical and
            // the second one can never pass a similarity limit below 1.
            l["glyphs"] = new System.Text.Json.Nodes.JsonArray("a", "b");
            l["combination"]!["recipes"] = new System.Text.Json.Nodes.JsonObject();
            l["grid"] = new System.Text.Json.Nodes.JsonObject { ["width"] = 2, ["height"] = 1 };
            l["subSymbolCount"] = new System.Text.Json.Nodes.JsonObject { ["min"] = 1, ["max"] = 1 };
            l["subSymbolLength"] = new System.Text.Json.Nodes.JsonObject { ["min"] = 2, ["max"] = 2 };
            l["quality"] = new System.Text.Json.Nodes.JsonObject
            {
                ["maxSimilarity"] = 0.5, ["rejectStraightLineOnly"] = false, ["maxRegenerationAttempts"] = 3,
            };
        });

        Assert.True(error is not null, "two identical glyphs cannot both pass a similarity limit of 0.5");
        Assert.Contains($"too similar to 'a' ({1.00:F2})", error!.Message);
        Assert.Contains("attempt 1:", error.Message);
    }

    // ---- usage-based size bias ---------------------------------------------------------------

    private static double AverageNodes(LanguageDefinition language, string glyph, int seeds = 200) =>
        Enumerable.Range(1, seeds).Average(s => GenerateGlyph(language, glyph, (ulong)s).Chains.Sum(c => c.Nodes.Count));

    private static LanguageDefinition UsageLanguage(double strength) => LoadBase(l =>
    {
        l["glyphs"] = new System.Text.Json.Nodes.JsonArray("heavy", "light", "x", "y", "z", "hx", "hy", "hz");
        l["subSymbolCount"] = new System.Text.Json.Nodes.JsonObject { ["min"] = 1, ["max"] = 4 };
        l["subSymbolLength"] = new System.Text.Json.Nodes.JsonObject { ["min"] = 3, ["max"] = 8 };
        l["combination"]!["recipes"] = new System.Text.Json.Nodes.JsonObject
        {
            ["hx"] = new System.Text.Json.Nodes.JsonArray("heavy", "x"),
            ["hy"] = new System.Text.Json.Nodes.JsonArray("heavy", "y"),
            ["hz"] = new System.Text.Json.Nodes.JsonArray("heavy", "z"),
        };
        l["usageSizeBias"] = new System.Text.Json.Nodes.JsonObject { ["strength"] = strength };
    }).Languages[0];

    [Test]
    public void ABusyComponentRollsSmallerThanItDidWithoutTheBias()
    {
        double without = AverageNodes(UsageLanguage(0), "heavy");
        double with = AverageNodes(UsageLanguage(2), "heavy");
        Assert.True(with < without - 2, $"average nodes {with:0.0} (bias) vs {without:0.0} (none) - expected a clear drop");
    }

    [Test]
    public void AGlyphUsedInNoRecipeIsUnaffectedByTheBias()
    {
        var off = UsageLanguage(0);
        var on = UsageLanguage(3);
        for (ulong seed = 1; seed <= 40; seed++)
            Assert.Equal(
                string.Join(" ", GenerateGlyph(off, "light", seed).Chains.SelectMany(c => c.Nodes)),
                string.Join(" ", GenerateGlyph(on, "light", seed).Chains.SelectMany(c => c.Nodes)),
                $"seed {seed}");
    }

    [Test]
    public void StrengthZeroIsExactlyTheUnbiasedGeneration()
    {
        var zero = UsageLanguage(0);
        var plain = LoadBase(l =>
        {
            l["glyphs"] = new System.Text.Json.Nodes.JsonArray("heavy", "light", "x", "y", "z", "hx", "hy", "hz");
            l["subSymbolCount"] = new System.Text.Json.Nodes.JsonObject { ["min"] = 1, ["max"] = 4 };
            l["subSymbolLength"] = new System.Text.Json.Nodes.JsonObject { ["min"] = 3, ["max"] = 8 };
            l["combination"]!["recipes"] = new System.Text.Json.Nodes.JsonObject
            {
                ["hx"] = new System.Text.Json.Nodes.JsonArray("heavy", "x"),
                ["hy"] = new System.Text.Json.Nodes.JsonArray("heavy", "y"),
                ["hz"] = new System.Text.Json.Nodes.JsonArray("heavy", "z"),
            };
            l.Remove("usageSizeBias");
        }).Languages[0];

        for (ulong seed = 1; seed <= 40; seed++)
            Assert.Equal(
                string.Join(" ", GenerateGlyph(plain, "heavy", seed).Chains.SelectMany(c => c.Nodes)),
                string.Join(" ", GenerateGlyph(zero, "heavy", seed).Chains.SelectMany(c => c.Nodes)),
                $"seed {seed}");
    }

    // ---- connection weighting ----------------------------------------------------------------

    private static double DiagonalShare(ConnectionWeighting mode)
    {
        var grid = new GlyphGen.Core.Grid.LatticeGrid(41, 41);
        var generator = new ChainGenerator();
        int diagonal = 0;
        const int trials = 5000;

        for (ulong seed = 0; seed < trials; seed++)
        {
            var request = new ChainGenerationRequest
            {
                StartNode = new GlyphGen.Core.Grid.GridPoint(20, 20),
                TargetLength = 2,
                ConnectionWeighting = mode,
                ConnectionRules =
                {
                    // 20 orthogonal moves against 4 diagonal ones, equal weights.
                    new ConnectionRuleConfig { Direction = ConnectionDirection.Orthogonal, MinLength = 1, MaxLength = 5, Weight = 1 },
                    new ConnectionRuleConfig { Direction = ConnectionDirection.Diagonal, MinLength = 1, MaxLength = 1, Weight = 1 },
                },
            };

            var chain = generator.TryGenerateChain(grid, request, new GlyphGen.Core.Rng.DeterministicRandom(seed), new GeometryContext(true))!;
            if (chain.Nodes[0].X != chain.Nodes[1].X && chain.Nodes[0].Y != chain.Nodes[1].Y) diagonal++;
        }

        return diagonal / (double)trials;
    }

    [Test] public void PerMoveWeightingLetsAWideRuleDominate() => Assert.Near(4.0 / 24.0, DiagonalShare(ConnectionWeighting.PerMove), 0.03);

    [Test] public void PerRuleWeightingMakesEqualWeightsMeanEqualOdds() => Assert.Near(0.5, DiagonalShare(ConnectionWeighting.PerRule), 0.03);
}
