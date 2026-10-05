using GlyphGen.Core.Grid;
using static GlyphGen.Tests.TestHelpers;

namespace GlyphGen.Tests;

/// <summary>GridEdge.Intersects decides what "crossing" means for the whole generator, so its
/// edge cases are pinned down here.</summary>
public class GridEdgeTests
{
    [Test] public void EdgesMeetingAtASharedNodeDoNotIntersect() =>
        Assert.False(Edge(0, 0, 2, 2).Intersects(Edge(2, 2, 4, 0)));

    [Test] public void AGenuineCrossingIntersects() =>
        Assert.True(Edge(0, 0, 2, 2).Intersects(Edge(0, 2, 2, 0)));

    [Test] public void ATJunctionIntersects() =>
        Assert.True(Edge(0, 0, 4, 0).Intersects(Edge(2, 0, 2, 3)));

    [Test] public void CollinearEdgesContinuingFromASharedNodeDoNotIntersect() =>
        Assert.False(Edge(0, 0, 2, 0).Intersects(Edge(2, 0, 5, 0)));

    [Test] public void CollinearOverlapIntersects() =>
        Assert.True(Edge(0, 0, 4, 0).Intersects(Edge(2, 0, 6, 0)));

    [Test] public void DoublingBackOverAnEdgeIntersects() =>
        Assert.True(Edge(0, 0, 4, 0).Intersects(Edge(4, 0, 2, 0)));

    [Test] public void ParallelEdgesDoNotIntersect() =>
        Assert.False(Edge(0, 0, 4, 0).Intersects(Edge(0, 1, 4, 1)));

    [Test] public void AnEdgePassingOverANodeDoesNotOccupyIt() =>
        // The long edge passes over (2,0); a separate edge that merely ends beside it is free.
        Assert.False(Edge(0, 0, 4, 0).Intersects(Edge(2, 1, 2, 3)));

    [Test] public void DiagonalsTouchingOnlyAtAnEndpointDoNotIntersect() =>
        Assert.False(Edge(0, 0, 3, 3).Intersects(Edge(3, 3, 3, 0)));

    [Test]
    public void IntersectsIsSymmetric()
    {
        var rng = new Random(11);
        int intersecting = 0;
        for (int i = 0; i < 3000; i++)
        {
            var a = Edge(rng.Next(0, 6), rng.Next(0, 6), rng.Next(0, 6), rng.Next(0, 6));
            var b = Edge(rng.Next(0, 6), rng.Next(0, 6), rng.Next(0, 6), rng.Next(0, 6));
            if (a.From == a.To || b.From == b.To) continue; // zero-length edges never occur

            bool ab = a.Intersects(b);
            if (ab) intersecting++;
            Assert.Equal(ab, b.Intersects(a), $"{a} vs {b}");
        }
        Assert.True(intersecting > 100, "The random sample should contain plenty of intersecting pairs.");
    }

    [Test]
    public void ReversingAnEdgeDoesNotChangeTheResult()
    {
        var a = Edge(0, 0, 4, 4);
        var b = Edge(0, 4, 4, 0);
        var reversed = new GridEdge(a.To, a.From);
        Assert.Equal(a.Intersects(b), reversed.Intersects(b));
    }
}
