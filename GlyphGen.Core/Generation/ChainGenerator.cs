using System.Linq;
using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Default IChainGenerator implementation. At every step, every geometrically valid concrete
/// move is enumerated across all of the language's weighted ConnectionRules (plus an optional
/// loop-closing move back to the start), and one is picked via weighted random selection. If no
/// valid move exists from the current tail, the chain simply ends early — there is no
/// backtracking past already-committed nodes, since a chain is a linear self-avoiding walk, not
/// a tree. The caller is responsible for retrying the whole chain with the next deterministic
/// attempt seed if the result comes back too short.
/// </summary>
public sealed class ChainGenerator : IChainGenerator
{
    private static readonly (int Dx, int Dy)[] OrthogonalDirections =
    {
        (1, 0), (-1, 0), (0, 1), (0, -1),
    };

    private static readonly (int Dx, int Dy)[] DiagonalDirections =
    {
        (1, 1), (1, -1), (-1, 1), (-1, -1),
    };

    private static readonly (int Dx, int Dy)[] BothDirections =
        OrthogonalDirections.Concat(DiagonalDirections).ToArray();

    /// <summary>Default IChainGenerator implementation. At every step, every geometrically valid concrete
    /// move is enumerated across all of the language's weighted ConnectionRules (plus an optional
    /// loop-closing move back to the start), and one is picked via weighted random selection. If no
    /// valid move exists from the current tail, the chain simply ends early — there is no
    /// backtracking past already-committed nodes, since a chain is a linear self-avoiding walk, not
    /// a tree. The caller is responsible for retrying the whole chain with the next deterministic
    /// attempt seed if the result comes back too short.
    /// </summary>
    public SubSymbolChain? TryGenerateChain(
        LatticeGrid grid,
        ChainGenerationRequest request,
        DeterministicRandom rng,
        IIntersectionContext existingGeometry)
    {
        var chain = new SubSymbolChain();
        chain.Nodes.Add(request.StartNode);
        var current = request.StartNode;

        // TargetLength is interpreted as a node count (including the start node), matching
        // the "nodes per chain" framing used for subSymbolLength in the config schema. Each drawn move uses up
        // its rule's ChainLengthCost of it (1 by default, so that is the node count unless a rule says otherwise).
        int used = 1;
        const int MaxNodes = 64;
        while (used < request.TargetLength && chain.Nodes.Count < MaxNodes)
        {
            var moves = new List<Move>();
            var weights = new List<double>();
            CollectMoveCandidates(grid, request, chain, current, existingGeometry, moves, weights);

            // Going back to the start is not a move of its own: it is a chance attached to whichever rule
            // got picked (BackToStartChance). With nothing left to pick, the best chance of any rule is used.
            bool canClose = request.AllowClosedLoop
                && chain.Nodes.Count >= 3
                && request.ConnectionRules.Any(r => r.BackToStartChance > 0)
                && IsValidEdge(new GridEdge(current, request.StartNode), chain, request, existingGeometry);

            if (moves.Count == 0)
            {
                if (canClose)
                {
                    var best = request.ConnectionRules.OrderByDescending(r => r.BackToStartChance).First();
                    if (rng.NextBool(Math.Clamp(best.BackToStartChance / 100.0, 0, 1)))
                    {
                        chain.Edges.Add(new GridEdge(current, request.StartNode, GridEdgeKind.Line, best.Curve));
                        chain.IsClosedLoop = true;
                    }
                }
                break; // dead end: whether this counts as a success is decided after the loop
            }

            var picked = moves[rng.WeightedIndex(weights)];

            if (canClose && picked.Rule.BackToStartChance > 0
                && rng.NextBool(Math.Clamp(picked.Rule.BackToStartChance / 100.0, 0, 1)))
            {
                chain.Edges.Add(new GridEdge(current, request.StartNode, GridEdgeKind.Line, picked.Rule.Curve));
                chain.IsClosedLoop = true;
                break;
            }

            var cut = picked.Rule.Split is { } split && !picked.Edge.IsCircle
                ? TrySplit(picked.Edge, split, chain, request, existingGeometry, grid, rng)
                : null;
            if (cut is { } pieces)
            {
                chain.Edges.AddRange(pieces.Pieces);
                chain.Nodes.AddRange(pieces.Points);
            }
            else chain.Edges.Add(picked.Edge);
            var next = picked.Next ?? picked.Edge.To;
            chain.Nodes.Add(next);
            current = next;
            used += picked.Rule.ChainLengthCost;
        }

        bool success = used >= request.TargetLength || chain.Nodes.Count >= MaxNodes || chain.IsClosedLoop;
        return success ? chain : null;
    }

    /// <summary>Cuts a straight edge into pieces by adding nodes on it (connection rule <c>split</c>). Null = the edge stays whole: the chance
    /// roll failed, the edge is too short, offers no free place for a node, or (snap Nearest) a piece would be blocked.</summary>
    private static (List<GridEdge> Pieces, List<GridPoint> Points)? TrySplit(
        GridEdge edge, EdgeSplitConfig split, SubSymbolChain chain, ChainGenerationRequest request,
        IIntersectionContext existingGeometry, LatticeGrid grid, DeterministicRandom rng)
    {
        double dx = edge.To.X - edge.From.X, dy = edge.To.Y - edge.From.Y;
        if (Math.Sqrt(dx * dx + dy * dy) < split.MinEdgeLength - 1e-9) return null;

        // Its own random stream, keyed by where in the chain this is: a rule without split draws nothing extra.
        var splitRng = rng.Derive($"split-{chain.Nodes.Count}");
        if (split.Chance < 100.0 && !splitRng.NextBool(Math.Clamp(split.Chance / 100.0, 0, 1))) return null;

        // A new node never lands on a node that is already there (the chain's own, or any placed stroke's).
        var occupied = new HashSet<GridPoint>(chain.Nodes);
        foreach (var other in existingGeometry.ExistingEdges)
            foreach (var end in other.DrawnEnds()) occupied.Add(end);

        var points = EdgeSplitting.PickPoints(edge, Math.Max(2, split.Parts.Resolve(splitRng)), split.Distribution, split.Snap, split.MaxDeviation, occupied, grid, splitRng);
        if (points.Count == 0) return null;

        if (split.Mode == SplitMode.ExtraNodes)
        {
            // The edge stays as drawn; each node is recorded as a fraction of its way plus how far beside it the node lies (0 for snap Exact).
            double lenSq = dx * dx + dy * dy, len = Math.Sqrt(lenSq);
            var marks = points.Select(p => new LineNode(((p.X - edge.From.X) * dx + (p.Y - edge.From.Y) * dy) / lenSq,
                ((p.Y - edge.From.Y) * dx - (p.X - edge.From.X) * dy) / len)).Where(m => m.T > 1e-9 && m.T < 1 - 1e-9).ToList();
            if (marks.Count == 0) return null;
            return (new List<GridEdge> { edge.WithLineNodes(marks) }, new List<GridPoint>());
        }

        var pieces = new List<GridEdge>();
        var from = edge.From;
        foreach (var to in points.Append(edge.To))
        {
            pieces.Add(edge.WithPoints(from, to).WithoutLineNodes());
            from = to;
        }

        // Exact nodes lie on the edge, which was checked as one piece. Nearest ones bend it, so each piece is checked on its own.
        if (split.Snap == SplitSnap.Nearest && pieces.Any(p => !IsValidEdge(p, chain, request, existingGeometry))) return null;
        return (pieces, points);
    }

    /// <param name="Next">Where the chain carries on when that is not the edge's To (an arc with continueFrom ArcEnd).</param>
    private readonly record struct Move(GridEdge Edge, ConnectionRuleConfig Rule, GridPoint? Next = null);

    private static void CollectMoveCandidates(
        LatticeGrid grid,
        ChainGenerationRequest request,
        SubSymbolChain chain,
        GridPoint current,
        IIntersectionContext existingGeometry,
        List<Move> moves,
        List<double> weights)
    {
        // The turn is measured from the previous edge, if it was a straight one.
        (int X, int Y)? back = null;
        if (chain.Edges.Count > 0 && !chain.Edges[^1].IsCircle)
        {
            var last = chain.Edges[^1];
            back = (last.From.X - current.X, last.From.Y - current.Y);
        }

        foreach (var rule in request.ConnectionRules)
        {
            int firstCandidate = moves.Count;

            if (rule.Direction == ConnectionDirection.Circle)
                CollectCircles(grid, request, chain, current, existingGeometry, rule, moves, weights);
            else if (rule.Direction == ConnectionDirection.Arc)
                CollectArcs(grid, request, chain, current, existingGeometry, rule, moves, weights);
            else
                CollectLines(grid, request, chain, current, existingGeometry, rule, back, moves, weights);

            if (request.ConnectionWeighting == ConnectionWeighting.PerRule)
            {
                int count = moves.Count - firstCandidate;
                for (int i = firstCandidate; i < moves.Count; i++)
                    weights[i] = rule.Weight / count;
            }
        }
    }

    private static void CollectLines(
        LatticeGrid grid, ChainGenerationRequest request, SubSymbolChain chain, GridPoint current,
        IIntersectionContext existingGeometry, ConnectionRuleConfig rule, (int X, int Y)? back,
        List<Move> moves, List<double> weights)
    {
        int minLength = Math.Max(1, rule.MinLength);
        var (minAngle, maxAngle) = rule.ResolveAngleRange();
        bool limitAngle = back is not null && (minAngle > 0 || maxAngle < 180);

        foreach (var (dx, dy, length) in LineVectors(rule.Direction, minLength, rule.MaxLength))
        {
            var target = current.Translate(dx, dy);

            if (!grid.Contains(target)) continue;
            if (!request.AllowNodeRevisit && chain.Nodes.Contains(target)) continue;
            if (limitAngle && !AngleAllowed(back!.Value, dx, dy, minAngle, maxAngle)) continue;
            if (!RespectsSelfSpacing(target, current, chain, request.SelfSpacingRules)) continue;

            var edge = new GridEdge(current, target, GridEdgeKind.Line, rule.Curve);
            if (!IsValidEdge(edge, chain, request, existingGeometry)) continue;

            moves.Add(new Move(edge, rule));

            // Under PerMove (the default) each concrete direction/length combination
            // inherits the parent rule's raw weight, so a rule with a wide length range
            // or many directions ends up with more total probability mass than a narrow
            // one of equal Weight. PerRule evens that out in the caller.
            weights.Add(rule.Weight);
        }
    }

    private static void CollectCircles(
        LatticeGrid grid, ChainGenerationRequest request, SubSymbolChain chain, GridPoint current,
        IIntersectionContext existingGeometry, ConnectionRuleConfig rule,
        List<Move> moves, List<double> weights)
    {
        for (int length = Math.Max(0, rule.MinLength); length <= rule.MaxLength; length++)
        {
            // Length 0 is a dot on the current node: one move, not eight.
            var directions = length == 0 ? new[] { (0, 0) } : BothDirections;
            foreach (var (dx, dy) in directions)
            {
                var target = current.Translate(dx * length, dy * length);
                if (!grid.Contains(target)) continue;
                if (length > 0 && !request.AllowNodeRevisit && chain.Nodes.Contains(target)) continue;

                var edge = new GridEdge(current, target, GridEdgeKind.Circle, rule.Curve, null, null, rule.OutlineNodes);
                var (minX, minY, maxX, maxY) = edge.Extent();
                if (!grid.ContainsBox(minX, minY, maxX, maxY)) continue;
                if (length > 0 && !RespectsSelfSpacing(target, current, chain, request.SelfSpacingRules)) continue;
                if (!IsValidEdge(edge, chain, request, existingGeometry)) continue;

                moves.Add(new Move(edge, rule));
                weights.Add(rule.Weight);
            }
        }
    }

    /// <summary>Part-circle moves: the circle is set like a Circle's (diameter to the node 'length' away), and every arc the rule's
    /// angle preset and sweep range allow is a candidate. The rule's weight is shared by the arcs of one circle, so a rule with
    /// many possible arcs does not outweigh the others.</summary>
    private static void CollectArcs(
        LatticeGrid grid, ChainGenerationRequest request, SubSymbolChain chain, GridPoint current,
        IIntersectionContext existingGeometry, ConnectionRuleConfig rule,
        List<Move> moves, List<double> weights)
    {
        var set = rule.Arc.Angles;
        int step = AngleSets.Step(set);
        var starts = AngleSets.Angles(set);
        var sweeps = new List<int>();
        for (int sweep = rule.Arc.MinSweep; sweep <= rule.Arc.MaxSweep; sweep += step)
            if (sweep % step == 0 && sweep > 0 && sweep < 360) sweeps.Add(sweep);
        if (sweeps.Count == 0 && rule.Arc.Anchor != ArcAnchor.Diameter) return;

        // The arcs one circle offers, as (angle the arc starts at relative to the diameter, sweep). From sits at 180 on the circle
        // and To at 0 (see GridEdge), so anchoring is just a choice of those numbers.
        List<(int Relative, int Sweep)>? anchored = rule.Arc.Anchor switch
        {
            ArcAnchor.Node => sweeps.SelectMany(sw => new[] { (180, sw), (((180 - sw) % 360 + 360) % 360, sw) }).ToList(),
            ArcAnchor.Diameter => new List<(int, int)> { (180, 180), (0, 180) },
            _ => null,
        };
        double share = rule.Weight / (anchored?.Count ?? starts.Length * sweeps.Count);
        bool arcEnd = rule.Arc.Anchor == ArcAnchor.Node && rule.Arc.ContinueFrom == ArcContinueFrom.ArcEnd;

        for (int length = Math.Max(1, rule.MinLength); length <= rule.MaxLength; length++)
            foreach (var (dx, dy) in BothDirections)
            {
                var target = current.Translate(dx * length, dy * length);
                if (!grid.Contains(target)) continue;
                // With continueFrom ArcEnd the far end of the diameter is not a node of the chain: the node the arc ends at is (checked below).
                if (!arcEnd && !request.AllowNodeRevisit && chain.Nodes.Contains(target)) continue;
                if (!arcEnd && !RespectsSelfSpacing(target, current, chain, request.SelfSpacingRules)) continue;

                if (anchored is not null)
                {
                    foreach (var (relative, sweep) in anchored)
                    {
                        var edge = new GridEdge(current, target, GridEdgeKind.Circle, rule.Curve, null, new ArcSpan(relative, sweep), rule.OutlineNodes);
                        var (minX, minY, maxX, maxY) = edge.Extent();
                        if (!grid.ContainsBox(minX, minY, maxX, maxY)) continue;

                        GridPoint? next = null;
                        if (arcEnd)
                        {
                            // The arc's end that is not on the current node: current sits at relative 180, so an arc starting there ends
                            // sweep degrees on; any other anchored arc ends on current and starts at the free end.
                            double angle = relative == 180 ? edge.ArcStartDegrees + sweep : edge.ArcStartDegrees;
                            var (px, py) = edge.CirclePoint(angle);
                            var node = new GridPoint((int)Math.Round(px), (int)Math.Round(py));
                            double off = Math.Sqrt((px - node.X) * (px - node.X) + (py - node.Y) * (py - node.Y));
                            if (off > rule.Arc.EndTolerance + 1e-9 || node == current || !grid.Contains(node)) continue;
                            if (!request.AllowNodeRevisit && chain.Nodes.Contains(node)) continue;
                            if (!RespectsSelfSpacing(node, current, chain, request.SelfSpacingRules)) continue;
                            next = node;
                        }

                        if (!IsValidEdge(edge, chain, request, existingGeometry)) continue;

                        moves.Add(new Move(edge, rule, next));
                        weights.Add(share);
                    }

                    continue;
                }

                double diameterAngle = Math.Atan2(dy, dx) * 180.0 / Math.PI;
                foreach (int start in starts)
                    foreach (int sweep in sweeps)
                    {
                        int relative = (((int)Math.Round(start - diameterAngle)) % 360 + 360) % 360;
                        var edge = new GridEdge(current, target, GridEdgeKind.Circle, rule.Curve, null, new ArcSpan(relative, sweep), rule.OutlineNodes);
                        var (minX, minY, maxX, maxY) = edge.Extent();
                        if (!grid.ContainsBox(minX, minY, maxX, maxY)) continue;
                        if (!IsValidEdge(edge, chain, request, existingGeometry)) continue;

                        moves.Add(new Move(edge, rule));
                        weights.Add(share);
                    }
            }
    }

    /// <summary>The (dx, dy, length) of every straight move a direction allows, lengths min..max.</summary>
    internal static IEnumerable<(int Dx, int Dy, int Length)> LineVectors(ConnectionDirection direction, int min, int max)
    {
        if (direction is ConnectionDirection.Any or ConnectionDirection.Angle)
        {
            // Every lattice offset whose Chebyshev length is within min..max, nearest first.
            for (int length = min; length <= max; length++)
                for (int dx = -length; dx <= length; dx++)
                    for (int dy = -length; dy <= length; dy++)
                        if (Math.Max(Math.Abs(dx), Math.Abs(dy)) == length)
                            yield return (dx, dy, length);
            yield break;
        }

        var directions = DirectionsFor(direction);
        for (int length = min; length <= max; length++)
            foreach (var (dx, dy) in directions)
                yield return (dx * length, dy * length, length);
    }

    /// <summary>The turn at the current node, 180 = carry straight on, 0 = fold back.</summary>
    private static bool AngleAllowed((int X, int Y) back, int dx, int dy, double min, double max)
    {
        double lenBack = Math.Sqrt((double)back.X * back.X + (double)back.Y * back.Y);
        double lenNew = Math.Sqrt((double)dx * dx + (double)dy * dy);
        if (lenBack < 1e-9 || lenNew < 1e-9) return true;

        double cos = Math.Clamp((back.X * dx + back.Y * dy) / (lenBack * lenNew), -1.0, 1.0);
        double angle = Math.Acos(cos) * 180.0 / Math.PI;
        return angle >= min - 1e-6 && angle <= max + 1e-6;
    }

    /// <summary>
    /// A new node must satisfy EVERY configured self-spacing rule (see SelfSpacingMode for what
    /// each mode measures against). The loop-closing move needs no check - it adds no new node.
    /// </summary>
    private static bool RespectsSelfSpacing(
        GridPoint target, GridPoint current, SubSymbolChain chain, IReadOnlyList<SelfSpacingRule> rules)
    {
        foreach (var rule in rules)
        {
            if (!RespectsRule(target, current, chain, rule)) return false;
        }

        return true;
    }

    private static bool RespectsRule(
        GridPoint target, GridPoint current, SubSymbolChain chain, SelfSpacingRule rule)
    {
        // Squared distances throughout; the epsilons make "exactly at the limit" count as OK.
        double minSq = rule.MinDistance > 0 ? rule.MinDistance * rule.MinDistance - 1e-9 : 0;
        double maxSq = rule.MaxDistance > 0 ? rule.MaxDistance * rule.MaxDistance + 1e-9 : double.MaxValue;
        if (minSq <= 0 && maxSq == double.MaxValue) return true;

        var nodes = chain.Nodes;

        switch (rule.Mode)
        {
            case SelfSpacingMode.Existing:
                foreach (var node in nodes)
                {
                    double d = DistSq(node, target);
                    if (d > maxSq) return false;
                    if (node == current || node == target) continue; // connecting-from node / exact revisit
                    if (d < minSq) return false;
                }
                return true;

            case SelfSpacingMode.Adjacent:
                for (int i = Math.Max(0, nodes.Count - Math.Max(1, rule.Lookback)); i < nodes.Count; i++)
                {
                    double d = DistSq(nodes[i], target);
                    if (d > maxSq) return false;
                    if (nodes[i] == target) continue; // exact revisit
                    if (d < minSq) return false;
                }
                return true;

            case SelfSpacingMode.AllNodes:
                foreach (var node in nodes)
                {
                    double d = DistSq(node, target);
                    if (d > maxSq || d < minSq) return false;
                }
                return true;

            default:
                throw new NotSupportedException($"Unknown self-spacing mode: {rule.Mode}");
        }
    }

    private static double DistSq(GridPoint a, GridPoint b)
    {
        double dx = a.X - b.X, dy = a.Y - b.Y;
        return dx * dx + dy * dy;
    }

    /// <summary>
    /// Validates a candidate edge against this chain's own prior edges (self-avoiding, modulo
    /// AllowEdgeRevisit for exact retraces) and, against every
    /// already-placed edge elsewhere in the glyph that the shape being placed may not cross. Node-revisit is checked separately by the
    /// caller before this runs, since the closing-loop move deliberately targets a node
    /// already in the chain (its own start) and must bypass that check.
    /// </summary>
    [ThreadStatic] private static List<GridPoint>? _ownScratch;

    private static bool IsValidEdge(
        GridEdge candidateEdge,
        SubSymbolChain chain,
        ChainGenerationRequest request,
        IIntersectionContext existingGeometry)
    {
        // A rule that may bend its edges is not checked here as a straight line: the bend is only planned once the whole
        // chain exists, so the placement checks the finished curves instead (faster, and it judges the real stroke).
        bool mayCurve = !candidateEdge.IsCircle && (request.AllEdgesCurved || candidateEdge.Curve is { ChanceToBeCurved: > 0 });

        // The chain's nodes plus the candidate's end, in a list reused for every candidate (one per thread): it is only read during this call.
        var own = _ownScratch ??= new List<GridPoint>();
        own.Clear();
        own.AddRange(chain.Nodes);
        own.Add(candidateEdge.To);
        foreach (var existing in chain.Edges)
        {
            if (IsSameEdge(existing, candidateEdge))
            {
                if (!request.AllowEdgeRevisit) return false;
                continue; // exact retrace explicitly permitted; skip the geometric check below
            }

            if (!mayCurve && EdgePaths.Crosses(candidateEdge, existing)) return false;
            if (!mayCurve && request.MinEdgeDistance > 0 && EdgePaths.TooClose(candidateEdge, existing, request.MinEdgeDistance, own)) return false;
        }

        if (mayCurve) return true;

        return !existingGeometry.IsBlocked(candidateEdge, own);
    }

    private static bool IsSameEdge(GridEdge a, GridEdge b) =>
        a.Kind == b.Kind && ((a.From == b.From && a.To == b.To) || (a.From == b.To && a.To == b.From));

    private static (int Dx, int Dy)[] DirectionsFor(ConnectionDirection direction) => direction switch
    {
        ConnectionDirection.Orthogonal => OrthogonalDirections,
        ConnectionDirection.Diagonal => DiagonalDirections,
        ConnectionDirection.Both => BothDirections,
        _ => Array.Empty<(int, int)>(),
    };
}