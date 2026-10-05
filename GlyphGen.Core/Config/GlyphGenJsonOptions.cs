using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlyphGen.Core.Config;

/// <summary>
/// Central place for the JSON options this schema needs — currently just enum-as-string
/// support (ConnectionDirection, CurveMode, MotifType, PlacementMode, ProximityMode,
/// SelfSpacingMode, CombinationBiasMode, ... all serialize as their name, e.g. "Orthogonal", not
/// a numeric value). Kept here rather than only in the CLI so any future consumer referencing
/// GlyphGen.Core directly as a library gets this for free instead of hitting the same "could not
/// be converted to ConnectionDirection" error.
/// </summary>
public static class GlyphGenJsonOptions
{
    public static JsonSerializerOptions Default { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Thrown by RootConfigLoader when a config parses but breaks a semantic rule.
/// Message lists every problem found, not just the first.</summary>
public sealed class ConfigValidationException : Exception
{
    public ConfigValidationException(string message) : base(message) { }
}

public static class RootConfigLoader
{
    public static RootConfig Load(string json)
    {
        var config = JsonSerializer.Deserialize<RootConfig>(json, GlyphGenJsonOptions.Default)
            ?? throw new JsonException("Config deserialized to null.");

        var errors = new List<string>();
        CheckGridPlacement(json, errors);
        Validate(config, errors);

        if (errors.Count > 0)
        {
            throw new ConfigValidationException(
                "Invalid config:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", errors));
        }

        return config;
    }

    public static RootConfig LoadFromFile(string path) => Load(File.ReadAllText(path));

    /// <summary>The grid moved from the root into each language. Checked on the raw JSON, because
    /// after deserialization a missing grid is indistinguishable from the 25x25 default, and an
    /// old top-level "grid" would simply be ignored.</summary>
    private static void CheckGridPlacement(string json, List<string> errors)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (TryGetProperty(root, "grid", out _))
            errors.Add("A top-level \"grid\" is no longer supported - put a \"grid\" block inside each language.");

        if (!TryGetProperty(root, "languages", out var languages) || languages.ValueKind != JsonValueKind.Array)
            return;

        int index = 0;
        foreach (var language in languages.EnumerateArray())
        {
            index++;
            if (language.ValueKind != JsonValueKind.Object) continue;
            if (TryGetProperty(language, "grid", out _)) continue;

            string label = TryGetProperty(language, "name", out var name) && name.ValueKind == JsonValueKind.String
                ? $"'{name.GetString()}'"
                : $"#{index}";
            errors.Add($"Language {label} has no \"grid\" block.");
        }
    }

    private static bool TryGetProperty(JsonElement obj, string name, out JsonElement value)
    {
        foreach (var property in obj.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    private static void Validate(RootConfig config, List<string> errors)
    {
        foreach (var language in config.Languages)
        {
            ValidateGrid(language, errors);
            ValidateGlyphLists(language, errors);

            foreach (var error in language.SubSymbolSpacing?.Validate() ?? Array.Empty<string>())
                errors.Add($"[{language.Name}] subSymbolSpacing: {error}");

            foreach (var error in language.UsageSizeBias?.Validate() ?? Array.Empty<string>())
                errors.Add($"[{language.Name}] usageSizeBias: {error}");

            foreach (var error in language.Combination?.Bias?.Validate() ?? Array.Empty<string>())
                errors.Add($"[{language.Name}] combination.bias: {error}");

            ValidateStartOnExistingNode(language, errors);
        }
    }

    private static void ValidateGrid(LanguageDefinition language, List<string> errors)
    {
        if (language.Grid is null)
        {
            errors.Add($"[{language.Name}] grid must not be null.");
            return;
        }

        if (language.Grid.Width < 1 || language.Grid.Height < 1)
        {
            errors.Add(
                $"[{language.Name}] grid width and height must each be at least 1 " +
                $"(got {language.Grid.Width}x{language.Grid.Height}).");
        }
    }

    /// <summary>Names must be non-empty and unique across glyphs + baseGlyphs, a base glyph can't
    /// itself be built from a recipe, and every recipe component must exist - these would
    /// otherwise surface mid-generation as "has not been generated yet" or a KeyNotFound.</summary>
    private static void ValidateGlyphLists(LanguageDefinition language, List<string> errors)
    {
        var bases = language.BaseGlyphs ?? new List<GlyphDefinition>();
        var glyphs = language.Glyphs ?? new List<GlyphDefinition>();
        var known = new HashSet<string>(StringComparer.Ordinal);

        void CheckList(string label, List<GlyphDefinition> list)
        {
            foreach (var glyph in list)
            {
                if (string.IsNullOrWhiteSpace(glyph?.Name))
                {
                    errors.Add($"[{language.Name}] {label} contains an empty glyph name.");
                    continue;
                }

                if (!known.Add(glyph.Name))
                    errors.Add($"[{language.Name}] glyph name '{glyph.Name}' appears more than once across glyphs/baseGlyphs.");
            }
        }

        CheckList("baseGlyphs", bases);
        CheckList("glyphs", glyphs);

        var recipes = language.Combination?.Recipes;
        if (recipes is null) return;

        foreach (var (recipeName, components) in recipes)
        {
            if (bases.Any(b => b.Name == recipeName))
                errors.Add($"[{language.Name}] '{recipeName}' is a base glyph but also has a combination recipe - base glyphs are components, not composites.");

            foreach (var component in components)
            {
                if (!known.Contains(component))
                    errors.Add($"[{language.Name}] recipe '{recipeName}' references '{component}', which is not listed in glyphs or baseGlyphs.");
            }
        }
    }

    /// <summary>StartOnExistingNode can't work without crossing (a shape starting on an existing
    /// node touches it), can't coexist with a minimum node distance (a shared node is distance 0
    /// by definition), and only affects RandomValid placement - each of those is flagged rather
    /// than silently ignored or made unsatisfiable.</summary>
    private static void ValidateStartOnExistingNode(LanguageDefinition language, List<string> errors)
    {
        var placements = new List<(string Where, PlacementConfig? Placement)>
        {
            ("subSymbolPlacement", language.SubSymbolPlacement),
        };
        foreach (var motif in language.GuaranteedMotifs)
            placements.Add(($"guaranteedMotifs['{motif.Id}'].placement", motif.Placement));

        foreach (var (where, placement) in placements)
        {
            if (placement is null || !placement.StartOnExistingNode) continue;

            if (!language.AllowSymbolsToCross)
                errors.Add($"[{language.Name}] {where}.startOnExistingNode requires allowSymbolsToCross to be true.");

            if (language.SubSymbolSpacing is { Enabled: true, MinNodeDistance: > 0 })
                errors.Add($"[{language.Name}] {where}.startOnExistingNode can never satisfy subSymbolSpacing.minNodeDistance > 0 (a shared node is at distance 0).");

            if (placement.Mode != PlacementMode.RandomValid)
                errors.Add($"[{language.Name}] {where}.startOnExistingNode only applies when mode is RandomValid (is {placement.Mode}).");
        }
    }
}
