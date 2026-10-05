using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Mutable IIntersectionContext accumulating every edge committed so far - either during a
/// single glyph's generation (motifs and free-form sub-symbols added one at a time) or during
/// one overlay-composition attempt (components added one at a time). Used to validate each
/// subsequent piece against everything already placed.
///
/// Also tracks each committed shape's node list as its own group (NodeGroups), separate from
/// the flattened edge list ExistingEdges uses - intersection checks only ever need individual
/// edges, but a spacing check (SubSymbolSpacingChecker) needs to know which nodes belong to the
/// SAME shape, e.g. to compute that shape's centroid.
/// </summary>
public sealed class GeometryContext : IIntersectionContext
{
    private readonly List<GridEdge> _edges = new();
    private readonly List<List<GridPoint>> _nodeGroups = new();

    public GeometryContext(bool allowCrossing) => AllowCrossing = allowCrossing;

    public bool AllowCrossing { get; }
    public IEnumerable<GridEdge> ExistingEdges => _edges;
    public IReadOnlyList<IReadOnlyList<GridPoint>> NodeGroups => _nodeGroups;

    public void Commit(SubSymbolChain chain)
    {
        _edges.AddRange(chain.Edges);
        _nodeGroups.Add(new List<GridPoint>(chain.Nodes));
    }

    /// <summary>
    /// Commits several chains that together form ONE shape - e.g. every chain of a placed
    /// combination component. Edges are added as usual, but the nodes go in as a single merged
    /// group, so SubSymbolSpacingChecker treats the whole component as one shape (one centroid,
    /// one closest-pair measurement) rather than as separate sub-symbols.
    /// </summary>
    public void CommitAsOneShape(IEnumerable<SubSymbolChain> chains)
    {
        var group = new List<GridPoint>();
        foreach (var chain in chains)
        {
            _edges.AddRange(chain.Edges);
            group.AddRange(chain.Nodes);
        }
        _nodeGroups.Add(group);
    }

    public void CommitAll(IEnumerable<SubSymbolChain> chains)
    {
        foreach (var chain in chains) Commit(chain);
    }
}
