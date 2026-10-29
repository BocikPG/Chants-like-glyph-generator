using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlyphGen.Core.Config;

/// <summary>
/// Central place for the JSON options this schema needs: enum-as-string support
/// (ConnectionDirection, CurveMode, MotifType, PlacementMode, ProximityMode, SelfSpacingMode,
/// CombinationBiasMode, ... all serialize as their name, e.g. "Orthogonal", not a numeric value)
/// and the GridPoint converter. Kept here rather than only in the CLI so any future consumer
/// referencing GlyphGen.Core directly as a library gets this for free instead of hitting the same
/// "could not be converted to ConnectionDirection" error.
/// </summary>
public static class GlyphGenJsonOptions
{
    public static JsonSerializerOptions Default { get; } = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(), new GridPointJsonConverter() },
    };
}

/// <summary>Thrown by RootConfigLoader when a config parses but breaks a rule. The message lists
/// every problem found in the failing phase, not just the first.</summary>
public sealed class ConfigValidationException : Exception
{
    public ConfigValidationException(string message) : base(message) { }
}

/// <summary>
/// Loads a config in three steps:
///  1. deserialize (malformed JSON or a wrongly-typed value throws a JsonException);
///  2. structure, on the raw JSON - unknown properties, unexpected nulls, the per-language grid;
///  3. values - ranges, enums, recipes, placement, motifs, spacing, and so on.
/// Structure errors are reported on their own first (they can leave nulls behind that would make
/// the value checks meaningless); fix those, then any value errors are reported together.
/// </summary>
public static class RootConfigLoader
{
    public static RootConfig Load(string json)
    {
        RootConfig? config;
        try
        {
            config = JsonSerializer.Deserialize<RootConfig>(json, GlyphGenJsonOptions.Default);
        }
        catch (JsonException ex)
        {
            throw new JsonException(JsonErrorExplainer.Explain(ex, json, typeof(RootConfig)), ex);
        }

        if (config is null) throw new JsonException("Config deserialized to null.");

        var structural = new List<string>();
        var warnings = new List<string>();
        ConfigStructureChecker.Check(json, structural, warnings);
        if (structural.Count > 0) throw Invalid(structural);
        config.Warnings.AddRange(warnings);

        ResolveRecipeArrangements(config);
        ResolveChainFrom(config);
        ApplyShapeStartSettings(config);
        foreach (var language in config.Languages)
            language.Quality.UndrawnShapeIds = language.Shapes.Where(sh => !sh.IsDrawn).Select(sh => sh.Id).ToHashSet(StringComparer.Ordinal);

        var errors = new List<string>();
        ConfigValidator.Validate(config, errors);
        if (errors.Count > 0) throw Invalid(errors);

        return config;
    }

    /// <summary>A recipe's own arrangement replaces, key by key (crossing, spacing, bias,
    /// candidates), what combination.arrangement says; keys it does not mention are inherited.
    /// Done on the JSON so "not written" can be told apart from "written as the default".</summary>
    private static void ResolveRecipeArrangements(RootConfig config)
    {
        foreach (var language in config.Languages)
        {
            var combination = language.Combination;
            var baseNode = JsonSerializer.SerializeToNode(combination.Arrangement, GlyphGenJsonOptions.Default)!.AsObject();

            foreach (var recipe in combination.Recipes.Values)
            {
                if (recipe.ArrangementJson is not { } raw) continue;

                var merged = (System.Text.Json.Nodes.JsonObject)baseNode.DeepClone();
                foreach (var property in raw.EnumerateObject())
                {
                    string key = char.ToLowerInvariant(property.Name[0]) + property.Name.Substring(1);
                    foreach (var existing in merged.Select(kv => kv.Key).ToList())
                        if (string.Equals(existing, key, StringComparison.OrdinalIgnoreCase)) merged.Remove(existing);
                    merged[key] = System.Text.Json.Nodes.JsonNode.Parse(property.Value.GetRawText());
                }

                recipe.EffectiveArrangement = merged.Deserialize<CombinationArrangementRules>(GlyphGenJsonOptions.Default);
            }
        }
    }

    /// <summary>A shape with chainFrom takes a copy of the named shape's chain block (followed through chainFrom links). A name that
    /// does not exist, a loop, or a source that is not a generative shape is left alone here: the validator reports it.</summary>
    /// <summary>placement.start.onExistingNode.ignoreStartNode is read by the spacing check, which only sees the shape's arrangement.spacing.</summary>
    private static void ApplyShapeStartSettings(RootConfig config)
    {
        foreach (var language in config.Languages)
            foreach (var shape in language.Shapes)
                shape.Arrangement.Spacing.IgnoreStartNode = shape.Placement.Start.OnExistingNode?.IgnoreStartNode ?? false;
    }

    private static void ResolveChainFrom(RootConfig config)
    {
        foreach (var language in config.Languages)
        {
            var byId = new Dictionary<string, ShapeDefinition>(StringComparer.Ordinal);
            foreach (var shape in language.Shapes)
                if (!string.IsNullOrWhiteSpace(shape.Id)) byId.TryAdd(shape.Id, shape);

            ChainRules? SourceChain(ShapeDefinition shape)
            {
                var seen = new HashSet<string>(StringComparer.Ordinal);
                var at = shape;
                while (at.ChainFrom is { } next)
                {
                    if (!seen.Add(at.Id) || !byId.TryGetValue(next, out var source)) return null;
                    at = source;
                }
                return at == shape ? null : at.Chain;
            }

            foreach (var shape in language.Shapes)
            {
                if (shape.ChainFrom is null) continue;
                var chain = SourceChain(shape);
                if (chain is null) continue;
                shape.Chain = JsonSerializer.SerializeToNode(chain, GlyphGenJsonOptions.Default).Deserialize<ChainRules>(GlyphGenJsonOptions.Default)!;
            }
        }
    }

    public static RootConfig LoadFromFile(string path) => Load(File.ReadAllText(path));

    private static ConfigValidationException Invalid(List<string> errors) =>
        new("Invalid config:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", errors));
}
