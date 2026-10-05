using System.Collections;
using System.Reflection;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using GlyphGen.Core.Grid;

namespace GlyphGen.Core.Config;

/// <summary>
/// Generates a JSON Schema (draft-07) for the config straight from the config classes, so an
/// editor can autocomplete options, flag typos (unknown properties) and wrongly-typed values,
/// and show each option's documentation on hover. Because it works by reflection it stays in step
/// with the code: regenerate it whenever an option is added, with
///
///     GlyphGen.Cli --schema config.schema.json
///
/// and point a config at it with  "$schema": "./config.schema.json"  (the loader ignores that key).
///
/// Descriptions come from the XML documentation file the compiler writes next to the assembly
/// (set GenerateDocumentationFile to true in the Core project); without it the schema is the same
/// minus the hover text. The schema covers structure, types, enum values, defaults and a few
/// obvious minimums; the loader's own checks (ConfigValidator) remain the authority on everything
/// else, such as one option's value depending on another.
/// </summary>
public static class ConfigSchemaGenerator
{
    /// <summary>Uses the documentation file next to the Core assembly when it exists.</summary>
    public static string Generate()
    {
        string xml = Path.ChangeExtension(typeof(RootConfig).Assembly.Location, ".xml");
        return Generate(File.Exists(xml) ? xml : null);
    }

    public static string Generate(string? xmlDocumentationPath)
    {
        var builder = new Builder(LoadDocumentation(xmlDocumentationPath));
        var schema = builder.Build();

        return schema.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    // ---------------------------------------------------------------------------------------

    private static readonly Dictionary<string, double> Minimums = new()
    {
        ["GridConfig.Width"] = 1,
        ["GridConfig.Height"] = 1,
        ["CanvasConfig.SizePx"] = 16,
        ["CanvasConfig.MarginPx"] = 0,
        ["PlacementConfig.MaxPlacementAttempts"] = 1,
        ["QualityConfig.MaxRegenerationAttempts"] = 1,
        ["CombinationConfig.MaxRetriesPerRecipe"] = 1,
        ["ConnectionRuleConfig.MinLength"] = 1,
        ["ConnectionRuleConfig.MaxLength"] = 1,
        ["ConnectionRuleConfig.Weight"] = 0,
        ["ScaleFactorConfig.Numerator"] = 1,
        ["ScaleFactorConfig.Denominator"] = 1,
        ["ContactSheetConfig.Columns"] = 1,
    };

    private static readonly Dictionary<Type, string[]> Required = new()
    {
        [typeof(RootConfig)] = new[] { "languages" },
        [typeof(LanguageDefinition)] = new[] { "name", "grid" },
        [typeof(GuaranteedMotifConfig)] = new[] { "id", "type" },
    };

    private static Dictionary<string, string> LoadDocumentation(string? path)
    {
        var docs = new Dictionary<string, string>();
        if (path is null || !File.Exists(path)) return docs;

        foreach (var member in XDocument.Load(path).Descendants("member"))
        {
            string? name = member.Attribute("name")?.Value;
            var summary = member.Element("summary");
            if (name is null || summary is null) continue;

            string text = Flatten(summary);
            if (text.Length > 0) docs[name] = text;
        }

        return docs;
    }

    /// <summary>Plain text out of a &lt;summary&gt;: tags dropped, cref'd names shortened,
    /// whitespace collapsed.</summary>
    private static string Flatten(XElement element)
    {
        var sb = new StringBuilder();
        foreach (var node in element.Nodes())
        {
            switch (node)
            {
                case XText text:
                    sb.Append(text.Value);
                    break;
                case XElement child when child.Attribute("cref") is { } cref:
                    string target = cref.Value;
                    int dot = target.LastIndexOf('.');
                    sb.Append(dot >= 0 ? target[(dot + 1)..] : target.Substring(target.IndexOf(':') + 1));
                    break;
                case XElement child when child.Attribute("name") is { } paramName:
                    sb.Append(paramName.Value);
                    break;
                case XElement child when child.Name.LocalName == "para":
                    sb.Append(' ').Append(Flatten(child)).Append(' ');
                    break;
                case XElement child:
                    sb.Append(Flatten(child));
                    break;
            }
        }

        return string.Join(' ', sb.ToString().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private sealed class Builder
    {
        private readonly Dictionary<string, string> _docs;
        private readonly SortedDictionary<string, JsonNode> _definitions = new(StringComparer.Ordinal);
        private readonly HashSet<Type> _started = new();

        public Builder(Dictionary<string, string> docs) => _docs = docs;

        public JsonObject Build()
        {
            var root = ClassSchema(typeof(RootConfig));

            root["$schema"] = "http://json-schema.org/draft-07/schema#";
            root["title"] = "GlyphGen config";

            // "$schema" itself is allowed at the top level (editors add it; the loader ignores it).
            ((JsonObject)root["properties"]!)["$schema"] = new JsonObject
            {
                ["type"] = "string",
                ["description"] = "Path or URL of this schema, so an editor can attach it.",
            };

            var definitions = new JsonObject();
            foreach (var (name, schema) in _definitions) definitions[name] = schema;
            root["definitions"] = definitions;

            return root;
        }

        private JsonObject ClassSchema(Type type)
        {
            var properties = new JsonObject();
            object? defaults = TryCreate(type);

            foreach (var property in Bindable(type))
            {
                bool nullable = ConfigStructureChecker.MayBeNull.Contains((type, property.Name));
                JsonNode schema = SchemaFor(property.PropertyType, nullable, property);

                if (Minimums.TryGetValue($"{type.Name}.{property.Name}", out double minimum) && schema is JsonObject numeric
                    && numeric["type"]?.GetValue<string>() is "integer" or "number")
                {
                    numeric["minimum"] = minimum;
                }

                string? description = Describe($"P:{type.FullName}.{property.Name}");
                JsonNode? defaultValue = defaults is null ? null : DefaultOf(property, defaults);

                // Draft-07 ignores siblings of a $ref, so a $ref is always wrapped (even with no
                // description yet) - that keeps the schema's structure identical whether or not
                // the XML documentation file is available; only the hover text differs.
                var holder = schema is JsonObject o && o.ContainsKey("$ref")
                    ? new JsonObject { ["allOf"] = new JsonArray(schema) }
                    : (JsonObject)schema;

                if (description is not null) holder["description"] = description;
                if (defaultValue is not null) holder["default"] = defaultValue;
                schema = holder;

                properties[CamelCase(property.Name)] = schema;
            }

            var result = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = properties,
                ["additionalProperties"] = false,
            };

            if (Describe($"T:{type.FullName}") is { } typeDescription)
                result["description"] = typeDescription;

            if (Required.TryGetValue(type, out var required))
                result["required"] = new JsonArray(required.Select(r => (JsonNode)JsonValue.Create(r)!).ToArray());

            return result;
        }

        private JsonNode SchemaFor(Type declared, bool nullable, PropertyInfo? owner)
        {
            var underlying = Nullable.GetUnderlyingType(declared);
            var type = underlying ?? declared;

            JsonNode node = CoreSchema(type, owner);

            if (nullable || underlying is not null)
            {
                node = new JsonObject
                {
                    ["oneOf"] = new JsonArray(node, new JsonObject { ["type"] = "null" }),
                };
            }

            return node;
        }

        private JsonNode CoreSchema(Type type, PropertyInfo? owner)
        {
            if (type == typeof(bool)) return new JsonObject { ["type"] = "boolean" };
            if (type == typeof(string)) return new JsonObject { ["type"] = "string" };
            if (IsInteger(type)) return new JsonObject { ["type"] = "integer" };
            if (type == typeof(double) || type == typeof(float) || type == typeof(decimal))
                return new JsonObject { ["type"] = "number" };

            if (type.IsEnum)
            {
                return new JsonObject
                {
                    ["type"] = "string",
                    ["enum"] = new JsonArray(System.Enum.GetNames(type).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
                };
            }

            // Types with their own converters, written by hand.
            if (type == typeof(GlyphDefinition))
            {
                return new JsonObject
                {
                    ["description"] = "A glyph name, e.g. \"warrior\" (the older { \"name\": \"warrior\" } form also loads).",
                    ["oneOf"] = new JsonArray(
                        new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                        new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject { ["name"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 } },
                            ["required"] = new JsonArray("name"),
                            ["additionalProperties"] = false,
                        }),
                };
            }

            if (type == typeof(GridPoint))
            {
                return new JsonObject
                {
                    ["oneOf"] = new JsonArray(
                        new JsonObject
                        {
                            ["type"] = "object",
                            ["properties"] = new JsonObject
                            {
                                ["x"] = new JsonObject { ["type"] = "integer" },
                                ["y"] = new JsonObject { ["type"] = "integer" },
                            },
                            ["required"] = new JsonArray("x", "y"),
                            ["additionalProperties"] = false,
                        },
                        new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "integer" },
                            ["minItems"] = 2,
                            ["maxItems"] = 2,
                        }),
                };
            }

            if (TryGetDictionary(type, out var keyType, out var valueType))
            {
                var map = new JsonObject
                {
                    ["type"] = "object",
                    ["additionalProperties"] = SchemaFor(valueType, false, null),
                };

                if (keyType.IsEnum)
                {
                    map["propertyNames"] = new JsonObject
                    {
                        ["enum"] = new JsonArray(System.Enum.GetNames(keyType).Select(n => (JsonNode)JsonValue.Create(n)!).ToArray()),
                    };
                }

                // A recipe needs at least one component.
                if (owner is { Name: nameof(CombinationConfig.Recipes) })
                {
                    map["additionalProperties"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                        ["minItems"] = 1,
                    };
                }

                return map;
            }

            if (TryGetItemType(type, out var itemType))
            {
                // Literal motif nodes are [x, y] pairs.
                if (owner is { Name: nameof(LiteralMotifConfig.Nodes) } && owner.DeclaringType == typeof(LiteralMotifConfig))
                {
                    return new JsonObject
                    {
                        ["type"] = "array",
                        ["minItems"] = 2,
                        ["items"] = new JsonObject
                        {
                            ["type"] = "array",
                            ["items"] = new JsonObject { ["type"] = "integer" },
                            ["minItems"] = 2,
                            ["maxItems"] = 2,
                        },
                    };
                }

                // Rotation angles that keep nodes on the grid.
                if (owner is { Name: nameof(RotationConfig.Allowed) or nameof(PlacementConfig.Rotations) })
                {
                    return new JsonObject
                    {
                        ["type"] = "array",
                        ["items"] = new JsonObject
                        {
                            ["type"] = "integer",
                            ["enum"] = new JsonArray(0, 90, 180, 270),
                        },
                    };
                }

                return new JsonObject
                {
                    ["type"] = "array",
                    ["items"] = SchemaFor(itemType, false, null),
                };
            }

            return Reference(type);
        }

        private JsonNode Reference(Type type)
        {
            if (_started.Add(type))
                _definitions[type.Name] = ClassSchema(type);

            return new JsonObject { ["$ref"] = $"#/definitions/{type.Name}" };
        }

        private string? Describe(string key) => _docs.TryGetValue(key, out var text) ? text : null;

        private static JsonNode? DefaultOf(PropertyInfo property, object instance)
        {
            object? value;
            try { value = property.GetValue(instance); }
            catch { return null; }

            return value switch
            {
                bool b => JsonValue.Create(b),
                int i => JsonValue.Create(i),
                long l => JsonValue.Create(l),
                double d when double.IsFinite(d) => JsonValue.Create(d),
                string s => JsonValue.Create(s),
                System.Enum e => JsonValue.Create(e.ToString()),
                _ => null,
            };
        }
    }

    // ---- reflection helpers -------------------------------------------------------------------

    private static IEnumerable<PropertyInfo> Bindable(Type type) =>
        type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.SetMethod is { IsPublic: true });

    private static object? TryCreate(Type type)
    {
        try { return Activator.CreateInstance(type); }
        catch { return null; }
    }

    private static bool IsInteger(Type type) =>
        type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)
        || type == typeof(uint) || type == typeof(ulong) || type == typeof(ushort) || type == typeof(sbyte);

    private static bool TryGetDictionary(Type type, out Type keyType, out Type valueType)
    {
        if (type.IsGenericType && typeof(IDictionary).IsAssignableFrom(type) && type.GetGenericArguments().Length == 2)
        {
            keyType = type.GetGenericArguments()[0];
            valueType = type.GetGenericArguments()[1];
            return true;
        }

        keyType = valueType = typeof(object);
        return false;
    }

    private static bool TryGetItemType(Type type, out Type itemType)
    {
        if (type.IsArray)
        {
            itemType = type.GetElementType()!;
            return true;
        }

        if (type != typeof(string) && type.IsGenericType && typeof(IEnumerable).IsAssignableFrom(type)
            && type.GetGenericArguments().Length == 1)
        {
            itemType = type.GetGenericArguments()[0];
            return true;
        }

        itemType = typeof(object);
        return false;
    }

    private static string CamelCase(string name) =>
        name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name.Substring(1);
}
