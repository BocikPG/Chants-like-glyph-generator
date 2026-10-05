using System.Text.Json;
using System.Text.Json.Serialization;
using GlyphGen.Core.Grid;

namespace GlyphGen.Core.Config;

/// <summary>
/// Reads a grid point from { "x": 7, "y": 9 } or [7, 9]. Needed because GridPoint is an
/// immutable struct: without a converter System.Text.Json builds it from its implicit default
/// constructor, ignores the get-only X/Y, and every point silently comes out as (0,0).
/// Written as an object.
/// </summary>
public sealed class GridPointJsonConverter : JsonConverter<GridPoint>
{
    public override GridPoint Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var element = doc.RootElement;

        switch (element.ValueKind)
        {
            case JsonValueKind.Array:
            {
                if (element.GetArrayLength() != 2)
                    throw new JsonException("A grid point written as an array must be [x, y].");

                var items = element.EnumerateArray().ToArray();
                if (!items[0].TryGetInt32(out int ax) || !items[1].TryGetInt32(out int ay))
                    throw new JsonException("A grid point's x and y must be integers.");
                return new GridPoint(ax, ay);
            }

            case JsonValueKind.Object:
            {
                int? x = null, y = null;
                foreach (var property in element.EnumerateObject())
                {
                    bool isX = string.Equals(property.Name, "x", StringComparison.OrdinalIgnoreCase);
                    bool isY = string.Equals(property.Name, "y", StringComparison.OrdinalIgnoreCase);
                    if (!isX && !isY)
                        throw new JsonException($"Unknown member '{property.Name}' in a grid point; only x and y are allowed.");
                    if (!property.Value.TryGetInt32(out int value))
                        throw new JsonException($"A grid point's {property.Name} must be an integer.");
                    if (isX) x = value; else y = value;
                }

                if (x is null || y is null)
                    throw new JsonException("A grid point needs both x and y.");
                return new GridPoint(x.Value, y.Value);
            }

            default:
                throw new JsonException("A grid point must be written as { \"x\": 0, \"y\": 0 } or [0, 0].");
        }
    }

    public override void Write(Utf8JsonWriter writer, GridPoint value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteNumber("x", value.X);
        writer.WriteNumber("y", value.Y);
        writer.WriteEndObject();
    }
}
