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
    private readonly List<EdgePaths.PathBox> _edgeBoxes = new();
    private readonly List<(double X, double Y)[]?> _edgePaths = new(); // Polyline of each of _edges, filled on first use
    private readonly List<double> _edgeClearance = new(); // spacing.minEdgeDistance of the shape that drew each of _edges
    private readonly List<SpacingConfig?> _edgeSpacing = new(); // spacing (its shape filter) of the shape that drew each of _edges
    private readonly List<string?> _groupShapes = new(); // the shape id of each of _nodeGroups
    private readonly List<(GridPoint Point, string? ShapeId)> _nodes = new();
    private readonly List<List<GridPoint>> _nodeGroups = new();
    private static readonly CrossingConfig NoCrossing = new();

    public GeometryContext() { }
    public GeometryContext(CrossingConfig crossing) => Crossing = crossing;

    /// <summary>Crossing settings of the shape currently being placed. The generator sets this (see SetCurrent) before placing each shape.</summary>
    public CrossingConfig Crossing { get; set; } = NoCrossing;

    /// <summary>Id of the shape currently being placed (null for combination components).</summary>
    public string? CurrentShapeId { get; set; }

    /// <summary>spacing.minEdgeDistance of the shape currently being placed (0 = none).</summary>
    public double CurrentClearance { get; set; }

    private SpacingConfig? _currentSpacing;

    public void SetCurrent(string? shapeId, CrossingConfig crossing, double clearance = 0)
    {
        CurrentShapeId = shapeId;
        Crossing = crossing;
        CurrentClearance = clearance;
        _currentSpacing = null;
    }

    /// <summary>As above for a shape: its spacing gives the clearance and which shapes it is measured against.</summary>
    public void SetCurrent(string? shapeId, CrossingConfig crossing, SpacingConfig spacing)
    {
        SetCurrent(shapeId, crossing, spacing.EffectiveEdgeDistance);
        _currentSpacing = spacing.HasShapeFilter ? spacing : null;
    }

    public IEnumerable<GridEdge> ExistingEdges => _edges;

    public IEnumerable<GridEdge> EdgesOfShapes(IReadOnlyCollection<string> shapeIds)
    {
        for (int i = 0; i < _edges.Count; i++)
            if (_edgeShapes[i] is { } id && shapeIds.Contains(id)) yield return _edges[i];
    }

    /// <summary>The nodes drawn by the given shapes so far.</summary>
    public IEnumerable<GridPoint> NodesOfShapes(IReadOnlyCollection<string> shapeIds)
    {
        foreach (var (point, id) in _nodes)
            if (id is not null && shapeIds.Contains(id)) yield return point;
    }

    public bool IsBlocked(GridEdge edge, IReadOnlyList<GridPoint> ownNodes)
    {
        var current = Crossing;
        (double X, double Y)[]? edgePath = null;
        EdgePaths.PathBox edgeBox = default;
        for (int i = 0; i < _edges.Count; i++)
        {
            var other = _edgeCrossing[i];
            double mine = _currentSpacing is null || _currentSpacing.Covers(_edgeShapes[i]) ? CurrentClearance : 0;
            double theirs = _edgeSpacing[i] is null || _edgeSpacing[i]!.Covers(CurrentShapeId) ? _edgeClearance[i] : 0;
            double clearance = Math.Max(mine, theirs);
            if (clearance > 0)
            {
                if (edgePath is null) { edgePath = EdgePaths.Polyline(edge); edgeBox = EdgePaths.PathBox.Of(edgePath); }
                if (_edgePaths[i] is null) { _edgePaths[i] = EdgePaths.Polyline(_edges[i]); _edgeBoxes[i] = EdgePaths.PathBox.Of(_edgePaths[i]!); }
                if (edgeBox.Near(_edgeBoxes[i], clearance) && EdgePaths.TooClose(edge, edgePath, _edges[i], _edgePaths[i]!, clearance, ownNodes)) return true;
            }
            if (IsAnywhere(current.Mode) && IsAnywhere(other.Mode)) continue;
            if ((current.Mode == CrossingMode.Strict || other.Mode == CrossingMode.Strict) && EdgePaths.Touches(edge, _edges[i])) return true;
            if (!EdgePaths.Crosses(edge, _edges[i])) continue;
            if (current.Mode is CrossingMode.None or CrossingMode.Strict || other.Mode is CrossingMode.None or CrossingMode.Strict) return true;

            foreach (var point in EdgePaths.CrossingPoints(edge, _edges[i]))
            {
                if (current.Mode == CrossingMode.NodesOnly && !NearNode(point, current, ownNodes)) return true;
                if (other.Mode == CrossingMode.NodesOnly && !NearNode(point, other, ownNodes)) return true;
            }
        }
        return false;
    }

    private static bool IsAnywhere(CrossingMode mode) => mode is CrossingMode.Anywhere or CrossingMode.AnywhereButRespectStrict;

    /// <summary>Combination only: gives the crossing settings and clearance a placed component's chain keeps in the combined glyph
    /// (null = every chain uses the arrangement's own). Set by CombinationResolver for CrossingMode.AnywhereButRespectStrict.</summary>
    public Func<SubSymbolChain, (CrossingConfig Crossing, double Clearance)>? ChainRules { get; set; }

    /// <summary>How many edges of the chain are blocked (see <see cref="IsBlocked"/>).</summary>
    public int CountBlocked(SubSymbolChain chain) { var own = chain.AllNodes(); return chain.Edges.Count(e => IsBlocked(e, own)); }

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

    /// <summary>The node groups a shape with this spacing is measured against (all of them unless it has onlyShapes / excludeShapes).</summary>
    public IReadOnlyList<IReadOnlyList<GridPoint>> NodeGroupsFor(SpacingConfig spacing)
    {
        if (!spacing.HasShapeFilter) return _nodeGroups;
        var result = new List<IReadOnlyList<GridPoint>>();
        for (int i = 0; i < _nodeGroups.Count; i++)
            if (spacing.Covers(_groupShapes[i])) result.Add(_nodeGroups[i]);
        return result;
    }

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
        for (int i = 0; i < _edges.Count; i++) { _edges[i] = Moved(_edges[i], dx, dy); _edgePaths[i] = null; }
        for (int i = 0; i < _nodes.Count; i++) _nodes[i] = (_nodes[i].Point.Translate(dx, dy), _nodes[i].ShapeId);
        foreach (var group in _nodeGroups)
            for (int i = 0; i < group.Count; i++) group[i] = group[i].Translate(dx, dy);
    }

    private static GridEdge Moved(GridEdge e, int dx, int dy) => e.WithPoints(e.From.Translate(dx, dy), e.To.Translate(dx, dy));

    private static string? ShapeIdOf(SubSymbolChain chain) =>
        chain.ShapeTag is { } tag ? tag[..Math.Max(0, tag.LastIndexOf('#'))] : null;

    /// <summary>Commits a placed shape together with its own crossing settings (what later shapes must respect).</summary>
    public void Commit(SubSymbolChain chain, CrossingConfig crossing, SpacingConfig spacing) =>
        Commit(chain, crossing, spacing.EffectiveEdgeDistance, spacing.HasShapeFilter ? spacing : null);

    public void Commit(SubSymbolChain chain, CrossingConfig crossing, double clearance = 0, SpacingConfig? filter = null)
    {
        string? shapeId = ShapeIdOf(chain);
        _edges.AddRange(chain.Edges);
        for (int i = 0; i < chain.Edges.Count; i++) { _edgeShapes.Add(shapeId); _edgeCrossing.Add(crossing); _edgeClearance.Add(clearance); _edgeSpacing.Add(filter); _edgePaths.Add(null); _edgeBoxes.Add(default); }
        var all = chain.AllNodes();
        foreach (var n in all) _nodes.Add((n, shapeId));
        _nodeGroups.Add(all);
        _groupShapes.Add(shapeId);
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
            var (chainCrossing, chainClearance) = ChainRules?.Invoke(chain) ?? (crossing, 0.0);
            _edges.AddRange(chain.Edges);
            for (int i = 0; i < chain.Edges.Count; i++) { _edgeShapes.Add(null); _edgeCrossing.Add(chainCrossing); _edgeClearance.Add(chainClearance); _edgeSpacing.Add(null); _edgePaths.Add(null); _edgeBoxes.Add(default); }
            var all = chain.AllNodes();
            foreach (var n in all) _nodes.Add((n, null));
            group.AddRange(all);
        }
        _nodeGroups.Add(group);
        _groupShapes.Add(null);
    }
}
