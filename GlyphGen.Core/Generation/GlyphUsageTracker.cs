namespace GlyphGen.Core.Generation;

/// <summary>Run-wide counter of how many times each glyph name has been used as a combination
/// component - drives the "regenerate least-used dependency first" tie-break on placement
/// failure.</summary>
public sealed class GlyphUsageTracker
{
    private readonly Dictionary<string, int> _counts = new();

    public int GetUsageCount(string glyphName) =>
        _counts.TryGetValue(glyphName, out var c) ? c : 0;

    public void RecordUsage(string glyphName) =>
        _counts[glyphName] = GetUsageCount(glyphName) + 1;
}
