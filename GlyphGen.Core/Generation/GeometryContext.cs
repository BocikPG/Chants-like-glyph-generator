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
    private readonly List<CrossingConfig> _edgeCrossing = new(); // crossing settings of the shape that drew each of _edges
    private readonly List<(GridPoint Point, string? ShapeId)> _nodes = new();
    private readonly List<List<GridPoint>> _nodeGroups = new();
    private static readonly CrossingConfig NoCrossing = new();

    public GeometryContext() { }
    public GeometryContext(CrossingConfig crossing) => Crossing = crossing;

    /// <summary>Crossing settings of the shape currently being placed. The generator sets this (see <see cref="SetCurrent"/>) before placing each shape.</summary>
    public CrossingConfig Crossing { get; set; } = NoCrossing;

    /// <summary>Id of the shape currently being placed (null for combination components).</summary>
    public string? CurrentShapeId { get; set; }

    public void SetCurrent(string? shapeId, CrossingConfig crossing)
    {
        CurrentShapeId = shapeId;
        Crossing = crossing;
    }

    public IEnumerable<GridEdge> ExistingEdges => _edges;

    public IEnumerable<GridEdge> EdgesOfShapes(IReadOnlyCollection<string> shapeIds)
    {
        for (int i = 0; i < _edges.Count; i++)
            if (_edgeShapes[i] is { } id && shapeIds.Contains(id)) yield return _edges[i];
    }

    public bool IsBlocked(GridEdge edge, IReadOnlyList<GridPoint> ownNodes)
    {
        var current = Crossing;
        for (int i = 0; i < _edges.Count; i++)
        {
            var other = _edgeCrossing[i];
            if (current.Mode == CrossingMode.Anywhere && other.Mode == CrossingMode.Anywhere) continue;
            if (!EdgePaths.Crosses(edge, _edges[i])) continue;
            if (current.Mode == CrossingMode.None || other.Mode == CrossingMode.None) return true;

            foreach (var point in EdgePaths.CrossingPoints(edge, _edges[i]))
            {
                if (current.Mode == CrossingMode.NodesOnly && !NearNode(point, current, ownNodes)) return true;
                if (other.Mode == CrossingMode.NodesOnly && !NearNode(point, other, ownNodes)) return true;
            }
        }
        return false;
    }

    /// <summary>How many edges of the chain are blocked (see <see cref="IsBlocked"/>).</summary>
    public int CountBlocked(SubSymbolChain chain) => chain.Edges.Count(e => IsBlocked(e, chain.Nodes));

    /// <summary>How many edges of the chain cross anything already placed, whatever the crossing settings say.</summary>
    public int CountCrossings(SubSymbolChain chain) =>
        chain.Edges.Count(e => _edges.Any(x => EdgePaths.Crosses(e, x)));

    private bool NearNode((double X, double Y) point, CrossingConfig by, IReadOnlyList<GridPoint> ownNodes)
    {
        double tolSq = by.NodeTolerance * by.NodeTolerance;
        var listed = by.NodesOfShapes is { Count: > 0 } ? by.NodesOfShapes : null;

        foreach (var (node, shapeId) in _nodes)
        {
            if (listed is not null && (shapeId is null || !listed.Contains(shapeId))) continue;
            double dx = node.X - point.X, dy = node.Y - point.Y;
            if (dx * dx + dy * dy <= tolSq + 1e-9) return true;
        }

        if (listed is null || (CurrentShapeId is not null && listed.Contains(CurrentShapeId)))
            foreach (var node in ownNodes)
            {
                double dx = node.X - point.X, dy = node.Y - point.Y;
                if (dx * dx + dy * dy <= tolSq + 1e-9) return true;
            }

        return false;
    }

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
        for (int i = 0; i < _nodes.Count; i++) _nodes[i] = (_nodes[i].Point.Translate(dx, dy), _nodes[i].ShapeId);
        foreach (var group in _nodeGroups)
            for (int i = 0; i < group.Count; i++) group[i] = group[i].Translate(dx, dy);
    }

    private static GridEdge Moved(GridEdge e, int dx, int dy) => e.WithPoints(e.From.Translate(dx, dy), e.To.Translate(dx, dy));

    private static string? ShapeIdOf(SubSymbolChain chain) =>
        chain.ShapeTag is { } tag ? tag[..Math.Max(0, tag.LastIndexOf('#'))] : null;

    /// <summary>Commits a placed shape together with its own crossing settings (what later shapes must respect).</summary>
    public void Commit(SubSymbolChain chain, CrossingConfig crossing)
    {
        string? shapeId = ShapeIdOf(chain);
        _edges.AddRange(chain.Edges);
        for (int i = 0; i < chain.Edges.Count; i++) { _edgeShapes.Add(shapeId); _edgeCrossing.Add(crossing); }
        foreach (var n in chain.Nodes) _nodes.Add((n, shapeId));
        _nodeGroups.Add(new List<GridPoint>(chain.Nodes));
    }

    /// <summary>
    /// Commits several chains that together form ONE shape - e.g. every chain of a placed
    /// combination component. Edges are added as usual, but the nodes go in as a single merged
    /// group, so SubSymbolSpacingChecker treats the whole component as one shape (one centroid,
    /// one closest-pair measurement) rather than as separate sub-symbols.
    /// </summary>
    public void CommitAsOneShape(IEnumerable<SubSymbolChain> chains, CrossingConfig crossing)
    {
        var group = new List<GridPoint>();
        foreach (var chain in chains)
        {
            _edges.AddRange(chain.Edges);
            for (int i = 0; i < chain.Edges.Count; i++) { _edgeShapes.Add(null); _edgeCrossing.Add(crossing); }
            foreach (var n in chain.Nodes) _nodes.Add((n, null));
            group.AddRange(chain.Nodes);
        }
        _nodeGroups.Add(group);
    }
}
