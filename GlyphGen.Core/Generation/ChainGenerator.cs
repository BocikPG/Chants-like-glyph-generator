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
        // the "nodes per chain" framing used for subSymbolLength in the config schema.
        while (chain.Nodes.Count < request.TargetLength)
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

            chain.Edges.Add(picked.Edge);
            chain.Nodes.Add(picked.Edge.To);
            current = picked.Edge.To;
        }

        bool success = chain.Nodes.Count >= request.TargetLength || chain.IsClosedLoop;
        return success ? chain : null;
    }

    private readonly record struct Move(GridEdge Edge, ConnectionRuleConfig Rule);

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

                var edge = new GridEdge(current, target, GridEdgeKind.Circle, rule.Curve);
                var (minX, minY, maxX, maxY) = edge.Extent();
                if (minX < -1e-9 || minY < -1e-9 || maxX > grid.Width - 1 + 1e-9 || maxY > grid.Height - 1 + 1e-9) continue;
                if (length > 0 && !RespectsSelfSpacing(target, current, chain, request.SelfSpacingRules)) continue;
                if (!IsValidEdge(edge, chain, request, existingGeometry)) continue;

                moves.Add(new Move(edge, rule));
                weights.Add(rule.Weight);
            }
        }
    }

    /// <summary>The (dx, dy, length) of every straight move a direction allows, lengths min..max.</summary>
    private static IEnumerable<(int Dx, int Dy, int Length)> LineVectors(ConnectionDirection direction, int min, int max)
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
    private static bool IsValidEdge(
        GridEdge candidateEdge,
        SubSymbolChain chain,
        ChainGenerationRequest request,
        IIntersectionContext existingGeometry)
    {
        foreach (var existing in chain.Edges)
        {
            if (IsSameEdge(existing, candidateEdge))
            {
                if (!request.AllowEdgeRevisit) return false;
                continue; // exact retrace explicitly permitted; skip the geometric check below
            }

            if (candidateEdge.Intersects(existing)) return false;
        }

        foreach (var existing in existingGeometry.BlockingEdges)
        {
            if (candidateEdge.Intersects(existing)) return false;
        }

        return true;
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