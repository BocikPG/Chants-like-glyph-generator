using System.Collections;
using System.Reflection;
using System.Text.Json;
using GlyphGen.Core.Grid;

namespace GlyphGen.Core.Config;

/// <summary>
/// Structural checks on the RAW json, run before any value validation:
///  - unknown properties (System.Text.Json silently drops them, so a typo like "subSymbolSpacin"
///    would otherwise just vanish), with a "did you mean" hint;
///  - explicit nulls where the C# property isn't nullable (they would crash later);
///  - every language having a "grid" block.
/// Walks the JSON alongside the config classes by reflection, so new options are covered
/// automatically.
/// </summary>
internal static class ConfigStructureChecker
{
    /// <summary>Properties whose C# type is nullable, so an explicit null is meaningful.</summary>
    internal static readonly HashSet<(Type Owner, string Property)> MayBeNull = new()
    {
        (typeof(ShapeDefinition), nameof(ShapeDefinition.Count)),
        (typeof(ShapeDefinition), nameof(ShapeDefinition.Nodes)),
        (typeof(ShapeDefinition), nameof(ShapeDefinition.SeedLabel)),
        (typeof(RecipeConfig), nameof(RecipeConfig.Arrangement)),
        (typeof(ComponentPlacementRule), nameof(ComponentPlacementRule.Offset)),
        (typeof(ComponentPlacementRule), nameof(ComponentPlacementRule.Pick)),
        (typeof(SidePull), nameof(SidePull.Pick)),
        (typeof(PlacementConfig), nameof(PlacementConfig.Side)),
        (typeof(JoinAngleRule), nameof(JoinAngleRule.Outer)),
        (typeof(JoinAngleRule), nameof(JoinAngleRule.Inner)),
        (typeof(TypefaceConfig), nameof(TypefaceConfig.Rectangular)),
        (typeof(TypefaceConfig), nameof(TypefaceConfig.ThickEnds)),
        (typeof(TypefaceConfig), nameof(TypefaceConfig.Brush)),
        (typeof(CombinationArrangementRules), nameof(CombinationArrangementRules.Contact)),
        (typeof(PlacementConfig), nameof(PlacementConfig.FixedOrigin)),
        (typeof(PlacementConfig), nameof(PlacementConfig.Rotations)),
    };

    [ThreadStatic] private static List<string>? _warnings;

    public static void Check(string json, List<string> errors, List<string>? warnings = null)
    {
        _warnings = warnings;
        try { CheckCore(json, errors); }
        finally { _warnings = null; }
    }

    private static void CheckCore(string json, List<string> errors)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add("The config must be a JSON object.");
            return;
        }

        CheckGridPlacement(root, errors);
        WalkObject(root, typeof(RootConfig), "", errors);
    }

    private static void CheckGridPlacement(JsonElement root, List<string> errors)
    {
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

    private static void Walk(JsonElement element, Type declared, string path, List<string> errors)
    {
        var type = Nullable.GetUnderlyingType(declared) ?? declared;
        if (IsLeaf(type)) return;

        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                if (TryGetMapValueType(type, out var valueType))
                {
                    foreach (var entry in element.EnumerateObject())
                        WalkValue(entry.Value, valueType, $"{path}['{entry.Name}']", errors);
                }
                else
                {
                    WalkObject(element, type, path, errors);
                }
                break;

            case JsonValueKind.Array:
            {
                var itemType = GetItemType(type);
                if (itemType is null) return;

                int i = 0;
                foreach (var item in element.EnumerateArray())
                {
                    WalkValue(item, itemType, path + Segment(item, i), errors);
                    i++;
                }
                break;
            }
        }
    }

    private static void WalkValue(JsonElement element, Type type, string path, List<string> errors)
    {
        if (element.ValueKind == JsonValueKind.Null)
        {
            errors.Add($"{path} must not be null.");
            return;
        }
        Walk(element, type, path, errors);
    }

    private static void WalkObject(JsonElement element, Type type, string path, List<string> errors)
    {
        var bindable = JsonNames.Bindable(type)
            .ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);

        foreach (var property in element.EnumerateObject())
        {
            if (!bindable.TryGetValue(property.Name, out var info))
            {
                // "$schema" lets an editor attach config.schema.json; the loader just ignores it.
                if (path.Length == 0 && property.Name == "$schema")
                    continue;

                string hint = Closest(property.Name, bindable.Keys.Select(CamelCase)) is { } best
                    ? $" Did you mean '{best}'?"
                    : "";
                errors.Add($"Unknown property '{property.Name}' in {(path.Length == 0 ? "the top level" : path)}.{hint}");
                continue;
            }

            string childPath = path.Length == 0 ? CamelCase(info.Name) : $"{path}.{CamelCase(info.Name)}";

            if (property.Value.ValueKind == JsonValueKind.Null)
            {
                if (!MayBeNull.Contains((type, info.Name)))
                    errors.Add($"{childPath} must not be null.");
                continue;
            }

            Walk(property.Value, info.PropertyType, childPath, errors);
        }

        if (_warnings is not null && SchemaRules.HasWarnRules(type))
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(element.GetRawText());
            if (node is System.Text.Json.Nodes.JsonObject written)
                _warnings.AddRange(SchemaRules.Warnings(type, written, path));
        }
    }

    /// <summary>Types the walker doesn't descend into: scalars, plus the two types that have
    /// their own JSON converters (which validate their own shape).</summary>
    private static bool IsLeaf(Type type) =>
        type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
        || type == typeof(GlyphDefinition) || type == typeof(GridPoint) || type == typeof(OriginChoice) || type == typeof(GlyphNames) || type == typeof(JsonElement);

    private static bool TryGetMapValueType(Type type, out Type valueType)
    {
        if (type.IsGenericType && typeof(IDictionary).IsAssignableFrom(type) && type.GetGenericArguments().Length == 2)
        {
            valueType = type.GetGenericArguments()[1];
            return true;
        }

        valueType = typeof(object);
        return false;
    }

    private static Type? GetItemType(Type type)
    {
        if (type.IsArray) return type.GetElementType();
        if (type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type) && type.GetGenericArguments().Length == 1)
            return type.GetGenericArguments()[0];
        return null;
    }

    /// <summary>Path segment for an array item: its name/id when it has one, else its index.</summary>
    private static string Segment(JsonElement item, int index)
    {
        if (item.ValueKind == JsonValueKind.Object)
        {
            foreach (var key in new[] { "name", "id" })
            {
                if (TryGetProperty(item, key, out var value) && value.ValueKind == JsonValueKind.String)
                    return $"['{value.GetString()}']";
            }
        }
        return $"[{index}]";
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

    private static string CamelCase(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);

    /// <summary>Nearest candidate by edit distance, if it is close enough to be a plausible typo.</summary>
    private static string? Closest(string name, IEnumerable<string> candidates)
    {
        string? best = null;
        int bestDistance = int.MaxValue;
        foreach (var candidate in candidates)
        {
            int d = EditDistance(name.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (d < bestDistance) { bestDistance = d; best = candidate; }
        }

        return best is not null && bestDistance <= Math.Max(2, name.Length / 3) ? best : null;
    }

    private static int EditDistance(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;

        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }
            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }
}
