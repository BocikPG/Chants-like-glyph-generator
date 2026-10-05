namespace GlyphGen.Core.Config;

/// <summary>Config errors should name options the way the JSON spells them (maxDistance), not the
/// way the C# property is spelled (MaxDistance).</summary>
internal static class JsonNames
{
    public static string Of(string propertyName) =>
        propertyName.Length == 0 ? propertyName : char.ToLowerInvariant(propertyName[0]) + propertyName.Substring(1);
}
