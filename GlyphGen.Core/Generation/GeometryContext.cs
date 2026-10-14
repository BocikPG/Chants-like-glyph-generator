using GlyphGen.Core.Config;
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
    private readonly List<string?> _edgeShapes = new(); // the shape id that drew each of _edges (null = unknown)
    private readonly List<GridEdge> _protectedEdges = new(); // edges of shapes that forbid being crossed
    private readonly List<List<GridPoint>> _nodeGroups = new();

    public GeometryContext() { }
    public GeometryContext(bool allowCrossing) => AllowCrossing = allowCrossing;

    /// <summary>Whether the shape currently being placed may cross other shapes' edges. The glyph
    /// generator sets this before placing each shape.</summary>
    public bool AllowCrossing { get; set; }

    public IEnumerable<GridEdge> ExistingEdges => _edges;

    public IEnumerable<GridEdge> EdgesOfShapes(IReadOnlyCollection<string> shapeIds)
    {
        for (int i = 0; i < _edges.Count; i++)
            if (_edgeShapes[i] is { } id && shapeIds.Contains(id)) yield return _edges[i];
    }

    /// <summary>All edges when the shape being placed forbids crossing; otherwise only those of
    /// shapes that forbid being crossed. Both sides must allow a crossing.</summary>
    public IEnumerable<GridEdge> BlockingEdges => AllowCrossing ? _protectedEdges : _edges;
    public IReadOnlyList<IReadOnlyList<GridPoint>> NodeGroups => _nodeGroups;

    /// <summary>Combination only: the side(s) the component being placed must end up on, compared with the
    /// middles of the components already placed. null = no rule for it.</summary>
    public IReadOnlyList<GlyphSide>? CurrentSides { get; set; }

    /// <summary>Combination only: middle (bounding-box centre) and side rule of every component placed so far.</summary>
    public List<(double X, double Y, IReadOnlyList<GlyphSide> Sides)> PlacedSides { get; } = new();

    /// <summary>Grid dimensions for the glyph being generated.</summary>
    public int GridWidth { get; set; }
    public int GridHeight { get; set; }

    /// <summary>Moves everything committed so far by whole lattice steps (the glyph is being shifted as a whole).</summary>
    public void Translate(int dx, int dy)
    {
        for (int i = 0; i < _edges.Count; i++) _edges[i] = Moved(_edges[i], dx, dy);
        for (int i = 0; i < _protectedEdges.Count; i++) _protectedEdges[i] = Moved(_protectedEdges[i], dx, dy);
        foreach (var group in _nodeGroups)
            for (int i = 0; i < group.Count; i++) group[i] = group[i].Translate(dx, dy);
    }

    private static GridEdge Moved(GridEdge e, int dx, int dy) => e.WithPoints(e.From.Translate(dx, dy), e.To.Translate(dx, dy));

    /// <summary>Commits a placed shape. allowsCrossing is that shape's own permission to be
    /// crossed (and to cross); when false, later shapes can never cross its edges.</summary>
    public void Commit(SubSymbolChain chain, bool allowsCrossing)
    {
        _edges.AddRange(chain.Edges);
        string? shapeId = chain.ShapeTag is { } tag ? tag[..Math.Max(0, tag.LastIndexOf('#'))] : null;
        for (int i = 0; i < chain.Edges.Count; i++) _edgeShapes.Add(shapeId);
        if (!allowsCrossing) _protectedEdges.AddRange(chain.Edges);
        _nodeGroups.Add(new List<GridPoint>(chain.Nodes));
    }

    /// <summary>
    /// Commits several chains that together form ONE shape - e.g. every chain of a placed
    /// combination component. Edges are added as usual, but the nodes go in as a single merged
    /// group, so SubSymbolSpacingChecker treats the whole component as one shape (one centroid,
    /// one closest-pair measurement) rather than as separate sub-symbols.
    /// </summary>
    public void CommitAsOneShape(IEnumerable<SubSymbolChain> chains, bool allowsCrossing)
    {
        var group = new List<GridPoint>();
        foreach (var chain in chains)
        {
            _edges.AddRange(chain.Edges);
            for (int i = 0; i < chain.Edges.Count; i++) _edgeShapes.Add(null);
            if (!allowsCrossing) _protectedEdges.AddRange(chain.Edges);
            group.AddRange(chain.Nodes);
        }
        _nodeGroups.Add(group);
    }
}
