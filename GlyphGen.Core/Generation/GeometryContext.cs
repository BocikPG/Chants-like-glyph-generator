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
    private readonly List<EdgePaths.PathBox?> _edgeBoxes = new(); // bounding box of each of _edges, filled on first use
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

    public IEnumerable<GridEdge> ExistingEdges => _partnerEdgeCount == 0 ? _edges : _edges.Skip(_partnerEdgeCount);

    private int _partnerEdgeCount; // the first edges of _edges belong to partner glyphs (combination.partners): obstacles, not part of this glyph
    private readonly List<(GridPoint Point, string Glyph, string? ShapeId)> _partnerNodes = new();
    private readonly List<(string Glyph, GridEdge Edge)> _partnerEdges = new();
    private readonly List<string> _partnerOrder = new(); // the partners in the order they were committed (the order of combination.partners)

    /// <summary>Adds a chain of a partner glyph (combination.partners): its edges block and keep clear of the shapes drawn after it like any
    /// committed edge, its nodes count for NodesOnly crossings and can be started on, but it is not part of this glyph. Call before any
    /// shape of the glyph is committed.</summary>
    public void CommitPartner(string partnerGlyph, SubSymbolChain chain, CrossingConfig crossing, double clearance)
    {
        if (_edges.Count != _partnerEdgeCount) throw new InvalidOperationException("Partner chains must be committed before the glyph's own shapes.");
        string? shapeId = ShapeIdOf(chain);
        if (!_partnerOrder.Contains(partnerGlyph)) _partnerOrder.Add(partnerGlyph);
        foreach (var e in chain.Edges) _partnerEdges.Add((partnerGlyph, e));
        _edges.AddRange(chain.Edges);
        for (int i = 0; i < chain.Edges.Count; i++) { _edgeShapes.Add(null); _edgeCrossing.Add(crossing); _edgeClearance.Add(clearance); _edgeSpacing.Add(null); _edgePaths.Add(null); _edgeBoxes.Add(default); }
        _partnerEdgeCount += chain.Edges.Count;
        foreach (var n in chain.AllNodes())
        {
            _nodes.Add((n, null));
            if (!_partnerNodes.Any(x => x.Point.Equals(n) && x.Glyph == partnerGlyph && x.ShapeId == shapeId)) _partnerNodes.Add((n, partnerGlyph, shapeId));
        }
    }

    public IEnumerable<GridPoint> PartnerNodes(string? partner, IReadOnlyCollection<string>? shapes)
    {
        var seen = new HashSet<GridPoint>();
        foreach (var (point, glyph, shapeId) in _partnerNodes)
        {
            if (partner is not null && glyph != partner) continue;
            if (shapes is { Count: > 0 } && (shapeId is null || !shapes.Contains(shapeId))) continue;
            if (seen.Add(point)) yield return point;
        }
    }

    /// <summary>Whether what this glyph has drawn so far touches the partner: shares a node with it or crosses one of its edges (the same test
    /// as combination.partnerContact).</summary>
    private bool TouchesPartner(string partner)
    {
        var own = ExistingEdges.ToList();
        if (own.Count == 0) return false;
        var theirs = new HashSet<GridPoint>(_partnerNodes.Where(x => x.Glyph == partner).Select(x => x.Point));
        foreach (var e in own)
        {
            if (theirs.Contains(e.From) || theirs.Contains(e.To) || e.ExtraNodes().Any(theirs.Contains)) return true;
        }

        var edges = _partnerEdges.Where(x => x.Glyph == partner).Select(x => x.Edge).ToList();
        return own.Any(a => edges.Any(b => EdgePaths.Crosses(a, b)));
    }

    /// <summary>The nodes a shape with start.onPartnerNode may start on. A named partner: its nodes. Otherwise, with one partner: its nodes;
    /// with several: those of the first partner (in combination.partners order) this glyph has not touched yet, so each start reaches
    /// another partner; when every partner is touched (or the untouched ones have no matching node) the nodes of all of them.
    /// <c>shapesByPartner</c> overrides <c>shapes</c> for the partners it names.</summary>
    public IEnumerable<GridPoint> PartnerStartNodes(PartnerNodeStart start)
    {
        IReadOnlyCollection<string>? ShapesFor(string partner) =>
            start.ShapesByPartner is { } map && map.TryGetValue(partner, out var list) ? list : start.Shapes;

        if (start.Partner is not null) return PartnerNodes(start.Partner, ShapesFor(start.Partner)).ToList();

        if (_partnerOrder.Count > 1)
        {
            foreach (var partner in _partnerOrder.Where(p => !TouchesPartner(p)))
            {
                var nodes = PartnerNodes(partner, ShapesFor(partner)).ToList();
                if (nodes.Count > 0) return nodes;
            }
        }

        var all = new List<GridPoint>();
        var seen = new HashSet<GridPoint>();
        foreach (var partner in _partnerOrder)
            foreach (var n in PartnerNodes(partner, ShapesFor(partner)))
                if (seen.Add(n)) all.Add(n);
        return all;
    }

    /// <summary>True when some partner glyph is committed (a glyph drawn against partners).</summary>
    public bool HasPartners => _partnerEdgeCount > 0 || _partnerNodes.Count > 0;

    public IEnumerable<GridEdge> EdgesOfShapes(IReadOnlyCollection<string> shapeIds)
    {
        for (int i = 0; i < _edges.Count; i++)
            if (_edgeShapes[i] is { } id && shapeIds.Contains(id)) yield return _edges[i];
    }

    /// <summary>The nodes of an already drawn glyph by name ("*" = all earlier plain glyphs), for nodesOfGlyph masks; null where no glyph registry exists.</summary>
    public Func<string, IEnumerable<GridPoint>>? GlyphNodes { get; set; }

    /// <summary>Components committed so far in a composite: their glyph name and transformed chains.</summary>
    public List<(string Glyph, IReadOnlyList<SubSymbolChain> Chains)> PlacedComponents { get; } = new();

    /// <summary>The component being placed must have a node on this mask (see ComponentPlacementRule.Nodes); null = no requirement.</summary>
    public Func<GridPoint, bool>? StartMask { get; set; }

    /// <summary>True: only the component's very first node (first node of its first stroke) is tested; false: any node.</summary>
    public bool StartMaskFirstOnly { get; set; }

    /// <summary>Whether the chains satisfy StartMask.</summary>
    public bool SatisfiesStartMask(IReadOnlyList<SubSymbolChain> chains)
    {
        if (StartMask is null) return true;
        if (StartMaskFirstOnly)
        {
            var first = chains.FirstOrDefault(c => c.AllNodes().Count > 0);
            return first is not null && StartMask(first.AllNodes()[0]);
        }
        return chains.Any(c => c.AllNodes().Any(StartMask));
    }

    /// <summary>The nodes of placed components (all, or only those of the named glyphs), optionally only of the named shapes inside them.</summary>
    public IEnumerable<GridPoint> NodesOfComponents(IReadOnlyCollection<string>? glyphs, IReadOnlyCollection<string>? shapes)
    {
        foreach (var (glyph, chains) in PlacedComponents)
        {
            if (glyphs is { Count: > 0 } && !glyphs.Contains(glyph)) continue;
            foreach (var chain in chains)
            {
                if (shapes is { Count: > 0 })
                {
                    var tag = chain.ShapeTag;
                    int at = tag?.LastIndexOf('#') ?? -1;
                    if (tag is null || !shapes.Contains(at > 0 ? tag[..at] : tag)) continue;
                }
                foreach (var n in chain.AllNodes()) yield return n;
            }
        }
    }

    /// <summary>The nodes drawn by the given shapes so far.</summary>
    public IEnumerable<GridPoint> NodesOfShapes(IReadOnlyCollection<string> shapeIds)
    {
        foreach (var (point, id) in _nodes)
            if (id is not null && shapeIds.Contains(id)) yield return point;
    }

    /// <summary>Bounding box of an edge's path. A straight edge needs no path for it (its ends are the box).</summary>
    private static EdgePaths.PathBox BoxOf(GridEdge e) =>
        (e.Kind == GridEdgeKind.Line && !e.IsCurved) || e.IsDot
            ? new EdgePaths.PathBox(Math.Min(e.From.X, e.To.X), Math.Min(e.From.Y, e.To.Y), Math.Max(e.From.X, e.To.X), Math.Max(e.From.Y, e.To.Y))
            : EdgePaths.PathBox.Of(EdgePaths.Polyline(e));

    private EdgePaths.PathBox BoxOfExisting(int i) => _edgeBoxes[i] ??= BoxOf(_edges[i]);

    public bool IsBlocked(GridEdge edge, IReadOnlyList<GridPoint> ownNodes)
    {
        var current = Crossing;
        (double X, double Y)[]? edgePath = null;
        var edgeBox = BoxOf(edge);
        for (int i = 0; i < _edges.Count; i++)
        {
            var other = _edgeCrossing[i];
            double mine = _currentSpacing is null || _currentSpacing.Covers(_edgeShapes[i]) ? CurrentClearance : 0;
            double theirs = _edgeSpacing[i] is null || _edgeSpacing[i]!.Covers(CurrentShapeId) ? _edgeClearance[i] : 0;
            double clearance = Math.Max(mine, theirs);
            var otherBox = BoxOfExisting(i);
            if (clearance > 0 && edgeBox.Near(otherBox, clearance))
            {
                edgePath ??= EdgePaths.Polyline(edge);
                _edgePaths[i] ??= EdgePaths.Polyline(_edges[i]);
                if (EdgePaths.TooClose(edge, edgePath, _edges[i], _edgePaths[i]!, clearance, ownNodes)) return true;
            }
            // Edges whose boxes are apart (by more than the contact radius) cannot cross or touch.
            if (!edgeBox.Near(otherBox, 0.1)) continue;
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

        if (listed is null || OwnNodesPreFiltered || (CurrentShapeId is not null && listed.Contains(CurrentShapeId)))
            foreach (var node in ownNodes)
            {
                double dx = node.X - point.X, dy = node.Y - point.Y;
                if (dx * dx + dy * dy <= tolSq + 1e-9) return true;
            }

        return false;
    }

    /// <summary>Combination placement: the own nodes handed to IsBlocked were already limited to the shapes in crossing.nodesOfShapes, so they
    /// all count (a component has many shapes, and a single CurrentShapeId cannot say which of them an own node belongs to).</summary>
    public bool OwnNodesPreFiltered { get; set; }

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

    /// <summary>Combination placement: how many contacts (shared nodes / crossings, see ContactKind) the component being placed must
    /// make with the ones already placed. 0 = no requirement.</summary>
    public int RequiredContacts { get; set; }

    public GlyphGen.Core.Config.ContactKind ContactKind { get; set; } = GlyphGen.Core.Config.ContactKind.Either;

    /// <summary>Combination placement: shapes a contact has to involve (arrangement.contact.nodesOfShapes); null = any.</summary>
    public IReadOnlyCollection<string>? ContactShapes { get; set; }

    private static string? ShapeOf(SubSymbolChain chain) =>
        chain.ShapeTag is { } tag && tag.LastIndexOf('#') > 0 ? tag[..tag.LastIndexOf('#')] : null;

    /// <summary>The contacts of a candidate component with what is already placed: nodes it shares, and edges it crosses.</summary>
    public int CountContacts(IEnumerable<SubSymbolChain> chains, GlyphGen.Core.Config.ContactKind kind)
    {
        var list = chains.ToList();
        var listed = ContactShapes is { Count: > 0 } ? ContactShapes : null;
        int count = 0;
        if (kind != GlyphGen.Core.Config.ContactKind.Crossing)
        {
            var placed = new HashSet<GridPoint>(_nodeGroups.SelectMany(g => g));
            var mine = new HashSet<GridPoint>(list.SelectMany(c => c.AllNodes()));
            if (listed is null) count += mine.Count(placed.Contains);
            else
            {
                var placedListed = new HashSet<GridPoint>(_nodes.Where(n => n.ShapeId is not null && listed.Contains(n.ShapeId)).Select(n => n.Point));
                var mineListed = new HashSet<GridPoint>(list.Where(c => ShapeOf(c) is { } id && listed.Contains(id)).SelectMany(c => c.AllNodes()));
                count += mine.Count(p => placed.Contains(p) && (placedListed.Contains(p) || mineListed.Contains(p)));
            }
        }

        if (kind != GlyphGen.Core.Config.ContactKind.Junction)
        {
            foreach (var chain in list)
            {
                bool mineListed = listed is null || (ShapeOf(chain) is { } id && listed.Contains(id));
                foreach (var edge in chain.Edges)
                    for (int i = 0; i < _edges.Count; i++)
                        if ((mineListed || (_edgeShapes[i] is { } other && listed!.Contains(other))) && EdgePaths.Crosses(edge, _edges[i])) count++;
            }
        }

        return count;
    }

    /// <summary>Combination only: middle (bounding-box centre) and side rule of every component placed so far.</summary>
    public List<(double X, double Y, IReadOnlyList<GlyphSide> Sides)> PlacedSides { get; } = new();

    /// <summary>Grid dimensions for the glyph being generated.</summary>
    public int GridWidth { get; set; }
    public int GridHeight { get; set; }

    /// <summary>Moves everything committed so far by whole lattice steps (the glyph is being shifted as a whole).</summary>
    public void Translate(int dx, int dy)
    {
        for (int i = 0; i < _edges.Count; i++) { _edges[i] = Moved(_edges[i], dx, dy); _edgePaths[i] = null; _edgeBoxes[i] = null; }
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
            chainClearance = Math.Max(chainClearance, CurrentClearance); // combination.arrangement.spacing.minEdgeDistance
            _edges.AddRange(chain.Edges);
            string? chainShape = ShapeIdOf(chain); // kept so crossing / contact nodesOfShapes can tell which shape a placed node belongs to
            for (int i = 0; i < chain.Edges.Count; i++) { _edgeShapes.Add(chainShape); _edgeCrossing.Add(chainCrossing); _edgeClearance.Add(chainClearance); _edgeSpacing.Add(null); _edgePaths.Add(null); _edgeBoxes.Add(default); }
            var all = chain.AllNodes();
            foreach (var n in all) _nodes.Add((n, chainShape));
            group.AddRange(all);
        }
        _nodeGroups.Add(group);
        _groupShapes.Add(null);
    }
}
