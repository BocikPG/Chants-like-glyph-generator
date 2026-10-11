using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

/// <summary>Generates one glyph: either its own free-form chains/motifs (standalone case),
/// or delegates to ICombinationResolver when the glyph name appears as a recipe key in the
/// language's CombinationConfig. generated is the run's shared, mutable glyph registry - a
/// composite generation may write back into it when a dependency gets regenerated (see
/// ICombinationResolver), so every other recipe referencing that name stays in sync. variation is
/// 0 for a normal build; a quality retry of a composite passes its attempt number so the rebuilt
/// composite is arranged differently (plain glyphs ignore it - their retries change by seed).</summary>
public interface IGlyphGenerator
{
    Glyph GenerateGlyph(
        LanguageDefinition language,
        GlyphDefinition glyphDef,
        LatticeGrid grid,
        ulong glyphSeed,
        Dictionary<string, Glyph> generated,
        GlyphUsageTracker usageTracker,
        int variation = 0,
        ulong languageSeed = 0);
}

/// <summary>Generates every glyph for one language, in dependency-safe order (components
/// before the composites that reference them).</summary>
public interface ILanguageGenerator
{
    IReadOnlyList<Glyph> GenerateAll(LanguageDefinition language, LatticeGrid grid, ulong languageSeed);
}

/// <summary>
/// Top-level entry point for a full run. Plain constructor wiring, no DI container —
/// keeps this trivially usable both from the console host and as a referenced DLL.
/// </summary>
public sealed class RunOrchestrator
{
    private readonly ILanguageGenerator _languageGenerator;

    public RunOrchestrator(ILanguageGenerator languageGenerator)
    {
        _languageGenerator = languageGenerator;
    }

    public IReadOnlyDictionary<string, IReadOnlyList<Glyph>> Run(RootConfig config)
    {
        var result = new Dictionary<string, IReadOnlyList<Glyph>>();

        foreach (var language in config.Languages)
        {
            var grid = new LatticeGrid(language.Grid.Width, language.Grid.Height);
            ulong languageSeed = SeedTree.ForLanguage(config.RootSeed, language.Name);
            result[language.Name] = _languageGenerator.GenerateAll(language, grid, languageSeed);
        }

        return result;
    }
}
