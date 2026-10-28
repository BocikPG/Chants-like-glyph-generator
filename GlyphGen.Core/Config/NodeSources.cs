namespace GlyphGen.Core.Config;

/// <summary>Which glyphs a glyph is drawn after: the ones its shapes take nodes from (placement.nodes: nodesOfGlyph) and its partners (combination.partners).</summary>
public static class NodeSources
{
    /// <summary>The glyphs whose nodes the shapes drawn in <paramref name="glyph"/> read (never the glyph itself).</summary>
    public static List<string> Of(LanguageDefinition language, string glyph)
    {
        var result = new List<string>();
        var order = language.BaseGlyphs.Concat(language.Glyphs).Select(g => g.Name).Distinct().ToList();
        int own = order.IndexOf(glyph);
        IEnumerable<ShapeDefinition> shapes = language.Shapes.Where(s => s.AppliesTo(glyph));
        if (language.Derivations.TryGetValue(glyph, out var derivation))
            shapes = shapes.Concat(language.Shapes.Where(s => derivation.Steps.Any(st => st.Shapes?.Contains(s.Id) == true)));

        // A glyph drawn against partners comes after them (and is drawn again when one of them is).
        if (language.Combination.Partners.TryGetValue(glyph, out var partners))
            foreach (var partner in partners)
                if (partner != glyph && !result.Contains(partner)) result.Add(partner);

        foreach (var shape in shapes.Distinct())
        {
            foreach (var region in (shape.Placement.Nodes?.Allow ?? new()).Concat(shape.Placement.Nodes?.Forbid ?? new()))
            {
                if (region.NodesOfGlyph is not { } other) continue;
                if (other.Glyph == GlyphNodes.AllGlyphs)
                {
                    for (int i = 0; i < (own < 0 ? order.Count : own); i++)
                        if (!language.Combination.Recipes.ContainsKey(order[i]) && order[i] != glyph && !result.Contains(order[i])) result.Add(order[i]);
                }
                else if (other.Glyph != glyph && !result.Contains(other.Glyph)) result.Add(other.Glyph);
            }
        }

        return result;
    }
}
