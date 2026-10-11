using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlyphGen.Core.Config;

/// <summary>
/// One combination recipe. Written either as a plain list of component glyph names -
/// "fortress": ["warrior", "protect"] - or as an object with its own arrangement:
/// "fortress": { "components": ["warrior", "protect"], "arrangement": { "allowCrossing": true } }.
/// Whatever the object form's arrangement lists (allowCrossing, spacing, bias) replaces that part
/// of combination.arrangement for this recipe; everything it leaves out is inherited.
/// </summary>
[JsonConverter(typeof(RecipeConfigConverter))]
public sealed class RecipeConfig
{
    /// <summary>Glyph names to overlay, in placement order.</summary>
    public List<string> Components { get; set; } = new();

    /// <summary>This recipe's overrides as written (object form only).</summary>
    public CombinationArrangementRules? Arrangement { get; set; }

    /// <summary>The raw JSON of the override, merged with combination.arrangement when the
    /// config loads (see RootConfigLoader).</summary>
    internal JsonElement? ArrangementJson { get; set; }

    /// <summary>The arrangement this recipe actually uses: the combination-level one with this
    /// recipe's overrides applied. Set by the loader.</summary>
    [JsonIgnore]
    public CombinationArrangementRules? EffectiveArrangement { get; internal set; }
}

public sealed class RecipeConfigConverter : JsonConverter<RecipeConfig>
{
    public override RecipeConfig Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var element = doc.RootElement;

        if (element.ValueKind == JsonValueKind.Array)
        {
            var list = new RecipeConfig();
            foreach (var item in element.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String)
                    throw new JsonException("A recipe's components must be glyph names (strings).");
                list.Components.Add(item.GetString() ?? "");
            }
            return list;
        }

        if (element.ValueKind != JsonValueKind.Object)
            throw new JsonException("A recipe must be a list of glyph names or an object with \"components\".");

        var recipe = new RecipeConfig();
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, "components", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.Array)
                    throw new JsonException("A recipe's \"components\" must be a list of glyph names.");
                foreach (var item in property.Value.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.String)
                        throw new JsonException("A recipe's components must be glyph names (strings).");
                    recipe.Components.Add(item.GetString() ?? "");
                }
            }
            else if (string.Equals(property.Name, "arrangement", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind != JsonValueKind.Object)
                    throw new JsonException("A recipe's \"arrangement\" must be an object.");
                recipe.ArrangementJson = property.Value.Clone();
                recipe.Arrangement = property.Value.Deserialize<CombinationArrangementRules>(options);
            }
            else
            {
                throw new JsonException($"Unknown member '{property.Name}' in a recipe; only components and arrangement are allowed.");
            }
        }
        return recipe;
    }

    public override void Write(Utf8JsonWriter writer, RecipeConfig value, JsonSerializerOptions options)
    {
        if (value.ArrangementJson is null)
        {
            JsonSerializer.Serialize(writer, value.Components, options);
            return;
        }
        writer.WriteStartObject();
        writer.WritePropertyName("components");
        JsonSerializer.Serialize(writer, value.Components, options);
        writer.WritePropertyName("arrangement");
        value.ArrangementJson.Value.WriteTo(writer);
        writer.WriteEndObject();
    }
}
