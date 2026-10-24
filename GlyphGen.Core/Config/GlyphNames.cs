using System.Text.Json;
using System.Text.Json.Serialization;

namespace GlyphGen.Core.Config;

/// <summary>One glyph name or a list of them ("a" or ["a", "b"]); "*" means every glyph. Used where one rule covers several glyphs.</summary>
[JsonConverter(typeof(GlyphNamesJsonConverter))]
public sealed class GlyphNames
{
    public List<string> Names { get; } = new();

    public GlyphNames() { }
    public GlyphNames(params string[] names) => Names.AddRange(names);

    public static implicit operator GlyphNames(string name) => new(name);

    /// <summary>Whether this names the glyph.</summary>
    public bool Covers(string glyph) => Names.Contains(glyph);

    /// <summary>Whether "*" is one of the names.</summary>
    public bool IsAll => Names.Contains("*");

    public override string ToString() => Names.Count == 1 ? Names[0] : "[" + string.Join(", ", Names) + "]";
}

public sealed class GlyphNamesJsonConverter : JsonConverter<GlyphNames>
{
    public override GlyphNames Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var result = new GlyphNames();
        if (reader.TokenType == JsonTokenType.String) { result.Names.Add(reader.GetString() ?? ""); return result; }
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("A glyph must be written as a name or an array of names.");
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
        {
            if (reader.TokenType != JsonTokenType.String) throw new JsonException("A list of glyphs must contain names (strings) only.");
            result.Names.Add(reader.GetString() ?? "");
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, GlyphNames value, JsonSerializerOptions options)
    {
        if (value.Names.Count == 1) { writer.WriteStringValue(value.Names[0]); return; }
        writer.WriteStartArray();
        foreach (var n in value.Names) writer.WriteStringValue(n);
        writer.WriteEndArray();
    }
}
