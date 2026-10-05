using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using GlyphGen.Core.Model;
using static GlyphGen.Tests.TestHelpers;

namespace GlyphGen.Tests;

/// <summary>Reproducibility: the same config and seed must always give the same glyphs, and the
/// shipped samples must keep generating. Snapshots lock the exact output in once recorded.</summary>
public class RegressionTests
{
    private static string DumpOf(string sampleFile, ulong? seedOverride = null)
    {
        var config = RootConfigLoader.LoadFromFile(ConfigPath(sampleFile));
        if (seedOverride is { } seed) config.RootSeed = seed;
        return Dump(Generate(config));
    }

    [Test]
    public void TheSameSeedGivesIdenticalGlyphs()
    {
        foreach (var sample in new[] { "sample-config.json", "sample-config-advanced.json" })
            Assert.True(DumpOf(sample) == DumpOf(sample), $"{sample} generated different glyphs on a second run");
    }

    /// <summary>The dump for a seed, or null when that seed can't satisfy the sample's constraints
    /// (the basic sample's recipes are tight - spacing on, no scaling or rotation - so some root
    /// seeds legitimately end in "Could not satisfy combination recipe").</summary>
    private static string? TryDump(string sampleFile, ulong seed)
    {
        try { return DumpOf(sampleFile, seed); }
        catch (GlyphGenerationException) { return null; }
    }

    [Test]
    public void ADifferentSeedGivesDifferentGlyphs()
    {
        foreach (var sample in new[] { "sample-config.json", "sample-config-advanced.json" })
        {
            var dumps = new List<string>();
            for (ulong seed = 1; seed <= 60 && dumps.Count < 2; seed++)
                if (TryDump(sample, seed) is { } dump) dumps.Add(dump);

            Assert.Equal(2, dumps.Count, $"{sample}: fewer than two of the first 60 seeds generated successfully");
            Assert.True(dumps[0] != dumps[1], $"{sample}: two different seeds produced the same output");
        }
    }

    [Test]
    public void AddingAGlyphDoesNotChangeTheOthers()
    {
        // A plain glyph's seed depends only on its language and name, so appending one more
        // glyph must leave the existing plain glyphs untouched.
        var baseline = LoadBase(l => l["glyphs"] = new System.Text.Json.Nodes.JsonArray("a", "b", "ab"));
        var extended = LoadBase(l => l["glyphs"] = new System.Text.Json.Nodes.JsonArray("a", "b", "ab", "c"));

        var before = Generate(baseline)["Base"].ToDictionary(g => g.Name);
        var after = Generate(extended)["Base"].ToDictionary(g => g.Name);

        foreach (var name in new[] { "a", "b" })
            Assert.Equal(Describe(before[name]), Describe(after[name]), $"glyph {name}");
    }

    private static string Describe(Glyph glyph) =>
        string.Join(" | ", glyph.Chains.Select(c => string.Join(" ", c.Nodes)));

    [Test]
    public void TheSampleConfigsGenerateEveryGlyphTheyListAcrossManySeeds()
    {
        foreach (var sample in new[] { "sample-config.json", "sample-config-advanced.json" })
        {
            int succeeded = 0;
            var failures = new List<string>();

            for (ulong seed = 1; seed <= 12; seed++)
            {
                var config = RootConfigLoader.LoadFromFile(ConfigPath(sample));
                config.RootSeed = seed;

                IReadOnlyDictionary<string, IReadOnlyList<Glyph>> results;
                try { results = Generate(config); }
                catch (GlyphGenerationException ex)
                {
                    failures.Add($"seed {seed}: {ex.Message}");
                    continue;
                }

                succeeded++;
                foreach (var language in config.Languages)
                    Assert.Equal(language.BaseGlyphs.Count + language.Glyphs.Count, results[language.Name].Count, $"{sample} seed {seed}");
            }

            // Recipes that share a component can legitimately fail on an unlucky seed. The advanced
            // sample is built to avoid that (nearly) always; the basic one only has to work sometimes.
            int required = sample == "sample-config-advanced.json" ? 11 : 1;
            Assert.True(succeeded >= required,
                $"{sample}: only {succeeded}/12 seeds generated (needed {required}).\n  " + string.Join("\n  ", failures));
        }
    }

    [Test] public void SampleConfigSnapshot() => MatchSnapshot("sample-config", DumpOf("sample-config.json"));

    [Test] public void AdvancedSampleSnapshot() => MatchSnapshot("sample-config-advanced", DumpOf("sample-config-advanced.json"));
}
