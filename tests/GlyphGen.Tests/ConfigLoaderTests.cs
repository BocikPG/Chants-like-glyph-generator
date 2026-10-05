using System.Text.Json;
using System.Text.Json.Nodes;
using GlyphGen.Core.Config;
using static GlyphGen.Tests.TestHelpers;

namespace GlyphGen.Tests;

/// <summary>What the loader accepts and rejects, and what it says when it rejects.</summary>
public class ConfigLoaderTests
{
    private static void Rejects(Action<JsonObject> tweak, string expectedText)
    {
        string? message = Rejection(tweak);
        Assert.True(message is not null, $"the config should have been rejected (expected \"{expectedText}\")");
        Assert.Contains(expectedText, message);
    }

    private static void Accepts(Action<JsonObject> tweak)
    {
        string? message = Rejection(tweak);
        Assert.True(message is null, "the config should have been accepted but was rejected:\n" + message);
    }

    private static JsonObject Obj(string json) => JsonNode.Parse(json)!.AsObject();

    // ---- valid configs -----------------------------------------------------------------------

    [Test] public void TheBaseConfigLoads() => Accepts(_ => { });

    [Test]
    public void TheShippedSamplesLoad()
    {
        foreach (var name in new[] { "sample-config.json", "sample-config-advanced.json" })
            RootConfigLoader.LoadFromFile(ConfigPath(name));
    }

    [Test]
    public void AGlyphCanBeWrittenAsAPlainNameOrAnObject()
    {
        var config = LoadBase(l => l["glyphs"] = new JsonArray("a", Obj("{ \"name\": \"b\" }"), "c", "ab"));
        Assert.SequenceEqual(new[] { "a", "b", "c", "ab" }, config.Languages[0].Glyphs.Select(g => g.Name));
    }

    [Test]
    public void PropertyNamesAreCaseInsensitive() =>
        Accepts(l => { l["SubSymbolCount"] = l["subSymbolCount"]!.DeepClone(); l.Remove("subSymbolCount"); });

    [Test]
    public void TheSchemaKeyIsAcceptedAndIgnored()
    {
        var root = BaseJson();
        root["$schema"] = "./config.schema.json";
        Load(root);
    }

    // ---- structure: typos, nulls, the moved grid ---------------------------------------------

    [Test]
    public void AnUnknownPropertyIsReportedWithASuggestion() =>
        Rejects(l => l["subSymbolSpacin"] = Obj("{ \"enabled\": true }"), "Unknown property 'subSymbolSpacin' in languages['Base']. Did you mean 'subSymbolSpacing'?");

    [Test]
    public void AnUnknownNestedPropertyNamesItsPath() =>
        Rejects(l => l["curve"]!["straightLineBais"] = Obj("{ \"min\": 1, \"max\": 2 }"), "Unknown property 'straightLineBais' in languages['Base'].curve");

    [Test]
    public void AnUnknownPropertyInsideAListItemIsFound() =>
        Rejects(l => l["connectionRules"]![0]!["wieght"] = 2, "Unknown property 'wieght'");

    [Test]
    public void AnExplicitNullInPlaceOfABlockIsRejected() =>
        Rejects(l => l["curve"] = null, "curve must not be null");

    [Test]
    public void ANullForANullableOptionIsFine() =>
        Accepts(l => l["subSymbolPlacement"]!["fixedOrigin"] = null);

    [Test]
    public void ATopLevelGridIsRejectedWithAnExplanation()
    {
        var root = BaseJson();
        root["grid"] = Obj("{ \"width\": 15, \"height\": 15 }");
        var error = Assert.Throws<ConfigValidationException>(() => Load(root));
        Assert.Contains("no longer supported", error.Message);
        DoesNotReportAsUnknown(error.Message);
    }

    private static void DoesNotReportAsUnknown(string message) => Assert.DoesNotContain("Unknown property 'grid'", message);

    [Test]
    public void ALanguageWithoutAGridIsRejected() =>
        Rejects(l => l.Remove("grid"), "has no \"grid\" block");

    [Test]
    public void StructureErrorsAreReportedBeforeValueErrors()
    {
        string? message = Rejection(l =>
        {
            l["subSymbolSpacin"] = true;                       // structure
            l["canvas"]!["sizePx"] = 4;                        // value
        });
        Assert.Contains("Unknown property", message);
        Assert.DoesNotContain("sizePx", message, "value errors wait until the structure is fixed");
    }

    [Test]
    public void AllValueErrorsAreReportedTogether()
    {
        string? message = Rejection(l =>
        {
            l["canvas"]!["sizePx"] = 4;
            l["quality"]!["maxRegenerationAttempts"] = 0;
            l["connectionRules"] = new JsonArray();
        });
        Assert.Contains("canvas.sizePx", message);
        Assert.Contains("quality.maxRegenerationAttempts", message);
        Assert.Contains("connectionRules", message);
    }

    // ---- values ------------------------------------------------------------------------------

    [Test] public void ACanvasThatIsTooSmallIsRejected() => Rejects(l => l["canvas"]!["sizePx"] = 8, "canvas.sizePx: must be at least 16");

    [Test] public void AMarginThatLeavesNoRoomIsRejected() =>
        Rejects(l => { l["canvas"]!["sizePx"] = 100; l["canvas"]!["marginPx"] = 60; }, "leaves no room to draw");

    [Test] public void AnEmptyGridIsRejected() => Rejects(l => l["grid"]!["width"] = 0, "grid: width and height must each be at least 1");

    [Test] public void ARangeWithMinAboveMaxIsRejected() =>
        Rejects(l => l["subSymbolCount"] = Obj("{ \"min\": 3, \"max\": 1 }"), "subSymbolCount: max (1) is smaller than min (3)");

    [Test] public void AOneNodeStrokeIsRejected() =>
        Rejects(l => l["subSymbolLength"] = Obj("{ \"min\": 1, \"max\": 4 }"), "a stroke needs at least 2 nodes");

    [Test] public void NoConnectionRulesIsRejectedWhenSubSymbolsAreRequested() =>
        Rejects(l => l["connectionRules"] = new JsonArray(), "connectionRules: needs at least one rule");

    [Test] public void BackwardsRuleLengthsAreRejected() =>
        Rejects(l => { l["connectionRules"]![0]!["minLength"] = 0; l["connectionRules"]![0]!["maxLength"] = -1; }, "minLength must be at least 1");

    [Test] public void RulesThatAllWeighNothingAreRejected() =>
        Rejects(l => { foreach (var r in l["connectionRules"]!.AsArray()) r!["weight"] = 0; }, "every rule has weight 0");

    [Test] public void AnUnknownEnumNumberIsRejected() =>
        Rejects(l => l["curve"]!["mode"] = 42, "curve.mode: has an unknown value");

    [Test] public void AnUnknownEnumNameIsRejectedWhileParsing() =>
        Assert.Throws<JsonException>(() => { var root = BaseJson(); Language(root)["connectionWeighting"] = "Sometimes"; Load(root); });

    [Test] public void AColourThatIsNotHexIsRejected() =>
        Rejects(l => l["render"]!["foreground"] = "white", "'white' is not a hex colour");

    [Test] public void QualityLimitsAreChecked() =>
        Rejects(l => l["quality"] = Obj("{ \"maxSimilarity\": 0, \"compositeMaxSimilarity\": -1, \"maxRegenerationAttempts\": 0 }"),
            "quality.maxSimilarity: must be a finite number above 0");

    // ---- curves and typefaces ----------------------------------------------------------------

    [Test] public void APercentageOverOneHundredIsRejected() =>
        Rejects(l => l["curve"]!["straightLineBias"] = Obj("{ \"min\": 50, \"max\": 150 }"), "curve.straightLineBias");

    [Test] public void TensionAboveItsCapIsRejectedInsteadOfBeingClampedSilently() =>
        Rejects(l => l["curve"]!["tension"]!["options"]!.AsArray().Add(Obj("{ \"min\": 0.4, \"max\": 0.9, \"weight\": 1 }")), "curve.tension.options[1]");

    [Test] public void NegativeStrokeWidthIsRejected() =>
        Rejects(l => l["render"]!["typeface"]!["rectangular"]!["width"] = Obj("{ \"min\": -0.1, \"max\": 0.05 }"), "render.typeface.rectangular.width");

    [Test] public void MiterLimitBelowOneIsRejected() =>
        Rejects(l => l["render"]!["typeface"]!["rectangular"]!["miterLimit"] = 0.5, "render.typeface.rectangular.miterLimit");

    [Test] public void ForceAllEdgesCurvedConflictsWithAStraightLineBias() =>
        Rejects(l => { l["curve"]!["forceAllEdgesCurved"] = true; l["curve"]!["straightLineBias"] = Obj("{ \"min\": 10, \"max\": 20 }"); }, "curve.forceAllEdgesCurved");

    [Test] public void RecheckNeedsTheGuardEnabled() =>
        Rejects(l => l["curve"]!["recheckProximityInCombined"] = true, "needs curve.proximityGuard.enabled");

    [Test] public void ContactSheetColumnsMustBePositive() =>
        Rejects(l => l["render"]!["contactSheet"] = Obj("{ \"columns\": 0 }"), "render.contactSheet.columns");

    // ---- glyphs and recipes ------------------------------------------------------------------

    [Test] public void DuplicateGlyphNamesAreRejected() =>
        Rejects(l => l["glyphs"] = new JsonArray("a", "b", "a", "ab"), "appears more than once");

    [Test] public void ARecipeKeyMustBeListedInGlyphs() =>
        Rejects(l => l["combination"]!["recipes"]!["ghost"] = new JsonArray("a"), "is not listed in glyphs");

    [Test] public void ARecipeComponentMustExist() =>
        Rejects(l => l["combination"]!["recipes"]!["ab"] = new JsonArray("a", "nope"), "references 'nope'");

    [Test] public void AnEmptyRecipeIsRejected() =>
        Rejects(l => l["combination"]!["recipes"]!["ab"] = new JsonArray(), "has no components");

    [Test]
    public void ABaseGlyphCannotHaveARecipe() =>
        Rejects(l =>
        {
            l["baseGlyphs"] = new JsonArray("base1");
            l["combination"]!["recipes"]!["base1"] = new JsonArray("a");
        }, "is a base glyph but also has a recipe");

    [Test]
    public void ANestedRecipeNeedsItsComponentListedFirst()
    {
        void Nest(JsonObject l, params string[] order)
        {
            l["glyphs"] = new JsonArray(order.Select(n => (JsonNode)n!).ToArray());
            l["combination"]!["recipes"] = Obj("{ \"ab\": [\"a\", \"b\"], \"abc\": [\"ab\", \"c\"] }");
        }

        Rejects(l => Nest(l, "a", "b", "c", "abc", "ab"), "must be listed BEFORE 'abc'");
        Accepts(l => Nest(l, "a", "b", "c", "ab", "abc"));
    }

    [Test] public void ARecipeThatUsesItselfIsRejected() =>
        Rejects(l => l["combination"]!["recipes"]!["ab"] = new JsonArray("ab", "a"), "must be listed BEFORE 'ab'");

    [Test] public void ScaleFactorsMustBePositive() =>
        Rejects(l => l["combination"]!["allowedScales"] = new JsonArray(Obj("{ \"numerator\": 1, \"denominator\": 0 }")), "denominator must be positive");

    [Test] public void RotationAnglesMustKeepNodesOnTheGrid() =>
        Rejects(l => l["combination"]!["rotation"]!["allowed"] = new JsonArray(0, 45), "45 is not supported");

    [Test] public void BiasModesThatCancelAreRejected() =>
        Rejects(l => l["combination"]!["bias"] = Obj("{ \"modes\": [\"Tallest\", \"Shortest\"] }"), "cancel each other out");

    [Test] public void BiasWeightsForUnlistedModesAreRejected() =>
        Rejects(l => l["combination"]!["bias"] = Obj("{ \"modes\": [\"Tallest\"], \"weights\": { \"Widest\": 2 } }"), "not in Modes");

    [Test] public void BiasWeightsParseFromAnEnumKeyedObject()
    {
        var config = LoadBase(l => l["combination"]!["bias"] = Obj("{ \"modes\": [\"Tallest\", \"Squarest\"], \"weights\": { \"Tallest\": 3 } }"));
        Assert.Equal(3.0, config.Languages[0].Combination.Bias.WeightOf(CombinationBiasMode.Tallest));
        Assert.Equal(1.0, config.Languages[0].Combination.Bias.WeightOf(CombinationBiasMode.Squarest));
    }

    // ---- motifs and placement ----------------------------------------------------------------

    private static JsonObject LiteralMotif(string id = "m", string nodes = "[[0,0],[2,0]]") =>
        Obj($"{{ \"id\": \"{id}\", \"type\": \"Literal\", \"literal\": {{ \"nodes\": {nodes} }} }}");

    [Test] public void AValidLiteralMotifIsAccepted() => Accepts(l => l["guaranteedMotifs"] = new JsonArray(LiteralMotif()));

    [Test] public void ALiteralMotifNeedsItsBlock() =>
        Rejects(l => l["guaranteedMotifs"] = new JsonArray(Obj("{ \"id\": \"m\", \"type\": \"Literal\" }")), "there is no 'literal' block");

    [Test] public void AMotifWithBothBlocksIsRejected() =>
        Rejects(l =>
        {
            var m = LiteralMotif();
            m["generative"] = Obj("{ \"lengthRange\": { \"min\": 3, \"max\": 4 }, \"connectionRules\": [ { \"direction\": \"Orthogonal\", \"minLength\": 1, \"maxLength\": 1, \"weight\": 1 } ] }");
            l["guaranteedMotifs"] = new JsonArray(m);
        }, "a 'generative' block is also present");

    [Test] public void MalformedLiteralPointsAreRejected() =>
        Rejects(l => l["guaranteedMotifs"] = new JsonArray(LiteralMotif(nodes: "[[0,0],[1],[2,2]]")), "must be an [x, y] pair");

    [Test] public void ARepeatedLiteralPointIsRejected() =>
        Rejects(l => l["guaranteedMotifs"] = new JsonArray(LiteralMotif(nodes: "[[0,0],[1,1],[1,1]]")), "zero-length edge");

    [Test] public void AGenerativeMotifNeedsConnectionRules() =>
        Rejects(l => l["guaranteedMotifs"] = new JsonArray(Obj("{ \"id\": \"g\", \"type\": \"Generative\", \"generative\": { \"lengthRange\": { \"min\": 3, \"max\": 4 } } }")),
            "needs at least one rule");

    [Test] public void DuplicateMotifIdsAreRejected() =>
        Rejects(l => l["guaranteedMotifs"] = new JsonArray(LiteralMotif("m"), LiteralMotif("m")), "id is used by more than one motif");

    [Test] public void FixedPlacementNeedsAnOrigin() =>
        Rejects(l => l["subSymbolPlacement"]!["mode"] = "Fixed", "mode is Fixed but fixedOrigin is not set");

    [Test] public void AFixedOriginOutsideTheGridIsRejected() =>
        Rejects(l => { l["subSymbolPlacement"]!["mode"] = "Fixed"; l["subSymbolPlacement"]!["fixedOrigin"] = Obj("{ \"x\": 99, \"y\": 1 }"); }, "is outside the 15x15 grid");

    [Test] public void AnOriginWithoutFixedModeIsRejectedAsIgnored() =>
        Rejects(l => l["subSymbolPlacement"]!["fixedOrigin"] = Obj("{ \"x\": 1, \"y\": 1 }"), "would be ignored");

    [Test] public void ABadGridPointShapeFailsToParse() =>
        Assert.Throws<JsonException>(() => { var root = BaseJson(); Language(root)["subSymbolPlacement"]!["fixedOrigin"] = Obj("{ \"x\": 3 }"); Load(root); });

    [Test] public void PlacementRotationsOnlyBelongOnLiteralMotifs() =>
        Rejects(l => l["subSymbolPlacement"]!["rotations"] = new JsonArray(0, 90), "only affects Literal motifs");

    [Test]
    public void PlacementRotationsMustBeLatticeAngles() =>
        Rejects(l =>
        {
            var m = LiteralMotif();
            m["placement"] = Obj("{ \"rotations\": [0, 45] }");
            l["guaranteedMotifs"] = new JsonArray(m);
        }, "45 is not supported");

    [Test] public void StartOnExistingNodeNoLongerNeedsCrossing() =>
        Accepts(l => { l["allowSymbolsToCross"] = false; l["subSymbolPlacement"]!["startOnExistingNode"] = true; });

    [Test] public void StartOnExistingNodeConflictsWithAMinimumNodeDistance() =>
        Rejects(l =>
        {
            l["subSymbolPlacement"]!["startOnExistingNode"] = true;
            l["subSymbolSpacing"] = Obj("{ \"enabled\": true, \"minNodeDistance\": 0.5 }");
        }, "shared node is at distance 0");

    // ---- spacing -----------------------------------------------------------------------------

    [Test] public void SelfSpacingRulesAreValidated() =>
        Rejects(l => l["subSymbolSpacing"] = Obj("{ \"enabled\": true, \"selfSpacing\": [ { \"mode\": \"Adjacent\", \"minDistance\": 3, \"maxDistance\": 1, \"lookback\": 0 } ] }"),
            "selfSpacing[0]: maxDistance (1) is smaller than minDistance (3)");

    [Test] public void ANegativeSpacingDistanceIsRejected() =>
        Rejects(l => l["subSymbolSpacing"] = Obj("{ \"enabled\": true, \"minNodeDistance\": -1 }"), "subSymbolSpacing");

    [Test] public void ANegativeUsageStrengthIsRejected() =>
        Rejects(l => l["usageSizeBias"] = Obj("{ \"strength\": -1 }"), "usageSizeBias");

    // ---- file level --------------------------------------------------------------------------

    [Test] public void TwoLanguagesMayNotShareAName()
    {
        var root = BaseJson();
        root["languages"]!.AsArray().Add(root["languages"]![0]!.DeepClone());
        var error = Assert.Throws<ConfigValidationException>(() => Load(root));
        Assert.Contains("name is used by more than one language", error.Message);
    }

    [Test] public void MalformedJsonIsAJsonException() =>
        Assert.Throws<JsonException>(() => RootConfigLoader.Load("{ \"languages\": [ "));
}
