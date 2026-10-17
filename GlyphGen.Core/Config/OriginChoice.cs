using System.Text.Json;
using System.Text.Json.Serialization;
using GlyphGen.Core.Grid;

namespace GlyphGen.Core.Config;

/// <summary>One or several origins for a Fixed placement. One is used every time; with several, one of them is picked at
/// random for each glyph (and, when a try fails, the next of a shuffled order). Written as a point ({ "x": 7, "y": 9 } or
/// [7, 9]) or as an array of points.</summary>
[JsonConverter(typeof(OriginChoiceJsonConverter))]
public sealed class OriginChoice
{
    public List<GridPoint> Points { get; } = new();

    public OriginChoice() { }
    public OriginChoice(params GridPoint[] points) => Points.AddRange(points);
}

public sealed class OriginChoiceJsonConverter : JsonConverter<OriginChoice>
{
    public override OriginChoice Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var element = doc.RootElement;
        var point = new GridPointJsonConverter();
        var result = new OriginChoice();

        GridPoint ReadPoint(JsonElement e)
        {
            var bytes = System.Text.Encoding.UTF8.GetBytes(e.GetRawText());
            var r = new Utf8JsonReader(bytes);
            r.Read();
            return point.Read(ref r, typeof(GridPoint), options);
        }

        // [x, y] is one point; an array whose items are points is a list of them.
        bool listOfPoints = element.ValueKind == JsonValueKind.Array && element.GetArrayLength() > 0
            && element.EnumerateArray().First().ValueKind is JsonValueKind.Array or JsonValueKind.Object;
        if (listOfPoints)
            foreach (var item in element.EnumerateArray()) result.Points.Add(ReadPoint(item));
        else
            result.Points.Add(ReadPoint(element));

        return result;
    }

    public override void Write(Utf8JsonWriter writer, OriginChoice value, JsonSerializerOptions options)
    {
        var point = new GridPointJsonConverter();
        if (value.Points.Count == 1) { point.Write(writer, value.Points[0], options); return; }
        writer.WriteStartArray();
        foreach (var p in value.Points) point.Write(writer, p, options);
        writer.WriteEndArray();
    }
}
