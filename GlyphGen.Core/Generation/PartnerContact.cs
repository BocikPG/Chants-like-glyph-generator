using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;

namespace GlyphGen.Core.Generation;

/// <summary>combination.partners: whether a glyph drawn against partner glyphs touches each of them.</summary>
internal static class PartnerContact
{
    /// <summary>The first partner of <paramref name="glyph"/> that <paramref name="drawn"/> does not touch (no shared node and no crossing
    /// edge), or null when it touches all of them, has none, or combination.partnerContact is off. A partner that is not drawn yet is
    /// not judged.</summary>
    public static string? FirstUntouched(LanguageDefinition language, string glyph, Glyph drawn, IReadOnlyDictionary<string, Glyph> generated)
    {
        if (!language.Combination.PartnerContact || !language.Combination.Partners.TryGetValue(glyph, out var partners)) return null;

        var own = GlyphGeometryUtils.GetAllChains(drawn).ToList();
        var ownNodes = new HashSet<GridPoint>(own.SelectMany(c => c.AllNodes()));
        var ownEdges = own.SelectMany(c => c.Edges).ToList();

        foreach (var partner in partners)
        {
            if (!generated.TryGetValue(partner, out var other)) continue;
            var chains = GlyphGeometryUtils.GetAllChains(other).ToList();
            if (chains.SelectMany(c => c.AllNodes()).Any(ownNodes.Contains)) continue;

            var edges = chains.SelectMany(c => c.Edges).ToList();
            if (ownEdges.Any(a => edges.Any(b => EdgePaths.Crosses(a, b)))) continue;
            return partner;
        }

        return null;
    }
}
