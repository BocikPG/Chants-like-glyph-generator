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

    /// <summary>Whether the shape currently being placed allows its edges to cross others'.</summary>
    bool AllowCrossing { get; }

    /// <summary>The existing edges the shape being placed must NOT cross. Everything when it
    /// does not allow crossing; otherwise only the edges of shapes that themselves forbid being
    /// crossed - a crossing needs both shapes' permission.</summary>
    IEnumerable<GridEdge> BlockingEdges { get; }
}

/// <summary>
/// Exact-geometry crossing check, safe to do precisely (no sampling) because every edge
/// endpoint is always an integer grid coordinate — see GridEdge.Intersects.
/// </summary>
public interface IIntersectionChecker
{
    bool WouldIntersect(GridEdge candidate, IIntersectionContext context);
}
