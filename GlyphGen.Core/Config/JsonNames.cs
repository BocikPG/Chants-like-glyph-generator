namespace GlyphGen.Core.Config;

/// <summary>Config errors should name options the way the JSON spells them (maxDistance), not the
/// way the C# property is spelled (MaxDistance).</summary>
internal static class JsonNames
{
    /// <summary>The properties a config file can set: public and settable, and not hidden from JSON (the [JsonIgnore] ones are
    /// C# shortcuts to an option that lives in a nested block).</summary>
    public static IEnumerable<System.Reflection.PropertyInfo> Bindable(Type type) =>
        type.GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
            .Where(p => p.GetIndexParameters().Length == 0 && p.SetMethod is { IsPublic: true }
                        && !p.IsDefined(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true));

    public static string Of(string propertyName) =>
        propertyName.Length == 0 ? propertyName : char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
}
