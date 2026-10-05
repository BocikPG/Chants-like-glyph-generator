using System.Text.Json.Nodes;
using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using static GlyphGen.Tests.TestHelpers;

namespace GlyphGen.Tests;

public class PlacementTests
{
    private static LanguageDefinition BaseLanguage(Action<JsonObject>? tweak = null) => LoadBase(tweak).Languages[0];

    private static JsonObject Motif(JsonObject placement, int[][]? nodes = null)
    {
        var literal = new JsonArray();
        foreach (var n in nodes ?? new[] { new[] { 0, 0 }, new[] { 2, 0 } })
            literal.Add(new JsonArray(n[0], n[1]));

        return new JsonObject
        {
            ["id"] = "m",
            ["type"] = "Literal",
            ["literal"] = new JsonObject { ["nodes"] = literal },
            ["placement"] = placement,
        };
    }

    private static SubSymbolChain MotifChain(Glyph glyph) => glyph.Chains.Single(c => c.MotifId == "m");

    // ---- startOnExistingNode -----------------------------------------------------------------

    private static void AssertSecondChainGrowsFromTheFirst(bool allowCrossing)
    {
        var language = BaseLanguage(l =>
        {
            l["allowSymbolsToCross"] = allowCrossing;
            l["subSymbolCount"] = new JsonObject { ["min"] = 2, ["max"] = 2 };
            l["subSymbolPlacement"]!["startOnExistingNode"] = true;
        });

        int checkedGlyphs = 0;
        for (ulong seed = 1; seed <= 30; seed++)
        {
            Glyph glyph;
            try { glyph = GenerateGlyph(language, "a", seed); }
            catch (GlyphGenerationException) { continue; } // an unlucky seed may not fit

            Assert.Equal(2, glyph.Chains.Count);
            Assert.True(glyph.Chains[0].Nodes.Contains(glyph.Chains[1].Nodes[0]),
                $"seed {seed}: the second chain starts at {glyph.Chains[1].Nodes[0]}, which is not on the first");
            checkedGlyphs++;

            if (!allowCrossing)
                foreach (var a in glyph.Chains[0].Edges)
                    foreach (var b in glyph.Chains[1].Edges)
                        Assert.False(a.Intersects(b), $"seed {seed}: {a} crosses {b} although crossing is off");
        }

        Assert.True(checkedGlyphs >= 15, $"only {checkedGlyphs} of 30 seeds produced a glyph");
    }

    [Test] public void StartOnExistingNodeGrowsFromAnExistingNodeWithCrossingOn() => AssertSecondChainGrowsFromTheFirst(true);

    [Test] public void StartOnExistingNodeAlsoWorksWithCrossingOff() => AssertSecondChainGrowsFromTheFirst(false);

    [Test]
    public void WithoutTheOptionChainsStartAnywhere()
    {
        var language = BaseLanguage(l => l["subSymbolCount"] = new JsonObject { ["min"] = 2, ["max"] = 2 });

        int onExisting = 0, total = 0;
        for (ulong seed = 1; seed <= 40; seed++)
        {
            var glyph = GenerateGlyph(language, "a", seed);
            total++;
            if (glyph.Chains[0].Nodes.Contains(glyph.Chains[1].Nodes[0])) onExisting++;
        }

        Assert.True(onExisting < total / 2, $"{onExisting}/{total} chains started on an existing node by chance");
    }

    // ---- fixed origin and rotation -----------------------------------------------------------

    [Test]
    public void AFixedOriginIsReadFromJsonAndUsed()
    {
        var language = BaseLanguage(l =>
        {
            l["subSymbolCount"] = new JsonObject { ["min"] = 0, ["max"] = 0 };
            l["guaranteedMotifs"] = new JsonArray(Motif(new JsonObject
            {
                ["mode"] = "Fixed", ["fixedOrigin"] = new JsonObject { ["x"] = 7, ["y"] = 9 }, ["allowRotationOnPlace"] = false,
            }));
        });

        var chain = MotifChain(GenerateGlyph(language, "a", 1));
        Assert.Equal(new GridPoint(7, 9), chain.Nodes[0], "the origin must not silently become (0,0)");
        Assert.Equal(new GridPoint(9, 9), chain.Nodes[1]);
    }

    [Test]
    public void AFixedOriginCanAlsoBeWrittenAsAnArray()
    {
        var language = BaseLanguage(l =>
        {
            l["subSymbolCount"] = new JsonObject { ["min"] = 0, ["max"] = 0 };
            l["guaranteedMotifs"] = new JsonArray(Motif(new JsonObject
            {
                ["mode"] = "Fixed", ["fixedOrigin"] = new JsonArray(3, 4), ["allowRotationOnPlace"] = false,
            }));
        });

        Assert.Equal(new GridPoint(3, 4), MotifChain(GenerateGlyph(language, "a", 1)).Nodes[0]);
    }

    private static Glyph RotatedMotif(JsonArray? rotations, bool combinationRotates, params int[] combinationAngles)
    {
        var placement = new JsonObject { ["mode"] = "Fixed", ["fixedOrigin"] = new JsonObject { ["x"] = 5, ["y"] = 5 } };
        if (rotations is not null) placement["rotations"] = rotations;

        var language = BaseLanguage(l =>
        {
            l["subSymbolCount"] = new JsonObject { ["min"] = 0, ["max"] = 0 };
            l["guaranteedMotifs"] = new JsonArray(Motif(placement));
            var combination = l["combination"]!.AsObject();
            combination["allowRotate"] = combinationRotates;
            combination["rotation"] = new JsonObject { ["allowed"] = new JsonArray(combinationAngles.Select(a => (JsonNode)a!).ToArray()), ["weight"] = 1.0 };
        });

        return GenerateGlyph(language, "a", 1);
    }

    [Test]
    public void ThePlacementsOwnRotationsRotateALiteralMotif()
    {
        // (0,0)-(2,0) turned 90 degrees and moved to (5,5) is (5,5)-(5,7).
        var chain = MotifChain(RotatedMotif(new JsonArray(90), combinationRotates: false, 0));
        Assert.Equal(new GridPoint(5, 5), chain.Nodes[0]);
        Assert.Equal(new GridPoint(5, 7), chain.Nodes[1]);
    }

    [Test]
    public void AnEmptyRotationsListNeverRotates()
    {
        var chain = MotifChain(RotatedMotif(new JsonArray(), combinationRotates: true, 90));
        Assert.Equal(new GridPoint(7, 5), chain.Nodes[1], "must stay horizontal even though the combination settings would rotate");
    }

    [Test]
    public void WithoutRotationsTheCombinationSettingsStillApplyAsBefore()
    {
        var chain = MotifChain(RotatedMotif(rotations: null, combinationRotates: true, 90));
        Assert.Equal(new GridPoint(5, 7), chain.Nodes[1]);

        var unrotated = MotifChain(RotatedMotif(rotations: null, combinationRotates: false, 90));
        Assert.Equal(new GridPoint(7, 5), unrotated.Nodes[1]);
    }

    // ---- diagnostics -------------------------------------------------------------------------

    [Test]
    public void AFailedPlacementExplainsWhy()
    {
        // A chain of 12 nodes on a 3x3 grid with no revisits cannot exist.
        var language = BaseLanguage(l =>
        {
            l["grid"] = new JsonObject { ["width"] = 3, ["height"] = 3 };
            l["subSymbolLength"] = new JsonObject { ["min"] = 12, ["max"] = 12 };
            l["subSymbolPlacement"]!["maxPlacementAttempts"] = 6;
        });

        var error = Assert.Throws<GlyphGenerationException>(() => GenerateGlyph(language, "a", 1));
        Assert.Contains("Of 6 attempt(s)", error.Message);
        Assert.Contains("ran out of valid moves", error.Message);
        Assert.Contains("shorter subSymbolLength", error.Message, "should suggest what to change");
    }

    [Test]
    public void SpacingRejectionsAreCountedSeparately()
    {
        var language = BaseLanguage(l =>
        {
            l["subSymbolCount"] = new JsonObject { ["min"] = 2, ["max"] = 2 };
            l["subSymbolSpacing"] = new JsonObject { ["enabled"] = true, ["minCenterDistance"] = 40 };
        });

        var error = Assert.Throws<GlyphGenerationException>(() => GenerateGlyph(language, "a", 1));
        Assert.Contains("broke a subSymbolSpacing rule", error.Message);
        Assert.Contains("relax subSymbolSpacing", error.Message);
    }
}
