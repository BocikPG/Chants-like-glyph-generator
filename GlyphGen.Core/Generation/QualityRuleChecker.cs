using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;

namespace GlyphGen.Core.Generation;

/// <summary>What one quality rule found in one glyph.</summary>
public sealed record QualityFinding(QualityRule Rule, int Violations, string Example, string? SubjectComponent = null, string? OtherComponent = null)
{
    public double Penalty => Violations * Rule.Weight;
}

/// <summary>
/// Evaluates <see cref="QualityRule"/>s on a finished glyph (plain or composite). Every stroke the rule's subject selects is judged
/// against the strokes its 'against' selector picks from the rest of the glyph. See the config guide, "quality.rules".
/// </summary>
public static class QualityRuleChecker
{
    private sealed class Stroke
    {
        public GridEdge Edge;
        public (double X, double Y)[] Path = Array.Empty<(double, double)>();
        public double MinX, MinY, MaxX, MaxY;
        public string? ShapeId;
        public SubSymbolChain Chain = null!;
        public int Component = -1;          // index into Glyph.Components, -1 in a plain glyph
        public string? ComponentName;
        public HashSet<(int, int)>? Raster; // visual similarity, cached per width
        public double RasterWidth;
        public double CenterX => (MinX + MaxX) / 2;
        public double CenterY => (MinY + MaxY) / 2;
    }

    /// <summary>Ids of the rules that found at least one subject stroke / one 'against' stroke in the glyphs evaluated so far, so a
    /// rule whose selectors match nothing at all (a typo in kind or shapes) can be reported at the end of the run.</summary>
    public sealed class Coverage
    {
        public HashSet<string> Subjects { get; } = new();
        public HashSet<string> Against { get; } = new();
    }

    public static List<QualityFinding> Evaluate(Glyph glyph, IReadOnlyList<QualityRule> rules, bool isComposite, Coverage? coverage = null)
    {
        var findings = new List<QualityFinding>();
        if (rules.Count == 0) return findings;

        var strokes = Collect(glyph);
        var counts = CountInstances(strokes);

        foreach (var rule in rules)
        {
            if (rule.AppliesTo == QualityAppliesTo.Plain && isComposite) continue;
            if (rule.AppliesTo == QualityAppliesTo.Composite && !isComposite) continue;
            if (rule.OnlyForGlyphs is { Count: > 0 } only && !only.Contains(glyph.Name)) continue;
            if (rule.ExcludeGlyphs is { Count: > 0 } excluded && excluded.Contains(glyph.Name)) continue;
            if (rule.Among == QualityAmong.OtherComponents && !isComposite) continue;

            if (coverage is not null)
            {
                if (strokes.Any(s => Matches(rule.Subject, s))) coverage.Subjects.Add(rule.Id);
                if (strokes.Any(s => Matches(rule.Against, s))) coverage.Against.Add(rule.Id);
            }

            bool between = rule.Among == QualityAmong.OtherComponents || (rule.Among == QualityAmong.Auto && isComposite);
            var finding = EvaluateRule(rule, strokes.Where(x => Matches(rule.Subject, x)).ToList(), strokes.Where(x => Matches(rule.Against, x)).ToList(), counts, between);
            if (finding is not null) findings.Add(finding);
        }

        return findings;
    }

    private static bool Names(List<string>? glyphs, string name) => glyphs is not null && (glyphs.Contains(name) || glyphs.Contains("*"));

    private static bool SameSelector(StrokeSelector a, StrokeSelector b) =>
        a.Kind == b.Kind && (a.Shapes ?? new()).OrderBy(x => x).SequenceEqual((b.Shapes ?? new()).OrderBy(x => x));

    /// <summary>
    /// quality.pairRules: judges the strokes of <paramref name="glyph"/> against the strokes of every other glyph in
    /// <paramref name="others"/> (and the other way round, where the rule's subject names those glyphs). Each finding names the
    /// glyph holding the judged stroke (SubjectComponent) and the glyph it clashed with (OtherComponent).
    /// </summary>
    public static List<QualityFinding> EvaluatePairs(Glyph glyph, IReadOnlyDictionary<string, Glyph> others, IReadOnlyList<QualityRule> rules, Coverage? coverage = null)
    {
        var findings = new List<QualityFinding>();
        if (rules.Count == 0) return findings;

        var cache = new Dictionary<string, List<Stroke>>();
        List<Stroke> StrokesOf(Glyph g, int component)
        {
            var list = Collect(g);
            foreach (var st in list) { st.Component = component; st.ComponentName = g.Name; }
            return list;
        }

        int next = 1;
        var ids = new Dictionary<string, int>();
        List<Stroke> Of(Glyph g)
        {
            if (cache.TryGetValue(g.Name, out var hit)) return hit;
            if (!ids.TryGetValue(g.Name, out var id)) ids[g.Name] = id = g.Name == glyph.Name ? 0 : next++;
            return cache[g.Name] = StrokesOf(g, id);
        }

        foreach (var rule in rules)
        {
            var subjectNames = rule.Subject.Glyphs; var againstNames = rule.Against.Glyphs;
            var total = new List<QualityFinding>();
            int violations = 0; string example = ""; string? sc = null, oc = null;

            foreach (var (name, other) in others)
            {
                if (name == glyph.Name) continue;
                var roles = new List<(Glyph S, Glyph A)>();
                if (Names(subjectNames, glyph.Name) && Names(againstNames, name)) roles.Add((glyph, other));
                // Both ways round, unless the rule is symmetric (then the first order already covers the pair).
                bool symmetric = roles.Count == 1 && Names(subjectNames, name) && Names(againstNames, glyph.Name) && SameSelector(rule.Subject, rule.Against);
                if (!symmetric && Names(subjectNames, name) && Names(againstNames, glyph.Name)) roles.Add((other, glyph));

                foreach (var (sg, ag) in roles)
                {
                    var sStrokes = Of(sg); var aStrokes = Of(ag);
                    var subjects = sStrokes.Where(x => Matches(rule.Subject, x)).ToList();
                    var against = aStrokes.Where(x => Matches(rule.Against, x)).ToList();
                    if (coverage is not null)
                    {
                        if (subjects.Count > 0) coverage.Subjects.Add(rule.Id);
                        if (against.Count > 0) coverage.Against.Add(rule.Id);
                    }

                    var finding = EvaluateRule(rule, subjects, against, CountInstances(sStrokes.Concat(aStrokes).ToList()), betweenComponents: true);
                    if (finding is null) continue;
                    violations += finding.Violations;
                    if (example.Length == 0) { example = finding.Example; sc = finding.SubjectComponent; oc = finding.OtherComponent; }
                }
            }

            if (violations > 0) findings.Add(new QualityFinding(rule, violations, example, sc, oc));
        }

        return findings;
    }

    private static QualityFinding? EvaluateRule(QualityRule rule, List<Stroke> subjects, List<Stroke> againstAll, Dictionary<(int, string), int> counts, bool betweenComponents)
    {
        if (subjects.Count == 0 || againstAll.Count == 0) return null;

        int violations = 0;
        string example = "";
        string? subjectComponent = null, otherComponent = null;

        foreach (var subject in subjects)
        {
            int found = 0;
            double nearest = double.MaxValue;
            bool brokenByDistance = false;
            Stroke? first = null;

            foreach (var other in againstAll)
            {
                if (ReferenceEquals(other.Chain, subject.Chain)) continue;
                if (betweenComponents && other.Component == subject.Component) continue;

                double factor = 1.0;
                if (rule.WhenCountsDiffer is { Shape.Length: > 0 } differ && betweenComponents
                    && counts.GetValueOrDefault((subject.Component, differ.Shape)) != counts.GetValueOrDefault((other.Component, differ.Shape)))
                {
                    if (differ.Action == CountDiffersAction.Skip) continue;
                    factor = differ.Factor;
                }

                switch (rule.Measure)
                {
                    case QualityMeasure.Distance:
                    {
                        double d = EdgePaths.PathDistance(subject.Path, other.Path);
                        nearest = Math.Min(nearest, d);
                        if (d < rule.MinDistance * factor - 1e-9) { brokenByDistance = true; first ??= other; }
                        break;
                    }
                    default:
                    {
                        if (!InWindow(subject, other, rule.Window, factor)) break;
                        if (rule.Measure == QualityMeasure.Similar && Similarity(subject, other, rule) < rule.SimilarityThreshold) break;
                        found++;
                        first ??= other;
                        break;
                    }
                }
            }

            bool broken = rule.Measure == QualityMeasure.Distance ? brokenByDistance : found > rule.Max;
            if (!broken) continue;
            violations++;
            if (example.Length == 0)
            {
                example = Describe(rule, subject, first, found, nearest);
                subjectComponent = subject.ComponentName;
                otherComponent = first?.ComponentName;
            }
        }

        return violations == 0 ? null : new QualityFinding(rule, violations, example, subjectComponent, otherComponent);
    }

    private static string Describe(QualityRule rule, Stroke subject, Stroke? other, int found, double nearest)
    {
        string who = $"{Name(subject)} at ({subject.CenterX:0.#},{subject.CenterY:0.#})";
        string with = other is null ? "" : $" ({Name(other)} at ({other.CenterX:0.#},{other.CenterY:0.#}))";
        return rule.Measure switch
        {
            QualityMeasure.Distance => $"{who} is {nearest:0.##} from the nearest other stroke{with}, minimum {rule.MinDistance:0.##}",
            QualityMeasure.Similar => $"{who} has {found} similar stroke(s) within {rule.Window.Radius:0.##}{with}, at most {rule.Max}",
            _ => $"{who} has {found} stroke(s) within {rule.Window.Radius:0.##}{with}, at most {rule.Max}",
        };

        static string Name(Stroke s) =>
            (s.Edge.IsDot ? "dot" : s.Edge.IsArc ? "arc" : s.Edge.IsCircle ? "circle" : s.Edge.IsCurved ? "curve" : "line")
            + (s.ShapeId is null ? "" : $" of '{s.ShapeId}'") + (s.ComponentName is null ? "" : $" in {s.ComponentName}");
    }

    private static List<Stroke> Collect(Glyph glyph)
    {
        var result = new List<Stroke>();
        void Add(SubSymbolChain chain, int component, string? componentName)
        {
            string? shapeId = chain.ShapeTag is { } tag && tag.LastIndexOf('#') > 0 ? tag[..tag.LastIndexOf('#')] : null;
            foreach (var edge in chain.Edges)
            {
                var path = EdgePaths.Polyline(edge);
                var stroke = new Stroke { Edge = edge, Path = path, ShapeId = shapeId, Chain = chain, Component = component, ComponentName = componentName };
                stroke.MinX = path.Min(p => p.X); stroke.MaxX = path.Max(p => p.X);
                stroke.MinY = path.Min(p => p.Y); stroke.MaxY = path.Max(p => p.Y);
                result.Add(stroke);
            }
        }

        foreach (var chain in glyph.Chains) Add(chain, -1, null);
        for (int i = 0; i < glyph.Components.Count; i++)
            foreach (var chain in glyph.Components[i].TransformedChains) Add(chain, i, glyph.Components[i].SourceGlyphName);
        return result;
    }

    /// <summary>How many instances of each shape every component (or the plain glyph, -1) has: distinct tagged chains.</summary>
    private static Dictionary<(int, string), int> CountInstances(List<Stroke> strokes)
    {
        var chains = new HashSet<(int, string, SubSymbolChain)>();
        foreach (var s in strokes)
            if (s.ShapeId is not null) chains.Add((s.Component, s.ShapeId, s.Chain));
        return chains.GroupBy(c => (c.Item1, c.Item2)).ToDictionary(g => g.Key, g => g.Count());
    }

    private static bool Matches(StrokeSelector selector, Stroke s)
    {
        if (selector.Shapes is { Count: > 0 } shapes && (s.ShapeId is null || !shapes.Contains(s.ShapeId))) return false;
        var e = s.Edge;
        return selector.Kind switch
        {
            StrokeKind.Dot => e.IsDot,
            StrokeKind.Arc => e.IsArc,
            StrokeKind.Circle => e.IsCircle && !e.IsArc,
            StrokeKind.Ring => e.IsCircle && !e.IsDot && !e.IsArc,
            StrokeKind.Line => !e.IsCircle && !e.IsCurved,
            StrokeKind.Curve => !e.IsCircle && e.IsCurved,
            _ => true,
        };
    }

    private static bool InWindow(Stroke subject, Stroke other, QualityWindow window, double factor)
    {
        double r = window.Radius * factor;
        if (window.Shape == WindowShape.Square)
            return subject.MinX - r <= other.MaxX && other.MinX <= subject.MaxX + r
                && subject.MinY - r <= other.MaxY && other.MinY <= subject.MaxY + r;
        return EdgePaths.PathDistance(subject.Path, other.Path) <= r + 1e-9;
    }

    // ---- similarity -----------------------------------------------------------------------------------------------

    private static double Similarity(Stroke a, Stroke b, QualityRule rule) =>
        rule.Similarity == SimilarityMode.Structural ? (SameStructure(a, b) ? 1.0 : 0.0) : VisualSimilarity(a, b, rule.StrokeWidth);

    private static bool SameStructure(Stroke a, Stroke b)
    {
        var x = a.Edge; var y = b.Edge;
        if (x.IsDot != y.IsDot || x.IsArc != y.IsArc || x.IsCircle != y.IsCircle || x.IsCurved != y.IsCurved) return false;
        if (x.IsDot) return true;
        if (x.IsArc)
            return Math.Abs(x.Arc!.Value.Sweep - y.Arc!.Value.Sweep) <= 45 && Math.Abs(Diameter(x) - Diameter(y)) <= 1.0;
        if (x.IsCircle) return Math.Abs(Diameter(x) - Diameter(y)) <= 1.0;
        return Math.Abs(Length(a.Path) - Length(b.Path)) <= 1.0;
    }

    private static double Diameter(GridEdge e) =>
        Math.Sqrt((double)(e.To.X - e.From.X) * (e.To.X - e.From.X) + (double)(e.To.Y - e.From.Y) * (e.To.Y - e.From.Y));

    private static double Length((double X, double Y)[] path)
    {
        double sum = 0;
        for (int i = 0; i + 1 < path.Length; i++)
            sum += Math.Sqrt((path[i + 1].X - path[i].X) * (path[i + 1].X - path[i].X) + (path[i + 1].Y - path[i].Y) * (path[i + 1].Y - path[i].Y));
        return sum;
    }

    private const int PixelsPerCell = 4;

    private static double VisualSimilarity(Stroke a, Stroke b, double width)
    {
        var ra = RasterOf(a, width);
        var rb = RasterOf(b, width);
        if (ra.Count == 0 && rb.Count == 0) return 1.0;
        // Strokes are compared as shapes, wherever they are: their centres are laid on each other.
        int dx = (int)Math.Round((b.CenterX - a.CenterX) * PixelsPerCell), dy = (int)Math.Round((b.CenterY - a.CenterY) * PixelsPerCell);
        int common = 0;
        foreach (var (x, y) in ra)
            if (rb.Contains((x + dx, y + dy))) common++;
        int union = ra.Count + rb.Count - common;
        return union == 0 ? 0.0 : (double)common / union;
    }

    private static HashSet<(int, int)> RasterOf(Stroke s, double width)
    {
        if (s.Raster is not null && s.RasterWidth == width) return s.Raster;
        var set = new HashSet<(int, int)>();
        double radius = width / 2 * PixelsPerCell;
        int reach = (int)Math.Ceiling(radius);
        void Stamp(double x, double y)
        {
            double px = x * PixelsPerCell, py = y * PixelsPerCell;
            for (int ix = (int)Math.Floor(px - reach); ix <= (int)Math.Ceiling(px + reach); ix++)
                for (int iy = (int)Math.Floor(py - reach); iy <= (int)Math.Ceiling(py + reach); iy++)
                    if ((ix - px) * (ix - px) + (iy - py) * (iy - py) <= radius * radius) set.Add((ix, iy));
        }

        var path = s.Path;
        if (path.Length == 1) Stamp(path[0].X, path[0].Y);
        for (int i = 0; i + 1 < path.Length; i++)
        {
            double len = Math.Sqrt((path[i + 1].X - path[i].X) * (path[i + 1].X - path[i].X) + (path[i + 1].Y - path[i].Y) * (path[i + 1].Y - path[i].Y));
            int steps = Math.Max(1, (int)Math.Ceiling(len * PixelsPerCell));
            for (int k = 0; k <= steps; k++)
                Stamp(path[i].X + (path[i + 1].X - path[i].X) * k / steps, path[i].Y + (path[i + 1].Y - path[i].Y) * k / steps);
        }

        s.Raster = set;
        s.RasterWidth = width;
        return set;
    }
}
