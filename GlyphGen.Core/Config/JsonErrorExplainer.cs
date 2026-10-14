using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace GlyphGen.Core.Config;

/// <summary>
/// Turns System.Text.Json's "The JSON value could not be converted to X. Path: ... | LineNumber: n | BytePositionInLine: m"
/// into something a config author can act on: where it is, what was written there, and what is allowed (the list of names
/// for an enum, "a whole number", "an object", and so on). The expected type is found by following the path through the config classes.
/// </summary>
internal static class JsonErrorExplainer
{
    public static string Explain(JsonException ex, string json, Type root)
    {
        long? line = ex.LineNumber is { } l ? l + 1 : null;
        long? col = ex.BytePositionInLine is { } b ? b + 1 : null;
        string where = line is null ? "" : $"line {line}, column {col}";
        string path = ex.Path is { Length: > 0 } p ? Pretty(p) : "";

        bool conversion = ex.Message.StartsWith("The JSON value could not be converted", StringComparison.Ordinal);
        if (!conversion)
        {
            // Our own converters already say what is wrong; add where.
            string baseMessage = ex.Message.Split(" Path: ")[0].TrimEnd();
            string location = string.Join(", ", new[] { path.Length > 0 ? $"at {path}" : "", where }.Where(x => x.Length > 0));
            return location.Length == 0 ? baseMessage : $"{baseMessage} ({location})";
        }

        var type = ex.Path is { } rawPath ? TypeAt(rawPath, root) : null;
        string written = line is null ? "" : WrittenAt(json, (int)line.Value - 1, (int)col!.Value - 1);
        string expected = type is null ? "" : " " + Expectation(type);

        return $"{(path.Length > 0 ? path : "A value")}{(where.Length > 0 ? $" ({where})" : "")} " +
            (written.Length > 0 ? $"has the value {written}, which is not valid here." : "has a value that is not valid here.") + expected;
    }

    private static string Pretty(string path) => path.StartsWith("$.") ? path[2..] : path.TrimStart('$');

    /// <summary>The value that ends at a position (the parser reports the end of the offending token): the last quoted
    /// string or bare word before it on that line.</summary>
    private static string WrittenAt(string json, int lineIndex, int column)
    {
        var lines = json.Split('\n');
        if (lineIndex < 0 || lineIndex >= lines.Length) return "";
        string text = lines[lineIndex].TrimEnd('\r');
        text = text[..Math.Min(text.Length, Math.Max(0, column + 1))];
        var m = Regex.Match(text, "(\"(?:[^\"\\\\]|\\\\.)*\"|[^,:\\s\\[\\]{}]+)\\s*[,}\\]]?\\s*$");
        string value = m.Success ? m.Groups[1].Value.Trim() : "";
        return value.Length is > 0 and <= 60 ? value : "";
    }

    private static string Expectation(Type type)
    {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type.IsEnum) return "It must be one of: " + string.Join(", ", Enum.GetNames(type)) + ".";
        if (type == typeof(bool)) return "It must be true or false.";
        if (type == typeof(string)) return "It must be text, in quotes.";
        if (type == typeof(int) || type == typeof(long) || type == typeof(short) || type == typeof(byte)) return "It must be a whole number.";
        if (type == typeof(double) || type == typeof(float) || type == typeof(decimal)) return "It must be a number.";
        if (type != typeof(string) && typeof(IDictionary).IsAssignableFrom(type)) return "It must be an object of named entries { ... }.";
        if (type != typeof(string) && typeof(IEnumerable).IsAssignableFrom(type)) return "It must be a list [ ... ].";
        if (type.IsClass) return $"It must be an object {{ ... }} ({type.Name}).";
        return "";
    }

    /// <summary>Follows a path such as $.languages[0].shapes[1].copyOf.flip through the config classes.</summary>
    private static Type? TypeAt(string path, Type root)
    {
        var type = root;
        foreach (Match m in Regex.Matches(path.TrimStart('$'), @"\.([A-Za-z_][\w]*)|\[(\d+)\]|\['([^']*)'\]"))
        {
            if (m.Groups[1].Success)
            {
                var prop = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(pr => string.Equals(pr.Name, m.Groups[1].Value, StringComparison.OrdinalIgnoreCase));
                if (prop is null) return null;
                type = prop.PropertyType;
            }
            else
            {
                type = Nullable.GetUnderlyingType(type) ?? type;
                if (type.IsGenericType && type.GetGenericArguments().Length == 1) type = type.GetGenericArguments()[0];
                else if (type.IsGenericType && type.GetGenericArguments().Length == 2) type = type.GetGenericArguments()[1];
                else if (type.IsArray) type = type.GetElementType()!;
                else return null;
            }
        }

        return type;
    }
}
