using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using GlyphGen.Core.Config;
using static GlyphGen.Tests.TestHelpers;

namespace GlyphGen.Tests;

/// <summary>The JSON Schema is generated from the config classes; these tests keep the checked-in
/// copy honest so it can't drift when an option is added.</summary>
public class SchemaTests
{
    private static JsonObject Generated() => JsonNode.Parse(ConfigSchemaGenerator.Generate())!.AsObject();

    /// <summary>Finds config.schema.json by walking up from this source file (works from a checkout).</summary>
    private static string? CheckedInSchemaPath([System.Runtime.CompilerServices.CallerFilePath] string here = "")
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(here)!);
        for (int i = 0; i < 6 && dir is not null; i++, dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, "config.schema.json");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>Canonical text: object keys sorted, and hover descriptions dropped (they only exist
    /// when the compiler's XML documentation file is available).</summary>
    private static string Canonical(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject o:
                return "{" + string.Join(",", o.Where(p => p.Key != "description").OrderBy(p => p.Key, StringComparer.Ordinal)
                    .Select(p => JsonSerializer.Serialize(p.Key) + ":" + Canonical(p.Value))) + "}";
            case JsonArray a:
                return "[" + string.Join(",", a.Select(Canonical)) + "]";
            default:
                return node?.ToJsonString() ?? "null";
        }
    }

    [Test]
    public void TheCheckedInSchemaMatchesTheConfigClasses()
    {
        string? path = CheckedInSchemaPath();
        if (path is null)
        {
            Note.Add("config.schema.json not found above the tests folder - skipped");
            return;
        }

        string expected = Canonical(JsonNode.Parse(File.ReadAllText(path)));
        string actual = Canonical(Generated());
        Assert.True(expected == actual,
            "config.schema.json is out of date. Regenerate it with:  GlyphGen.Cli --schema config.schema.json");
    }

    [Test]
    public void TheSchemaStructureDoesNotDependOnTheDocumentationFile() =>
        Assert.True(Canonical(JsonNode.Parse(ConfigSchemaGenerator.Generate(null))) == Canonical(Generated()),
            "with and without the XML documentation the schema should differ only in hover text");

    [Test]
    public void TheSchemaIsClosedSoTyposAreFlagged()
    {
        var schema = Generated();
        Assert.Equal(false, schema["additionalProperties"]!.GetValue<bool>());
        Assert.True(schema["properties"]!.AsObject().ContainsKey("$schema"), "the $schema key must be allowed");
        Assert.Equal(false, schema["definitions"]!["LanguageDefinition"]!["additionalProperties"]!.GetValue<bool>());
    }

    [Test]
    public void RequiredOptionsAreMarked()
    {
        var language = Generated()["definitions"]!["LanguageDefinition"]!;
        Assert.SequenceEqual(new[] { "name", "grid" }, language["required"]!.AsArray().Select(n => n!.GetValue<string>()));
    }

    [Test]
    public void EnumsListTheirValuesAndDefaults()
    {
        var mode = Generated()["definitions"]!["CurveConfig"]!["properties"]!["mode"]!;
        Assert.SequenceEqual(new[] { "Derived", "Explicit", "Hybrid" }, mode["enum"]!.AsArray().Select(n => n!.GetValue<string>()));
        Assert.Equal("Derived", mode["default"]!.GetValue<string>());
    }

    [Test]
    public void EveryBindableOptionAppearsInTheSchema()
    {
        var definitions = Generated()["definitions"]!.AsObject();
        foreach (var type in typeof(RootConfig).Assembly.GetTypes().Where(t => t.Namespace == "GlyphGen.Core.Config" && t.IsClass && t.IsSealed && !t.IsAbstract))
        {
            if (!definitions.TryGetPropertyValue(type.Name, out var definition)) continue; // not a config block

            var properties = definition!["properties"]!.AsObject();
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.SetMethod is { IsPublic: true }))
            {
                string name = char.ToLowerInvariant(p.Name[0]) + p.Name[1..];
                Assert.True(properties.ContainsKey(name), $"{type.Name}.{p.Name} is missing from the schema");
            }
        }
    }

    [Test]
    public void GridPointsAndGlyphsAcceptTheirAlternativeForms()
    {
        var generated = Generated();
        var origin = generated["definitions"]!["PlacementConfig"]!["properties"]!["fixedOrigin"]!.ToJsonString();
        Assert.Contains("\"oneOf\"", origin);
        Assert.Contains("\"minItems\":2", origin.Replace(" ", ""), "an [x, y] array form");

        var glyphs = generated["definitions"]!["LanguageDefinition"]!["properties"]!["glyphs"]!.ToJsonString();
        Assert.Contains("\"string\"", glyphs);
    }

    [Test]
    public void LiteralMotifNodesArePairsAndRotationsAreLatticeAngles()
    {
        var generated = Generated();
        var nodes = generated["definitions"]!["LiteralMotifConfig"]!["properties"]!["nodes"]!.ToJsonString().Replace(" ", "");
        Assert.Contains("\"maxItems\":2", nodes);

        var rotations = generated["definitions"]!["RotationConfig"]!["properties"]!["allowed"]!.ToJsonString().Replace(" ", "");
        Assert.Contains("[0,90,180,270]", rotations);
    }
}
