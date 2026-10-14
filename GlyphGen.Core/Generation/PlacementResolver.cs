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
        if (config.Mode == PlacementMode.Fixed && config.FixedOrigin is null)
        {
            throw new InvalidOperationException(
                "PlacementConfig.Mode is Fixed but FixedOrigin was not set.");
        }

        // Nodes already in the glyph (commit order, no duplicates) - only gathered when asked for.
        List<GridPoint>? anchorNodes = null;
        // Works with crossing off too: GridEdge.Intersects treats edges that merely meet at a
        // shared node as not crossing, so a shape growing out of an existing node is legal.
        if (config.Mode == PlacementMode.RandomValid && config.StartOnExistingNode)
            anchorNodes = CollectNodes(config.StartOnShapes is { } only ? existingGeometry.EdgesOfShapes(only) : existingGeometry.ExistingEdges);

        bool positionVaries = config.Mode == PlacementMode.RandomValid;
        bool rotationVaries = config.AllowRotationOnPlace
            && rotationOptions is not null
            && rotationOptions.Allowed.Count > 1;

        bool anythingVaries = positionVaries || rotationVaries || shapeVariesPerAttempt;
        int effectiveMaxAttempts = anythingVaries ? Math.Max(1, config.MaxPlacementAttempts) : 1;

        for (int attempt = 0; attempt < effectiveMaxAttempts; attempt++)
        {
            // Each attempt gets its own deterministic child RNG derived from a stable label, so
            // retries never share draws with each other regardless of how many draws a prior
            // attempt happened to consume.
            var attemptRng = rng.Derive($"placement-attempt-{attempt}");

            var origin = ResolveOrigin(config, grid, attemptRng, anchorNodes);
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
        }
        return nodes;
    }

    private static GridPoint ResolveOrigin(
        PlacementConfig config, LatticeGrid grid, DeterministicRandom rng, IReadOnlyList<GridPoint>? anchorNodes) =>
        config.Mode switch
        {
            PlacementMode.Fixed => config.FixedOrigin!.Value,
            PlacementMode.CenterAnchor => new GridPoint(grid.Width / 2, grid.Height / 2),
            PlacementMode.RandomValid => anchorNodes is { Count: > 0 }
                ? anchorNodes[rng.NextInt(0, anchorNodes.Count)]
                : new GridPoint(
                    rng.NextInt(0, grid.Width),
                    rng.NextInt(0, grid.Height)),
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

        foreach (var edge in shape.Edges)
        {
            if (edge.IsCircle)
            {
                var (minX, minY, maxX, maxY) = edge.Extent();
                if (minX < -1e-9 || minY < -1e-9 || maxX > grid.Width - 1 + 1e-9 || maxY > grid.Height - 1 + 1e-9) return false;
            }

            foreach (var existing in existingGeometry.BlockingEdges)
            {
                if (edge.Intersects(existing)) return false;
            }
        }

        return true;
    }
}
