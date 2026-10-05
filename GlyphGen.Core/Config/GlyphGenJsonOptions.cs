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
        var config = JsonSerializer.Deserialize<RootConfig>(json, GlyphGenJsonOptions.Default)
            ?? throw new JsonException("Config deserialized to null.");

        var structural = new List<string>();
        ConfigStructureChecker.Check(json, structural);
        if (structural.Count > 0) throw Invalid(structural);

        var errors = new List<string>();
        ConfigValidator.Validate(config, errors);
        if (errors.Count > 0) throw Invalid(errors);

        return config;
    }

    public static RootConfig LoadFromFile(string path) => Load(File.ReadAllText(path));

    private static ConfigValidationException Invalid(List<string> errors) =>
        new("Invalid config:" + Environment.NewLine + " - " + string.Join(Environment.NewLine + " - ", errors));
}
