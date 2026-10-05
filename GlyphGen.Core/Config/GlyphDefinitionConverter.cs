using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlyphGen.Core.Config;

/// <summary>
/// Lets a glyph be written in JSON as just its name - "glyphs": ["warrior", "weapon"] - while
/// the rest of the code keeps working with GlyphDefinition (so per-glyph overrides can be added
/// to the class later without touching generation code). The older object form
/// { "name": "warrior" } is still accepted when reading.
/// </summary>
public sealed class GlyphDefinitionConverter : JsonConverter<GlyphDefinition>
{
    public override GlyphDefinition Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String)
            return new GlyphDefinition { Name = reader.GetString() ?? "" };

        if (reader.TokenType == JsonTokenType.StartObject)
        {
            using var doc = JsonDocument.ParseValue(ref reader);
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "name", StringComparison.OrdinalIgnoreCase)
                    && property.Value.ValueKind == JsonValueKind.String)
                {
                    return new GlyphDefinition { Name = property.Value.GetString() ?? "" };
                }
            }
            throw new JsonException("A glyph object needs a string \"name\".");
        }

        throw new JsonException("A glyph must be written as its name, e.g. \"warrior\".");
    }

    public override void Write(Utf8JsonWriter writer, GlyphDefinition value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Name);
}
