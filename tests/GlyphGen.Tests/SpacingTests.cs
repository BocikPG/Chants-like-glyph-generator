using GlyphGen.Core.Config;
using GlyphGen.Core.Generation;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using static GlyphGen.Tests.TestHelpers;

namespace GlyphGen.Tests;

/// <summary>Spacing between shapes (SubSymbolSpacingChecker).</summary>
public class SpacingCheckerTests
{
    private static IReadOnlyList<GridPoint> Pts(params (int X, int Y)[] p) => p.Select(t => new GridPoint(t.X, t.Y)).ToList();

    private static bool Ok(IReadOnlyList<GridPoint> candidate, SubSymbolSpacingConfig config, params IReadOnlyList<GridPoint>[] existing) =>
        SubSymbolSpacingChecker.SatisfiesSpacing(candidate, existing, config);

    [Test] public void DisabledSpacingAlwaysPasses() =>
        Assert.True(Ok(Pts((0, 0)), new SubSymbolSpacingConfig { Enabled = false, MinNodeDistance = 99 }, Pts((0, 0))));

    [Test] public void TheFirstShapeHasNothingToBeNearSoTheMaximumPasses() =>
        Assert.True(Ok(Pts((0, 0)), new SubSymbolSpacingConfig { Enabled = true, MaxNodeDistance = 1 }));

    [Test]
    public void MinCenterDistanceComparesCentroids()
    {
        var other = Pts((0, 0), (2, 0));                 // centroid (1,0)
        var candidate = Pts((4, 0), (6, 0));             // centroid (5,0) -> distance 4
        Assert.True(Ok(candidate, new SubSymbolSpacingConfig { Enabled = true, MinCenterDistance = 4 }, other));
        Assert.False(Ok(candidate, new SubSymbolSpacingConfig { Enabled = true, MinCenterDistance = 4.5 }, other));
    }

    [Test]
    public void MinNodeDistanceComparesTheClosestPair()
    {
        var other = Pts((0, 0), (1, 0));
        var candidate = Pts((3, 0), (9, 9));             // closest pair (1,0)-(3,0) = 2
        Assert.True(Ok(candidate, new SubSymbolSpacingConfig { Enabled = true, MinNodeDistance = 2 }, other));
        Assert.False(Ok(candidate, new SubSymbolSpacingConfig { Enabled = true, MinNodeDistance = 2.1 }, other));
    }

    [Test]
    public void ASharedNodeIsDistanceZero() =>
        Assert.False(Ok(Pts((1, 1)), new SubSymbolSpacingConfig { Enabled = true, MinNodeDistance = 0.1 }, Pts((1, 1), (5, 5))));

    [Test]
    public void NearestShapeNeedsOnlyOneNeighbourInRange()
    {
        var near = Pts((0, 0));
        var far = Pts((20, 0));
        var candidate = Pts((2, 0));                     // 2 from `near`, 18 from `far`
        var config = new SubSymbolSpacingConfig { Enabled = true, MaxNodeDistance = 3, MaxDistanceMode = ProximityMode.NearestShape };
        Assert.True(Ok(candidate, config, near, far));
    }

    [Test]
    public void AllShapesNeedsEveryNeighbourInRange()
    {
        var near = Pts((0, 0));
        var far = Pts((20, 0));
        var candidate = Pts((2, 0));
        var config = new SubSymbolSpacingConfig { Enabled = true, MaxNodeDistance = 3, MaxDistanceMode = ProximityMode.AllShapes };
        Assert.False(Ok(candidate, config, near, far));
        Assert.True(Ok(candidate, config, near));
    }

    [Test]
    public void ExactlyAtTheMaximumIsAllowed() =>
        Assert.True(Ok(Pts((3, 0)), new SubSymbolSpacingConfig { Enabled = true, MaxNodeDistance = 3 }, Pts((0, 0))));

    [Test]
    public void BeyondTheMaximumFailsEvenWithAMinimumSatisfied() =>
        Assert.False(Ok(Pts((6, 0)), new SubSymbolSpacingConfig { Enabled = true, MinNodeDistance = 1, MaxNodeDistance = 3 }, Pts((0, 0))));

    [Test]
    public void AComponentIsMeasuredAsOneShapeAcrossAllItsChains()
    {
        var existing = new List<IReadOnlyList<GridPoint>> { Pts((5, 0)) };
        var candidate = new[] { Chain((0, 0), (1, 0)), Chain((10, 0), (11, 0)) };

        // Pooled nodes: closest to (5,0) is (1,0), at distance 4.
        Assert.True(SubSymbolSpacingChecker.SatisfiesSpacingAsOneShape(candidate, existing, new SubSymbolSpacingConfig { Enabled = true, MinNodeDistance = 4 }));
        Assert.False(SubSymbolSpacingChecker.SatisfiesSpacingAsOneShape(candidate, existing, new SubSymbolSpacingConfig { Enabled = true, MinNodeDistance = 4.5 }));
    }

    [Test]
    public void GeometryContextCommitsAComponentAsASingleNodeGroup()
    {
        var context = new GeometryContext(allowCrossing: true);
        context.CommitAsOneShape(new[] { Chain((0, 0), (1, 0)), Chain((5, 5), (6, 5)) });
        context.Commit(Chain((9, 9), (10, 9)));

        Assert.Equal(2, context.NodeGroups.Count, "one group for the component, one for the plain chain");
        Assert.Equal(4, context.NodeGroups[0].Count);
        Assert.Equal(3, context.ExistingEdges.Count(), "every edge is still tracked for crossing checks");
    }
}

/// <summary>Spacing within one chain (the selfSpacing rules, applied as the chain grows).</summary>
public class SelfSpacingTests
{
    private static readonly GridPoint Start = new(12, 12);

    private static List<SubSymbolChain> Chains(
        IEnumerable<SelfSpacingRule> rules, int length = 8, int seeds = 250, bool allowRevisit = false)
    {
        var grid = new LatticeGrid(25, 25);
        var generator = new ChainGenerator();
        var result = new List<SubSymbolChain>();
        var ruleList = rules.ToList();

        for (ulong seed = 1; seed <= (ulong)seeds; seed++)
        {
            var request = new ChainGenerationRequest
            {
                StartNode = Start,
                TargetLength = length,
                AllowNodeRevisit = allowRevisit,
                SelfSpacingRules = ruleList,
                ConnectionRules =
                {
                    new ConnectionRuleConfig { Direction = ConnectionDirection.Orthogonal, MinLength = 1, MaxLength = 3, Weight = 1 },
                    new ConnectionRuleConfig { Direction = ConnectionDirection.Diagonal, MinLength = 1, MaxLength = 2, Weight = 1 },
                },
            };

            var chain = generator.TryGenerateChain(grid, request, new GlyphGen.Core.Rng.DeterministicRandom(seed), new GeometryContext(true));
            if (chain is not null) result.Add(chain);
        }

        return result;
    }

    private const double Eps = 1e-9;

    [Test]
    public void WithoutRulesChainsAreGeneratedAsBefore() =>
        Assert.True(Chains(Array.Empty<SelfSpacingRule>()).Count > 150);

    [Test]
    public void ExistingMinimumKeepsNonAdjacentNodesApart()
    {
        var chains = Chains(new[] { new SelfSpacingRule { Mode = SelfSpacingMode.Existing, MinDistance = 2 } });
        Assert.True(chains.Count > 20, "some chains should survive the rule");

        foreach (var chain in chains)
            for (int i = 0; i < chain.Nodes.Count; i++)
                for (int j = i + 2; j < chain.Nodes.Count; j++)
                    Assert.True(Distance(chain.Nodes[i], chain.Nodes[j]) >= 2 - Eps,
                        $"nodes {i} and {j} are {Distance(chain.Nodes[i], chain.Nodes[j]):0.00} apart");
    }

    [Test]
    public void AdjacentMinimumWithLookbackOneConstrainsEveryStep()
    {
        var chains = Chains(new[] { new SelfSpacingRule { Mode = SelfSpacingMode.Adjacent, MinDistance = 2, Lookback = 1 } });
        Assert.True(chains.Count > 20);

        foreach (var chain in chains)
            for (int i = 1; i < chain.Nodes.Count; i++)
                Assert.True(Distance(chain.Nodes[i - 1], chain.Nodes[i]) >= 2 - Eps, "a step was shorter than the minimum");
    }

    [Test]
    public void AdjacentLookbackTwoAlsoChecksTheNodeBeforeThat()
    {
        var chains = Chains(new[] { new SelfSpacingRule { Mode = SelfSpacingMode.Adjacent, MinDistance = 1.5, Lookback = 2 } });
        Assert.True(chains.Count > 20);

        foreach (var chain in chains)
            for (int i = 2; i < chain.Nodes.Count; i++)
                Assert.True(Distance(chain.Nodes[i - 2], chain.Nodes[i]) >= 1.5 - Eps, $"node {i} is too close to node {i - 2}");
    }

    [Test]
    public void AllNodesMaximumBoundsTheWholeChain()
    {
        var chains = Chains(new[] { new SelfSpacingRule { Mode = SelfSpacingMode.AllNodes, MaxDistance = 4 } });
        Assert.True(chains.Count > 20);

        foreach (var chain in chains)
            foreach (var a in chain.Nodes)
                foreach (var b in chain.Nodes)
                    Assert.True(Distance(a, b) <= 4 + Eps, $"{a} and {b} are {Distance(a, b):0.00} apart");
    }

    [Test]
    public void AllNodesMinimumAppliesEvenToTheNodeItConnectsFrom()
    {
        var chains = Chains(new[] { new SelfSpacingRule { Mode = SelfSpacingMode.AllNodes, MinDistance = 2 } });
        Assert.True(chains.Count > 10);

        foreach (var chain in chains)
            for (int i = 0; i < chain.Nodes.Count; i++)
                for (int j = i + 1; j < chain.Nodes.Count; j++)
                    Assert.True(Distance(chain.Nodes[i], chain.Nodes[j]) >= 2 - Eps, $"nodes {i} and {j} are too close");
    }

    [Test]
    public void SeveralRulesAreAllEnforcedTogether()
    {
        var chains = Chains(new[]
        {
            new SelfSpacingRule { Mode = SelfSpacingMode.Existing, MinDistance = 1.5 },
            new SelfSpacingRule { Mode = SelfSpacingMode.AllNodes, MaxDistance = 5 },
        });
        Assert.True(chains.Count > 20);

        foreach (var chain in chains)
            for (int i = 0; i < chain.Nodes.Count; i++)
                for (int j = i + 1; j < chain.Nodes.Count; j++)
                {
                    double d = Distance(chain.Nodes[i], chain.Nodes[j]);
                    Assert.True(d <= 5 + Eps, "AllNodes maximum broken");
                    if (j > i + 1) Assert.True(d >= 1.5 - Eps, "Existing minimum broken");
                }
    }

    [Test]
    public void ARevisitedNodeIsExemptFromTheExistingMinimum()
    {
        // With revisits on and a minimum of 2, chains may still step back onto an earlier node.
        var chains = Chains(new[] { new SelfSpacingRule { Mode = SelfSpacingMode.Existing, MinDistance = 2 } }, length: 8, allowRevisit: true);
        Assert.True(chains.Any(c => c.Nodes.Distinct().Count() < c.Nodes.Count), "expected at least one chain that revisits a node");
    }
}
