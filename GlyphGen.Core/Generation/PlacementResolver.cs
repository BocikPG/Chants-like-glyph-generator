using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Default IPlacementResolver implementation. Each attempt resolves an origin (per Mode) and a
/// rotation (per AllowRotationOnPlace + rotationOptions), asks shapeFactory to produce the
/// shape there, then validates every resulting node/edge against grid bounds and
/// existingGeometry itself. This re-check is redundant but harmless for a Generative shape
/// whose factory (IChainGenerator) already validated collisions incrementally while growing -
/// it's what actually matters for a Literal shape's factory, which hasn't validated anything
/// on its own, so keeping one uniform check here avoids two divergent code paths.
///
/// PlacementConfig.StartOnExistingNode (RandomValid mode only): instead of a random grid point,
/// the origin is drawn from the nodes already in the glyph, so a new shape grows out of an
/// existing one. The very first shape, with nothing to attach to, is placed normally.
/// </summary>
public sealed class PlacementResolver : IPlacementResolver
{
    public PlacementResult TryResolve(
        PlacementConfig config,
        LatticeGrid grid,
        DeterministicRandom rng,
        IIntersectionContext existingGeometry,
        bool shapeVariesPerAttempt,
        RotationConfig? rotationOptions,
        Func<GridPoint, int, DeterministicRandom, SubSymbolChain?> shapeFactory)
    {
        if (config.Mode == PlacementMode.Fixed && (config.FixedOrigin is null || config.FixedOrigin.Points.Count == 0))
        {
            throw new InvalidOperationException(
                "PlacementConfig.Mode is Fixed but FixedOrigin was not set.");
        }

        // Nodes already in the glyph (commit order, no duplicates) - only gathered when asked for.
        List<GridPoint>? anchorNodes = null;
        // Works with crossing off too: GridEdge.Intersects treats edges that merely meet at a
        // shared node as not crossing, so a shape growing out of an existing node is legal.
        if (config.Mode == PlacementMode.RandomValid && config.StartOnExistingNode)
            anchorNodes = CollectNodes(config.StartOnShapes is { } only ? existingGeometry.EdgesOfShapes(only) : existingGeometry.ExistingEdges)
                .Where(grid.Contains).ToList();

        bool positionVaries = config.Mode == PlacementMode.RandomValid;
        bool rotationVaries = config.AllowRotationOnPlace
            && rotationOptions is not null
            && rotationOptions.Allowed.Count > 1;

        bool anythingVaries = positionVaries || rotationVaries || shapeVariesPerAttempt;
        // Several fixed origins: tried in a shuffled order (from a stream of its own, so a single origin changes nothing).
        List<GridPoint>? fixedOrder = null;
        if (config.Mode == PlacementMode.Fixed && config.FixedOrigin!.Points.Count > 1)
        {
            fixedOrder = new List<GridPoint>(config.FixedOrigin.Points);
            var shuffle = rng.Derive("fixed-origins");
            for (int i = fixedOrder.Count - 1; i > 0; i--)
            {
                int j = shuffle.NextInt(0, i + 1);
                (fixedOrder[i], fixedOrder[j]) = (fixedOrder[j], fixedOrder[i]);
            }
        }

        anythingVaries |= fixedOrder is not null;
        int effectiveMaxAttempts = anythingVaries ? Math.Max(1, Math.Max(config.MaxPlacementAttempts, fixedOrder?.Count ?? 0)) : 1;

        for (int attempt = 0; attempt < effectiveMaxAttempts; attempt++)
        {
            // Each attempt gets its own deterministic child RNG derived from a stable label, so
            // retries never share draws with each other regardless of how many draws a prior
            // attempt happened to consume.
            var attemptRng = rng.Derive($"placement-attempt-{attempt}");

            // startOnExistingNode with a chance below 100: roll (from its own stream) whether this attempt grows from a node.
            var anchors = anchorNodes;
            if (anchors is not null && config.StartOnExistingNodeChance < 100.0
                && attemptRng.Derive("start-on-node").NextDouble() * 100.0 >= config.StartOnExistingNodeChance)
                anchors = null;

            var origin = fixedOrder is not null ? fixedOrder[attempt % fixedOrder.Count] : ResolveOrigin(config, grid, attemptRng, anchors);
            int rotation = ResolveRotation(config, rotationOptions, attemptRng);

            var shape = shapeFactory(origin, rotation, attemptRng);
            if (shape is null) continue; // shapeFactory's own failure (e.g. chain died early)

            if (!FitsWithoutCollision(shape, grid, existingGeometry)) continue;

            return new PlacementResult
            {
                Success = true,
                Origin = origin,
                RotationDegrees = rotation,
                AttemptsUsed = attempt + 1,
                PlacedShape = shape,
            };
        }

        return new PlacementResult
        {
            Success = false,
            AttemptsUsed = effectiveMaxAttempts,
        };
    }

    private static List<GridPoint> CollectNodes(IEnumerable<GridEdge> edges)
    {
        var seen = new HashSet<GridPoint>();
        var nodes = new List<GridPoint>();
        foreach (var edge in edges)
        {
            if (seen.Add(edge.From)) nodes.Add(edge.From);
            if (seen.Add(edge.To)) nodes.Add(edge.To);
            foreach (var extra in edge.ExtraNodes()) if (seen.Add(extra)) nodes.Add(extra);
        }
        return nodes;
    }

    private static GridPoint ResolveOrigin(
        PlacementConfig config, LatticeGrid grid, DeterministicRandom rng, IReadOnlyList<GridPoint>? anchorNodes) =>
        config.Mode switch
        {
            PlacementMode.Fixed => config.FixedOrigin!.Points[0],
            PlacementMode.CenterAnchor => new GridPoint((grid.MinX + grid.MaxX + 1) / 2, (grid.MinY + grid.MaxY + 1) / 2),
            PlacementMode.RandomValid => anchorNodes is { Count: > 0 }
                ? anchorNodes[rng.NextInt(0, anchorNodes.Count)]
                : grid.NodeFilter is not null && grid.UsableNodes() is { Count: > 0 } usable
                ? usable[rng.NextInt(0, usable.Count)]
                : new GridPoint(
                    grid.MinX + rng.NextInt(0, grid.MaxX - grid.MinX + 1),
                    grid.MinY + rng.NextInt(0, grid.MaxY - grid.MinY + 1)),
            _ => throw new NotSupportedException($"Unknown placement mode: {config.Mode}"),
        };

    private static int ResolveRotation(
        PlacementConfig config,
        RotationConfig? rotationOptions,
        DeterministicRandom rng)
    {
        if (!config.AllowRotationOnPlace || rotationOptions is null || rotationOptions.Allowed.Count == 0)
            return 0;

        // Angles carry a single shared Weight rather than per-angle weights (see
        // RotationConfig) - picked uniformly among the allowed set.
        int index = rng.NextInt(0, rotationOptions.Allowed.Count);
        return rotationOptions.Allowed[index];
    }

    private static bool FitsWithoutCollision(
        SubSymbolChain shape,
        LatticeGrid grid,
        IIntersectionContext existingGeometry)
    {
        foreach (var node in shape.Nodes)
        {
            if (!grid.Contains(node)) return false;
        }

        var ownNodes = shape.AllNodes();
        foreach (var edge in shape.Edges)
        {
            if (edge.IsCircle)
            {
                var (minX, minY, maxX, maxY) = edge.Extent();
                if (!grid.ContainsBox(minX, minY, maxX, maxY)) return false;
            }

            if (existingGeometry.IsBlocked(edge, ownNodes)) return false;
        }

        // A chain may never cross itself (curves included).
        return !EdgePaths.ChainCrossesItself(shape.Edges)
            && !EdgePaths.ChainTooClose(shape.Edges, ownNodes, existingGeometry.CurrentClearance);
    }
}
