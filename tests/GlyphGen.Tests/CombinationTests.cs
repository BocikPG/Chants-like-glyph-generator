using System.Reflection;
using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;
using static GlyphGen.Tests.TestHelpers;

namespace GlyphGen.Tests;

public class CombinationTests
{
    private static readonly LatticeGrid Grid = new(15, 15);

    /// <summary>A horizontal bar and a vertical bar, each 4 long, on a 15x15 grid.</summary>
    private static Dictionary<string, Glyph> Bars() => new()
    {
        ["h"] = MakeGlyph("h", Chain((0, 0), (4, 0))),
        ["v"] = MakeGlyph("v", Chain((0, 0), (0, 4))),
    };

    private static Glyph? Combine(CombinationConfig config, ulong seed, int variation = 0, LatticeGrid? grid = null,
        Dictionary<string, Glyph>? parts = null, string[]? names = null)
    {
        parts ??= Bars();
        return new CombinationResolver().TryCombine(
            "hv", names ?? new[] { "h", "v" }, parts, config, grid ?? Grid, new DeterministicRandom(seed),
            new GlyphUsageTracker(), (name, _) => parts[name], new SubSymbolSpacingConfig(), variation);
    }

    private static CombinationConfig WithBias(params CombinationBiasMode[] modes)
    {
        var config = new CombinationConfig();
        config.Bias.Modes.AddRange(modes);
        return config;
    }

    private static (int Width, int Height) Size(Glyph g)
    {
        var (minX, maxX, minY, maxY) = BoundsOf(g);
        return (maxX - minX, maxY - minY);
    }

    // ---- bias --------------------------------------------------------------------------------

    [Test]
    public void TallestMakesTheCompositeAsTallAsTheGridAllows()
    {
        for (ulong seed = 1; seed <= 12; seed++)
            Assert.Equal(14, Size(Combine(WithBias(CombinationBiasMode.Tallest), seed)!).Height, $"seed {seed}");
    }

    [Test]
    public void ShortestMakesItAsShortAsTheComponentsAllow()
    {
        for (ulong seed = 1; seed <= 12; seed++)
            Assert.Equal(4, Size(Combine(WithBias(CombinationBiasMode.Shortest), seed)!).Height, $"seed {seed}");
    }

    [Test]
    public void WidestAndNarrowestPickTheExtremes()
    {
        for (ulong seed = 1; seed <= 12; seed++)
        {
            Assert.Equal(14, Size(Combine(WithBias(CombinationBiasMode.Widest), seed)!).Width, $"widest, seed {seed}");
            Assert.Equal(4, Size(Combine(WithBias(CombinationBiasMode.Narrowest), seed)!).Width, $"narrowest, seed {seed}");
        }
    }

    [Test]
    public void SquarestMakesWidthEqualHeight()
    {
        for (ulong seed = 1; seed <= 12; seed++)
        {
            var (w, h) = Size(Combine(WithBias(CombinationBiasMode.Squarest), seed)!);
            Assert.Equal(w, h, $"seed {seed}");
        }
    }

    [Test]
    public void SeveralModesBlend()
    {
        // Smallest overall box: the 4x4 corner touch.
        for (ulong seed = 1; seed <= 8; seed++)
            Assert.Equal((4, 4), Size(Combine(WithBias(CombinationBiasMode.Shortest, CombinationBiasMode.Narrowest), seed)!));

        // Tall and thin: as tall as the grid allows while only as wide as one bar.
        var (w, h) = Size(Combine(WithBias(CombinationBiasMode.Tallest, CombinationBiasMode.Narrowest), 3)!);
        Assert.Equal((4, 14), (w, h));
    }

    [Test]
    public void WeightsDecideWhichModeWinsWhenTheyConflict()
    {
        // Two horizontal bars. Shortest wants them in one row (height 0, so never square);
        // Squarest wants width == height. Weighting one mode heavily must let it win.
        Dictionary<string, Glyph> Bars2() => new()
        {
            ["p"] = MakeGlyph("p", Chain((0, 0), (4, 0))),
            ["q"] = MakeGlyph("q", Chain((0, 0), (4, 0))),
        };
        var names = new[] { "p", "q" };

        var shortHeavy = WithBias(CombinationBiasMode.Shortest, CombinationBiasMode.Squarest);
        shortHeavy.Bias.Weights[CombinationBiasMode.Shortest] = 10;
        var squareHeavy = WithBias(CombinationBiasMode.Shortest, CombinationBiasMode.Squarest);
        squareHeavy.Bias.Weights[CombinationBiasMode.Squarest] = 10;

        var short1 = Size(Combine(shortHeavy, 5, parts: Bars2(), names: names)!);
        var square1 = Size(Combine(squareHeavy, 5, parts: Bars2(), names: names)!);

        Assert.Equal(0, short1.Height, "a heavy Shortest weight puts both bars in one row");
        Assert.True(square1.Height > 0 && square1.Width == square1.Height, $"a heavy Squarest weight gives a square, got {square1}");
    }

    [Test]
    public void TheBiasNeverBreaksTheNoCrossingRule()
    {
        foreach (var mode in Enum.GetValues<CombinationBiasMode>())
            for (ulong seed = 1; seed <= 6; seed++)
            {
                var composite = Combine(WithBias(mode), seed)!;
                var edges = composite.Components.SelectMany(c => c.TransformedChains.SelectMany(ch => ch.Edges)).ToList();
                Assert.False(edges[0].Intersects(edges[1]), $"{mode} seed {seed}: the two bars cross");
            }
    }

    // ---- crossing and arrangement ------------------------------------------------------------

    private static Dictionary<string, Glyph> FrameAndBar() => new()
    {
        // A square frame that fills a 3x3 grid, and a vertical bar through its middle: with no
        // crossing allowed the bar has nowhere to go (every column overlaps or cuts the frame).
        ["frame"] = MakeGlyph("frame", new SubSymbolChain
        {
            IsClosedLoop = true,
            Nodes = { P(0, 0), P(2, 0), P(2, 2), P(0, 2), P(0, 0) },
            Edges = { Edge(0, 0, 2, 0), Edge(2, 0, 2, 2), Edge(2, 2, 0, 2), Edge(0, 2, 0, 0) },
        }),
        ["bar"] = MakeGlyph("bar", Chain((1, 0), (1, 2))),
    };

    [Test]
    public void ComponentsCannotCrossByDefault()
    {
        var config = new CombinationConfig { MaxRetriesPerRecipe = 2 };
        Assert.True(Combine(config, 1, grid: new LatticeGrid(3, 3), parts: FrameAndBar(), names: new[] { "frame", "bar" }) is null);
    }

    [Test]
    public void AllowComponentsToCrossLetsThemOverlap()
    {
        var config = new CombinationConfig { MaxRetriesPerRecipe = 2, AllowComponentsToCross = true };
        var composite = Combine(config, 1, grid: new LatticeGrid(3, 3), parts: FrameAndBar(), names: new[] { "frame", "bar" });
        Assert.True(composite is not null);
        Assert.Equal(2, composite!.Components.Count);
    }

    [Test]
    public void TheFirstTryPrefersThePlainOverlay()
    {
        for (ulong seed = 1; seed <= 10; seed++)
            Assert.Equal(new GridPoint(0, 0), Combine(new CombinationConfig(), seed)!.Components[0].Translation, $"seed {seed}");
    }

    [Test]
    public void ARetryVariationDropsThatPreferenceSoTheArrangementChanges()
    {
        var firstTranslations = Enumerable.Range(1, 20)
            .Select(s => Combine(new CombinationConfig(), (ulong)s, variation: 1)!.Components[0].Translation)
            .ToList();

        Assert.True(firstTranslations.Any(t => t != new GridPoint(0, 0)), "a varied rebuild should sometimes move the first component");
        Assert.True(firstTranslations.Distinct().Count() > 3, "and should not always land in the same place");
    }

    // ---- weighted fallback order (white-box: OrderedAlternativeCombos is private) -------------

    private static List<(int Numerator, int Denominator, int Rotation)> Order(CombinationConfig config, ulong seed)
    {
        var method = typeof(CombinationResolver).GetMethod("OrderedAlternativeCombos", BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new TestFailedException("CombinationResolver.OrderedAlternativeCombos was renamed or removed - update this test.");
        return (List<(int, int, int)>)method.Invoke(null, new object[] { config, new DeterministicRandom(seed) })!;
    }

    [Test]
    public void HeavierScaleFactorsAreTriedEarlierAndZeroWeightIsExcluded()
    {
        var config = new CombinationConfig
        {
            AllowScale = true,
            AllowedScales =
            {
                new ScaleFactorConfig { Numerator = 1, Denominator = 2, Weight = 8 },
                new ScaleFactorConfig { Numerator = 2, Denominator = 1, Weight = 1 },
                new ScaleFactorConfig { Numerator = 3, Denominator = 1, Weight = 0 },
            },
        };

        int heavyFirst = 0;
        const int trials = 3000;
        for (ulong seed = 0; seed < trials; seed++)
        {
            var order = Order(config, seed);
            Assert.Equal(2, order.Count, "the weight-0 factor must be left out");
            if (order[0] == (1, 2, 0)) heavyFirst++;
        }

        Assert.Near(8.0 / 9.0, heavyFirst / (double)trials, 0.04, "the weight-8 factor should come first about 8 times in 9");
    }

    [Test]
    public void RotationWeightZeroRemovesRotatedCombinations()
    {
        var config = new CombinationConfig { AllowRotate = true, Rotation = { Allowed = { 0, 90, 180 }, Weight = 0 } };
        Assert.Equal(0, Order(config, 1).Count);
    }

    [Test]
    public void EqualWeightsGiveAUniformShuffle()
    {
        var config = new CombinationConfig { AllowRotate = true, Rotation = { Allowed = { 0, 90, 180, 270 }, Weight = 1 } };
        var firsts = new Dictionary<int, int>();
        for (ulong seed = 0; seed < 3000; seed++)
        {
            int first = Order(config, seed)[0].Rotation;
            firsts[first] = firsts.GetValueOrDefault(first) + 1;
        }

        foreach (var rotation in new[] { 90, 180, 270 })
            Assert.Near(1000, firsts[rotation], 120, $"rotation {rotation}");
    }
}
