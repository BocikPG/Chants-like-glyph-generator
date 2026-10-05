using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Rendering.Curves;
using GlyphGen.Rendering.Geometry;
using static GlyphGen.Tests.TestHelpers;

namespace GlyphGen.Tests;

/// <summary>GlyphCurvePlanner: a component must carry its source glyph's curves into a composite,
/// mapped through the component's own transform.</summary>
public class CurveTests
{
    private const ulong Seed = 99;

    private static CurveConfig AllCurves() => new()
    {
        Mode = CurveMode.Derived,
        StraightLineBias = new PercentRange { Min = 0, Max = 0 },
        Tension = { Options = { new TensionOption { Min = 0.3, Max = 0.4, Weight = 1 } } },
    };

    private static Glyph Source() => MakeGlyph("src", Chain((0, 0), (2, 0), (2, 2), (4, 2)));

    private static int Curved(PlannedGlyph plan) => plan.Layers.SelectMany(l => l.Chains).SelectMany(c => c.Edges).Count(e => !e.IsLine);
    private static int Total(PlannedGlyph plan) => plan.Layers.SelectMany(l => l.Chains).SelectMany(c => c.Edges).Count();

    private static void AssertNear(Vec2 expected, Vec2 actual, string what) =>
        Assert.True(Math.Abs(expected.X - actual.X) < 1e-9 && Math.Abs(expected.Y - actual.Y) < 1e-9,
            $"{what}: expected ({expected.X}, {expected.Y}) but was ({actual.X}, {actual.Y})");

    private static Vec2 Map(Vec2 v, int num, int den, int rotation, int dx, int dy)
    {
        // The same maths as CombinationResolver, on continuous points: scale, rotate about (0,0), translate.
        double x = v.X * num / den, y = v.Y * num / den;
        (double rx, double ry) = rotation switch
        {
            0 => (x, y), 90 => (-y, x), 180 => (-x, -y), 270 => (y, -x), _ => throw new ArgumentOutOfRangeException(nameof(rotation)),
        };
        return new Vec2(rx + dx, ry + dy);
    }

    private static void AssertComponentReusesSourcePlan(int num, int den, int rotation, int dx, int dy)
    {
        var source = Source();
        var composite = MakeComposite("comp", Place(source, num, den, rotation, dx, dy));
        var planner = new GlyphCurvePlanner(new[] { source, composite }, AllCurves(), 1234);

        var original = new GlyphCurvePlanner(new[] { source }, AllCurves(), 1234).Plan(source).Layers.Single().Chains.Single();
        var mapped = planner.Plan(composite).Layers.Single().Chains.Single();

        Assert.Equal(original.Edges.Count, mapped.Edges.Count);
        for (int i = 0; i < original.Edges.Count; i++)
        {
            var o = original.Edges[i];
            var m = mapped.Edges[i];
            Assert.Equal(o.IsLine, m.IsLine, $"edge {i} line/curve");
            AssertNear(Map(o.P0, num, den, rotation, dx, dy), m.P0, $"edge {i} P0");
            AssertNear(Map(o.C1, num, den, rotation, dx, dy), m.C1, $"edge {i} C1");
            AssertNear(Map(o.C2, num, den, rotation, dx, dy), m.C2, $"edge {i} C2");
            AssertNear(Map(o.P3, num, den, rotation, dx, dy), m.P3, $"edge {i} P3");
        }
    }

    [Test] public void AComponentKeepsItsCurvesWhenOnlyMoved() => AssertComponentReusesSourcePlan(1, 1, 0, 6, 3);
    [Test] public void AComponentKeepsItsCurvesWhenRotated() => AssertComponentReusesSourcePlan(1, 1, 90, 8, 1);
    [Test] public void AComponentKeepsItsCurvesWhenRotatedHalfwayRound() => AssertComponentReusesSourcePlan(1, 1, 180, 10, 10);
    [Test] public void AComponentKeepsItsCurvesWhenScaledUp() => AssertComponentReusesSourcePlan(2, 1, 0, 1, 1);
    [Test] public void AComponentKeepsItsCurvesWhenScaledAndRotated() => AssertComponentReusesSourcePlan(2, 1, 270, 9, 0);

    [Test]
    public void ThePlanOfAPlainGlyphIsTheSameWhetherOrNotACompositeUsesIt()
    {
        var source = Source();
        var alone = new GlyphCurvePlanner(new[] { source }, AllCurves(), 5).Plan(source);
        var composite = MakeComposite("comp", Place(source, 1, 1, 0, 3, 3));
        var together = new GlyphCurvePlanner(new[] { source, composite }, AllCurves(), 5).Plan(source);

        Assert.Equal(Total(alone), Total(together));
        for (int i = 0; i < Total(alone); i++)
        {
            var a = alone.Layers[0].Chains[0].Edges[i];
            var b = together.Layers[0].Chains[0].Edges[i];
            AssertNear(a.C1, b.C1, $"edge {i} C1");
        }
    }

    // ---- context modes -----------------------------------------------------------------------

    private static (PlannedGlyph Plain, PlannedGlyph Combined) Plans(CurveContextMode mode)
    {
        var curve = AllCurves();
        curve.ContextMode = mode;
        var source = Source();
        var composite = MakeComposite("comp", Place(source, 1, 1, 0, 6, 6));
        var planner = new GlyphCurvePlanner(new[] { source, composite }, curve, Seed);
        return (planner.Plan(source), planner.Plan(composite));
    }

    [Test]
    public void AllCurvedCurvesBothContexts()
    {
        var (plain, combined) = Plans(CurveContextMode.AllCurved);
        Assert.True(Curved(plain) > 0);
        Assert.Equal(Curved(plain), Curved(combined));
    }

    [Test]
    public void AllStraightFlattensBothContexts()
    {
        var (plain, combined) = Plans(CurveContextMode.AllStraight);
        Assert.Equal(0, Curved(plain));
        Assert.Equal(0, Curved(combined));
    }

    [Test]
    public void CurvedMiniStraightCombinedCurvesOnlyThePlainGlyph()
    {
        var (plain, combined) = Plans(CurveContextMode.CurvedMiniStraightCombined);
        Assert.True(Curved(plain) > 0);
        Assert.Equal(0, Curved(combined));
    }

    [Test]
    public void StraightMiniCurvedCombinedCurvesOnlyTheComposite()
    {
        var (plain, combined) = Plans(CurveContextMode.StraightMiniCurvedCombined);
        Assert.Equal(0, Curved(plain));
        Assert.True(Curved(combined) > 0, "the composite keeps the source glyph's natural curves");
    }

    // ---- forcing and re-checking -------------------------------------------------------------

    [Test]
    public void ForceAllEdgesCurvedOverridesTheStraightnessSettings()
    {
        var curve = AllCurves();
        curve.StraightLineBias = new PercentRange { Min = 100, Max = 100 };
        curve.ProximityGuard = new CurveProximityGuardConfig { Enabled = true, MinEdgeLength = 99 };
        var source = Source();

        Assert.Equal(0, Curved(new GlyphCurvePlanner(new[] { source }, curve, Seed).Plan(source)), "sanity: normally everything is straight here");

        curve.ForceAllEdgesCurved = true;
        var forced = new GlyphCurvePlanner(new[] { source }, curve, Seed).Plan(source);
        Assert.Equal(Total(forced), Curved(forced));
    }

    private static (PlannedGlyph Off, PlannedGlyph On) GuardPlans()
    {
        PlannedGlyph Plan(bool recheck)
        {
            var curve = AllCurves();
            curve.ProximityGuard = new CurveProximityGuardConfig { Enabled = true, MinEdgeLength = 0.5, MinDistanceToOtherNodes = 1.5 };
            curve.RecheckProximityInCombined = recheck;

            var a = MakeGlyph("a", Chain((0, 0), (4, 0)));
            var b = MakeGlyph("b", Chain((4, 1), (7, 1))); // a node one unit from a's end
            var composite = MakeComposite("ab", Place(a, 1, 1, 0, 0, 0), Place(b, 1, 1, 0, 0, 0));
            return new GlyphCurvePlanner(new[] { a, b, composite }, curve, Seed).Plan(composite);
        }

        return (Plan(false), Plan(true));
    }

    [Test]
    public void WithoutTheRecheckAComponentKeepsCurvesItsNeighbourNowCrowds()
    {
        var (off, _) = GuardPlans();
        Assert.True(off.Layers[0].Chains[0].Edges[0].IsLine == false, "judged alone, a's edge was free to curve");
    }

    [Test]
    public void TheRecheckFlattensAnEdgeTheNeighbourCrowds()
    {
        var (_, on) = GuardPlans();
        Assert.True(on.Layers[0].Chains[0].Edges[0].IsLine, "with b's node one unit away the guard must flatten it");
    }

    [Test]
    public void TheRecheckDoesNothingWhenNothingIsCrowded()
    {
        var curve = AllCurves();
        curve.ProximityGuard = new CurveProximityGuardConfig { Enabled = true, MinEdgeLength = 0.5, MinDistanceToOtherNodes = 1.5 };
        curve.RecheckProximityInCombined = true;

        var a = MakeGlyph("a", Chain((0, 0), (4, 0)));
        var b = MakeGlyph("b", Chain((0, 9), (4, 9)));
        var composite = MakeComposite("ab", Place(a, 1, 1, 0, 0, 0), Place(b, 1, 1, 0, 0, 0));
        var plan = new GlyphCurvePlanner(new[] { a, b, composite }, curve, Seed).Plan(composite);

        Assert.Equal(Total(plan), Curved(plan));
    }
}
