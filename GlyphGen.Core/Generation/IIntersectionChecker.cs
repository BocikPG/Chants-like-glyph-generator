using GlyphGen.Core.Grid;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Everything already committed to the glyph-in-progress, for collision-checking new
/// candidate edges against. Pass-through nodes on long connections are deliberately NOT
/// tracked as occupied anywhere in this context — only actual drawn edges are checked for
/// geometric crossing, per design (a long straight jump doesn't "claim" the lattice points
/// it happens to pass over).
/// </summary>
public interface IIntersectionContext
{
    IEnumerable<GridEdge> ExistingEdges { get; }

    /// <summary>True when the edge would cross something it may not: each committed edge it cuts through must be accepted by
    /// the crossing settings of both shapes (the one being placed, and the one that owns that edge). ownNodes are the nodes of
    /// the chain the edge belongs to (they can make a crossing "at a node").</summary>
    bool IsBlocked(GridEdge edge, IReadOnlyList<GridPoint> ownNodes);

    /// <summary>The existing edges that were drawn by the given shapes (ids). Contexts that do not know which shape
    /// drew what return everything.</summary>
    IEnumerable<GridEdge> EdgesOfShapes(IReadOnlyCollection<string> shapeIds) => ExistingEdges;
}

/// <summary>
/// Exact-geometry crossing check, safe to do precisely (no sampling) because every edge
/// endpoint is always an integer grid coordinate — see GridEdge.Intersects.
/// </summary>
public interface IIntersectionChecker
{
    bool WouldIntersect(GridEdge candidate, IIntersectionContext context);
}
