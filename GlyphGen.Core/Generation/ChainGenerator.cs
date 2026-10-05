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
            var candidates = new List<GridPoint>();
            var weights = new List<double>();
            CollectMoveCandidates(grid, request, chain, current, existingGeometry, candidates, weights);

            bool canClose = request.AllowClosedLoop
                && chain.Nodes.Count >= 3
                && IsValidEdge(current, request.StartNode, chain, request, existingGeometry);

            int closingIndex = -1;
            if (canClose)
            {
                closingIndex = candidates.Count;
                candidates.Add(request.StartNode);
                weights.Add(request.ClosedLoopWeight);
            }

            if (candidates.Count == 0)
            {
                // Dead end: stop here. Whether this counts as a successful chain is decided
                // once, below, after the loop.
                break;
            }

            int pickedIndex = rng.WeightedIndex(weights);
            var target = candidates[pickedIndex];

            chain.Edges.Add(new GridEdge(current, target));

            if (pickedIndex == closingIndex)
            {
                chain.IsClosedLoop = true;
                break;
            }

            chain.Nodes.Add(target);
            current = target;
        }

        bool success = chain.Nodes.Count >= request.TargetLength || chain.IsClosedLoop;
        return success ? chain : null;
    }

    private static void CollectMoveCandidates(
        LatticeGrid grid,
        ChainGenerationRequest request,
        SubSymbolChain chain,
        GridPoint current,
        IIntersectionContext existingGeometry,
        List<GridPoint> candidates,
        List<double> weights)
    {
        foreach (var rule in request.ConnectionRules)
        {
            var directions = DirectionsFor(rule.Direction);
            int minLength = Math.Max(1, rule.MinLength);

            for (int length = minLength; length <= rule.MaxLength; length++)
            {
                foreach (var (dx, dy) in directions)
                {
                    var target = current.Translate(dx * length, dy * length);

                    if (!grid.Contains(target)) continue;
                    if (!request.AllowNodeRevisit && chain.Nodes.Contains(target)) continue;
                    if (!RespectsSelfSpacing(target, current, chain, request.SelfSpacingRules)) continue;
                    if (!IsValidEdge(current, target, chain, request, existingGeometry)) continue;

                    candidates.Add(target);

                    // NOTE: each concrete direction/length combination inherits the parent
                    // rule's raw weight rather than splitting it across combinations — a rule
                    // with a wide length range or Both-direction set therefore ends up with
                    // more total probability mass than a narrow one of equal Weight. This is
                    // an intentional simplicity trade-off; revisit if a rule's Weight needs to
                    // stay independent of how many concrete moves it expands into.
                    weights.Add(rule.Weight);
                }
            }
        }
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
    /// AllowEdgeRevisit for exact retraces) and, unless crossing is allowed, against every
    /// already-placed edge elsewhere in the glyph. Node-revisit is checked separately by the
    /// caller before this runs, since the closing-loop move deliberately targets a node
    /// already in the chain (its own start) and must bypass that check.
    /// </summary>
    private static bool IsValidEdge(
        GridPoint from,
        GridPoint to,
        SubSymbolChain chain,
        ChainGenerationRequest request,
        IIntersectionContext existingGeometry)
    {
        var candidateEdge = new GridEdge(from, to);

        foreach (var existing in chain.Edges)
        {
            if (IsSameEdge(existing, candidateEdge))
            {
                if (!request.AllowEdgeRevisit) return false;
                continue; // exact retrace explicitly permitted; skip the geometric check below
            }

            if (candidateEdge.Intersects(existing)) return false;
        }

        if (!existingGeometry.AllowCrossing)
        {
            foreach (var existing in existingGeometry.ExistingEdges)
            {
                if (candidateEdge.Intersects(existing)) return false;
            }
        }

        return true;
    }

    private static bool IsSameEdge(GridEdge a, GridEdge b) =>
        (a.From == b.From && a.To == b.To) || (a.From == b.To && a.To == b.From);

    private static (int Dx, int Dy)[] DirectionsFor(ConnectionDirection direction) => direction switch
    {
        ConnectionDirection.Orthogonal => OrthogonalDirections,
        ConnectionDirection.Diagonal => DiagonalDirections,
        ConnectionDirection.Both => BothDirections,
        _ => Array.Empty<(int, int)>(),
    };
}
