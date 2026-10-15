using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Derived glyphs: a copy of another glyph's strokes, edited by an ordered list of steps (see
/// DerivationConfig). The work happens on a small editable model of the strokes; every random choice comes from
/// a stream derived from the glyph's seed and the step's index, so a derivation is reproducible.
/// </summary>
public sealed partial class GlyphGenerator
{
    private sealed class WorkEdge
    {
        public WorkEdge(GridEdge edge, GridEdge? origin) { Edge = edge; Origin = origin; }
        public GridEdge Edge { get; }

        /// <summary>The same edge in the source glyph, while it is still the source's edge (curves are kept
        /// for these); null for edges that are new or were moved.</summary>
        public GridEdge? Origin { get; }
    }

    private sealed class WorkChain
    {
        public List<WorkEdge> Edges { get; set; } = new();
        public bool Closed { get; set; }
        public string? MotifId { get; set; }
        public string? ShapeTag { get; set; }
    }

    private sealed class DerivState
    {
        public List<WorkChain> Chains { get; } = new();
        public List<GridPoint> Lone { get; } = new();

        // x' = a[0]*x + a[1]*y + a[4]; y' = a[2]*x + a[3]*y + a[5]
        public double[] Affine { get; set; } = { 1, 0, 0, 1, 0, 0 };

        public int EdgeCount => Chains.Sum(c => c.Edges.Count);

        public DerivState Copy()
        {
            var copy = new DerivState { Affine = (double[])Affine.Clone() };
            copy.Chains.AddRange(Chains);
            copy.Lone.AddRange(Lone);
            return copy;
        }
    }

    private sealed record BridgeContext(IReadOnlyList<ConnectionRuleConfig> Rules, int MaxMoves, LatticeGrid Grid, DeterministicRandom Rng);

    private Glyph GenerateDerived(
        LanguageDefinition language,
        GlyphDefinition glyphDef,
        DerivationConfig derivation,
        LatticeGrid grid,
        ulong glyphSeed,
        Dictionary<string, Glyph> generated)
    {
        if (!generated.TryGetValue(derivation.From, out var source))
        {
            throw new GlyphGenerationException(
                $"[{language.Name}] '{glyphDef.Name}' is derived from '{derivation.From}', which has not been generated " +
                "(check the name, and that derivations do not depend on each other in a circle).");
        }

        var state = Flatten(source);
        var rng = new DeterministicRandom(glyphSeed);
        int emptiedAt = -1;
        int mostChanged = 0; // 1 = only moves, 2 = removals, 3 = Add

        for (int i = 0; i < derivation.Steps.Count; i++)
        {
            var step = derivation.Steps[i];
            var stepRng = rng.Derive($"derive-step-{i}");

            if (step.OnlyIf is { } gate && !GateHolds(state, gate)) continue;
            if (step.Chance < 100.0 && !stepRng.NextBool(step.Chance / 100.0)) continue;

            mostChanged = Math.Max(mostChanged, step.Op switch
            {
                DerivationOp.Scale or DerivationOp.Mirror or DerivationOp.Rotate or DerivationOp.Nudge => 1,
                DerivationOp.Add => 3,
                _ => 2,
            });

            state = step.Op switch
            {
                DerivationOp.DeleteNodes => StepDeleteNodes(state, step, stepRng, grid),
                DerivationOp.DeleteEdges => StepDeleteEdges(state, step, stepRng, keepMode: false),
                DerivationOp.Keep => StepDeleteEdges(state, step, stepRng, keepMode: true),
                DerivationOp.Add => StepAdd(language, glyphDef, state, step, stepRng, grid),
                DerivationOp.Scale => StepScale(state, step, stepRng, grid),
                DerivationOp.Mirror => StepMirror(state, step, stepRng),
                DerivationOp.Rotate => StepRotate(state, step, stepRng, grid),
                DerivationOp.Nudge => StepNudge(state, step, stepRng, grid),
                _ => throw new NotSupportedException($"Unknown derivation op: {step.Op}"),
            };

            if (emptiedAt < 0 && state.EdgeCount == 0) emptiedAt = i;
        }

        if (state.EdgeCount == 0)
        {
            string cause = emptiedAt < 0
                ? $"the source '{derivation.From}' has no edges to start with, so there is nothing to derive from."
                : $"step {emptiedAt + 1} ({derivation.Steps[emptiedAt].Op}) removed the last edge" + (derivation.Steps[emptiedAt].Op switch
                {
                    DerivationOp.DeleteNodes or DerivationOp.DeleteEdges => ": delete less (a smaller amount), or raise keepAtLeast on that step.",
                    DerivationOp.Keep => ": keep more (a larger amount).",
                    _ => ".",
                }) + (derivation.Steps.Skip(emptiedAt + 1).Any(x => x.Op == DerivationOp.Add) ? "" : " A later Add step could put something back.");
            throw new GlyphGenerationException(
                $"[{language.Name}] The derivation of '{glyphDef.Name}' from '{derivation.From}' left no edges: {cause}");
        }

        var glyph = new Glyph
        {
            Name = glyphDef.Name,
            LanguageName = language.Name,
            GridWidth = grid.Width,
            GridHeight = grid.Height,
        };

        var derived = new DerivedFrom
        {
            Source = derivation.From,
            FollowsSourceRotation = derivation.FinalOutputRotation != DerivedRotation.Own && mostChanged <= (int)derivation.FinalOutputRotation,
        };

        int chainIndex = 0;
        foreach (var chain in state.Chains)
        {
            var made = ToChain(chain);
            // Edges that kept their source curve keep it; new ones (bridges) and moved ones get a plan of their own.
            BendPlanner.Plan(made, language.Curve, rng.Derive($"bend-{chainIndex++}"), curved: BendPlanner.AnyCurved(made, language.Curve));
            glyph.Chains.Add(made);
        }

        foreach (var node in state.Lone)
        {
            var lone = new SubSymbolChain();
            lone.Nodes.Add(node);
            glyph.Chains.Add(lone);
        }

        glyph.Derived = derived;
        return glyph;
    }

    private static bool GateHolds(DerivState s, DerivationCondition gate)
    {
        int edges = s.EdgeCount;
        int nodes = Degrees(s).Count;
        return (gate.MinEdges is not { } minE || edges >= minE)
            && (gate.MaxEdges is not { } maxE || edges <= maxE)
            && (gate.MinNodes is not { } minN || nodes >= minN)
            && (gate.MaxNodes is not { } maxN || nodes <= maxN);
    }

    // ---- model <-> glyph ---------------------------------------------------------------------------

    private static DerivState Flatten(Glyph source)
    {
        var state = new DerivState();
        foreach (var chain in GlyphGeometryUtils.GetAllChains(source))
        {
            if (chain.Edges.Count == 0)
            {
                state.Lone.AddRange(chain.Nodes);
                continue;
            }

            var work = new WorkChain { Closed = chain.IsClosedLoop, MotifId = chain.MotifId, ShapeTag = chain.ShapeTag };
            foreach (var e in chain.Edges) work.Edges.Add(new WorkEdge(e, e));
            state.Chains.Add(work);
        }
        return state;
    }

    private static SubSymbolChain ToChain(WorkChain work)
    {
        var chain = new SubSymbolChain { IsClosedLoop = work.Closed, MotifId = work.MotifId, ShapeTag = work.ShapeTag };
        if (work.Edges.Count == 0) return chain;

        chain.Nodes.Add(work.Edges[0].Edge.From);
        foreach (var e in work.Edges)
        {
            chain.Edges.Add(e.Edge);
            chain.Nodes.Add(e.Edge.To);
        }
        if (work.Closed) chain.Nodes.RemoveAt(chain.Nodes.Count - 1); // the last edge returns to the start
        return chain;
    }

    private static WorkChain FromChain(SubSymbolChain chain)
    {
        var work = new WorkChain { Closed = chain.IsClosedLoop, MotifId = chain.MotifId, ShapeTag = chain.ShapeTag };
        foreach (var e in chain.Edges) work.Edges.Add(new WorkEdge(e, null));
        return work;
    }

    // ---- graph helpers -----------------------------------------------------------------------------

    private static Dictionary<GridPoint, int> Degrees(DerivState s)
    {
        var degree = new Dictionary<GridPoint, int>();
        void Add(GridPoint p) => degree[p] = degree.TryGetValue(p, out int d) ? d + 1 : 1;

        foreach (var chain in s.Chains)
            foreach (var e in chain.Edges)
            {
                Add(e.Edge.From);
                if (!e.Edge.IsDot) Add(e.Edge.To);
            }

        foreach (var p in s.Lone) if (!degree.ContainsKey(p)) degree[p] = 0;
        return degree;
    }

    private static List<GridPoint> SortedPoints(Dictionary<GridPoint, int> degree) =>
        degree.Keys.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();

    private static int Components(DerivState s)
    {
        var parent = new Dictionary<GridPoint, GridPoint>();
        GridPoint Find(GridPoint p)
        {
            while (!parent[p].Equals(p)) { parent[p] = parent[parent[p]]; p = parent[p]; }
            return p;
        }

        foreach (var chain in s.Chains)
            foreach (var e in chain.Edges)
            {
                if (!parent.ContainsKey(e.Edge.From)) parent[e.Edge.From] = e.Edge.From;
                if (!parent.ContainsKey(e.Edge.To)) parent[e.Edge.To] = e.Edge.To;
                parent[Find(e.Edge.From)] = Find(e.Edge.To);
            }

        return parent.Keys.Select(Find).Distinct().Count();
    }

    private static bool Touches(GridEdge e, GridPoint p) => e.From.Equals(p) || e.To.Equals(p);

    private static void Shuffle<T>(IList<T> list, DeterministicRandom rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.NextInt(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static int ResolveAmount(DerivationAmount? amount, int total, DeterministicRandom rng)
    {
        if (amount?.Percent is { } percent) return (int)Math.Round(total * percent.Resolve(rng) / 100.0);
        if (amount?.Count is { } count) return count.Resolve(rng);
        return 0;
    }

    /// <summary>Splits a chain at every removed edge; what is left in between becomes chains of their own. A
    /// closed chain that loses an edge is opened at the gap first.</summary>
    private static List<WorkChain> SplitChain(WorkChain chain, Func<WorkEdge, bool> removed)
    {
        var edges = chain.Edges;
        int firstRemoved = edges.FindIndex(e => removed(e));
        if (firstRemoved < 0) return new List<WorkChain> { chain };

        if (chain.Closed)
            edges = edges.Skip(firstRemoved + 1).Concat(edges.Take(firstRemoved + 1)).ToList();

        var result = new List<WorkChain>();
        var run = new List<WorkEdge>();
        void Flush()
        {
            if (run.Count > 0) result.Add(new WorkChain { Edges = run, MotifId = chain.MotifId, ShapeTag = chain.ShapeTag });
            run = new List<WorkEdge>();
        }

        foreach (var e in edges)
        {
            if (removed(e)) Flush();
            else run.Add(e);
        }
        Flush();
        return result;
    }

    /// <summary>The state after chains changed: nodes that lost their last edge are dropped, or kept as lone
    /// nodes.</summary>
    private static DerivState Finish(DerivState before, List<WorkChain> chains, IEnumerable<GridPoint> gone, bool removeOrphans)
    {
        var next = new DerivState { Affine = before.Affine };
        next.Chains.AddRange(chains);

        var goneSet = new HashSet<GridPoint>(gone);
        var degree = Degrees(next);
        foreach (var p in before.Lone)
            if (!goneSet.Contains(p) && !degree.ContainsKey(p)) next.Lone.Add(p);

        if (!removeOrphans)
            foreach (var p in Degrees(before).Keys.OrderBy(p => p.X).ThenBy(p => p.Y))
                if (!goneSet.Contains(p) && !degree.ContainsKey(p) && !next.Lone.Contains(p)) next.Lone.Add(p);

        return next;
    }

    private static bool Acceptable(DerivState next, int keepAtLeast, bool keepConnected, int baselineComponents) =>
        next.EdgeCount >= keepAtLeast && (!keepConnected || Components(next) <= baselineComponents);

    // ---- DeleteNodes / DeleteEdges / Keep ------------------------------------------------------------

    private DerivState StepDeleteNodes(DerivState s, DerivationStep step, DeterministicRandom rng, LatticeGrid grid)
    {
        var degree = Degrees(s);
        var points = SortedPoints(degree);
        int wanted = ResolveAmount(step.Amount, points.Count, rng);

        var candidates = points.Where(p => TargetMatches(step.Target, degree[p])).ToList();
        Shuffle(candidates, rng);

        bool bridge = step.Mode == DeleteNodeMode.Bridge;
        var context = bridge
            ? new BridgeContext(step.ConnectionRules ?? new List<ConnectionRuleConfig>(), Math.Max(1, step.BridgeMaxMoves ?? 3), grid, rng.Derive("bridge"))
            : null;

        int keepAtLeast = step.KeepAtLeast ?? 1;
        bool keepConnected = step.KeepConnected ?? false;
        bool removeOrphans = step.RemoveOrphanNodes ?? true;
        int baseline = Components(s);

        int removed = 0;
        foreach (var p in candidates)
        {
            if (removed >= wanted) break;
            if (!Degrees(s).ContainsKey(p)) continue; // already gone with an earlier pick

            var next = WithoutPoint(s, p, context, removeOrphans);
            if (!Acceptable(next, keepAtLeast, keepConnected, baseline)) continue;

            s = next;
            removed++;
        }

        return s;
    }

    private static bool TargetMatches(DerivationTarget? target, int degree) => (target ?? DerivationTarget.Any) switch
    {
        DerivationTarget.Ends => degree == 1,
        DerivationTarget.Inner => degree == 2,
        DerivationTarget.Junction => degree >= 3,
        _ => true,
    };

    private DerivState WithoutPoint(DerivState s, GridPoint p, BridgeContext? bridge, bool removeOrphans)
    {
        var degree = Degrees(s);
        var chains = s.Chains.ToList();

        if (bridge is not null && degree.TryGetValue(p, out int d) && d == 2)
        {
            var allEdges = s.Chains.SelectMany(c => c.Edges).Select(e => e.Edge).ToList();
            var points = new HashSet<GridPoint>(degree.Keys);

            for (int c = 0; c < chains.Count; c++)
            {
                var list = chains[c].Edges.ToList();
                int i = FindPassThrough(list, p, chains[c].Closed);
                if (i < 0) continue;

                bool closed = chains[c].Closed;
                if (closed) list = list.Skip(i).Concat(list.Take(i)).ToList(); // so the pair is list[0], list[1]
                int at = closed ? 0 : i;

                var a = list[at].Edge.From;
                var b = list[at + 1].Edge.To;
                if (a.Equals(b)) break;

                var others = allEdges.ToList();
                others.RemoveAll(e => IsSame(e, list[at].Edge) || IsSame(e, list[at + 1].Edge));

                var path = FindBridge(a, b, p, others, points, bridge);
                if (path is not null)
                {
                    list.RemoveRange(at, 2);
                    list.InsertRange(at, path.Select(e => new WorkEdge(e, null)));
                    chains[c] = new WorkChain { Edges = list, Closed = closed, MotifId = chains[c].MotifId, ShapeTag = chains[c].ShapeTag };
                }
                break;
            }
        }

        var result = new List<WorkChain>();
        foreach (var chain in chains)
            result.AddRange(SplitChain(chain, e => Touches(e.Edge, p)));

        return Finish(s, result, new[] { p }, removeOrphans);
    }

    private static bool IsSame(GridEdge a, GridEdge b) =>
        a.Kind == b.Kind && a.From.Equals(b.From) && a.To.Equals(b.To);

    /// <summary>The index i where edges i and i+1 both run through p (p is the end of one and the start of the
    /// next), both straight. -1 if p is not a plain pass-through of this chain.</summary>
    private static int FindPassThrough(List<WorkEdge> edges, GridPoint p, bool closed)
    {
        int n = edges.Count;
        for (int i = 0; i < n; i++)
        {
            int j = i + 1;
            if (j >= n) { if (!closed) break; j = 0; }
            var e1 = edges[i].Edge; var e2 = edges[j].Edge;
            if (e1.IsCircle || e2.IsCircle) continue;
            if (e1.To.Equals(p) && e2.From.Equals(p) && !e1.From.Equals(p) && !e2.To.Equals(p)) return i;
        }
        return -1;
    }

    /// <summary>Grows a path of straight moves (from the rules) from a to b that crosses nothing and avoids p and
    /// the glyph's other nodes. Shortest first; null if none exists within the move limit.</summary>
    private static List<GridEdge>? FindBridge(
        GridPoint a, GridPoint b, GridPoint p, List<GridEdge> others, HashSet<GridPoint> existing, BridgeContext ctx)
    {
        var moves = new List<(int Dx, int Dy, ConnectionRuleConfig Rule)>();
        foreach (var rule in ctx.Rules)
        {
            if (rule.Direction == ConnectionDirection.Circle) continue;
            foreach (var (dx, dy, _) in ChainGenerator.LineVectors(rule.Direction, Math.Max(1, rule.MinLength), rule.MaxLength))
                moves.Add((dx, dy, rule));
        }
        if (moves.Count == 0) return null;

        int budget = 6000;

        bool Free(GridEdge edge, List<GridEdge> path) =>
            !others.Any(o => EdgePaths.Crosses(edge, o)) && !path.Any(o => EdgePaths.Crosses(edge, o));

        List<GridEdge>? Search(GridPoint at, int remaining, List<GridEdge> path, HashSet<GridPoint> used)
        {
            if (budget-- <= 0) return null;

            if (remaining == 1)
            {
                var order = moves.ToList();
                Shuffle(order, ctx.Rng);
                foreach (var (dx, dy, rule) in order)
                {
                    if (!at.Translate(dx, dy).Equals(b)) continue;
                    var last = new GridEdge(at, b, GridEdgeKind.Line, rule.Curve);
                    if (!Free(last, path)) continue;
                    var done = path.ToList();
                    done.Add(last);
                    return done;
                }
                return null;
            }

            var shuffled = moves.ToList();
            Shuffle(shuffled, ctx.Rng);
            foreach (var (dx, dy, rule) in shuffled)
            {
                var t = at.Translate(dx, dy);
                if (!ctx.Grid.Contains(t) || t.Equals(p) || t.Equals(b) || used.Contains(t) || existing.Contains(t)) continue;

                var edge = new GridEdge(at, t, GridEdgeKind.Line, rule.Curve);
                if (!Free(edge, path)) continue;

                path.Add(edge);
                used.Add(t);
                var found = Search(t, remaining - 1, path, used);
                used.Remove(t);
                path.RemoveAt(path.Count - 1);
                if (found is not null) return found;
            }
            return null;
        }

        for (int length = 1; length <= ctx.MaxMoves; length++)
        {
            var found = Search(a, length, new List<GridEdge>(), new HashSet<GridPoint> { a });
            if (found is not null) return found;
        }
        return null;
    }

    private DerivState StepDeleteEdges(DerivState s, DerivationStep step, DeterministicRandom rng, bool keepMode)
    {
        var all = s.Chains.SelectMany(c => c.Edges).ToList();
        int total = all.Count;
        int amount = ResolveAmount(step.Amount, total, rng);
        int wanted = keepMode ? total - amount : amount;

        var candidates = all.ToList();
        Shuffle(candidates, rng);

        int keepAtLeast = step.KeepAtLeast ?? 1;
        bool keepConnected = step.KeepConnected ?? false;
        bool removeOrphans = step.RemoveOrphanNodes ?? true;
        int baseline = Components(s);

        int removed = 0;
        foreach (var edge in candidates)
        {
            if (removed >= wanted) break;

            var chains = new List<WorkChain>();
            foreach (var chain in s.Chains)
                chains.AddRange(SplitChain(chain, e => ReferenceEquals(e, edge)));

            var next = Finish(s, chains, Array.Empty<GridPoint>(), removeOrphans);
            if (!Acceptable(next, keepAtLeast, keepConnected, baseline)) continue;

            s = next;
            removed++;
        }

        return s;
    }

    // ---- Add -------------------------------------------------------------------------------------------

    private DerivState StepAdd(
        LanguageDefinition language, GlyphDefinition glyphDef, DerivState s, DerivationStep step,
        DeterministicRandom rng, LatticeGrid grid)
    {
        var geometry = new GeometryContext { GridWidth = grid.Width, GridHeight = grid.Height };
        foreach (var chain in s.Chains) geometry.Commit(ToChain(chain), new CrossingConfig { Mode = CrossingMode.Anywhere });

        var next = s.Copy();
        bool optional = step.Optional ?? false;
        var addedByShape = new Dictionary<string, List<SubSymbolChain>>(StringComparer.Ordinal);

        foreach (var id in step.Shapes ?? new List<string>())
        {
            var shape = language.Shapes.First(x => x.Id == id);

            if (!shape.IsDrawn(rng))
            {
                addedByShape[id] = new List<SubSymbolChain>();
                continue;
            }

            if (shape.CopyOf is { } copyOf)
            {
                // A copy needs its origin earlier in this same list (the validator checks); the copies' curves are drawn fresh.
                var origins = addedByShape.GetValueOrDefault(copyOf.Shape) ?? new List<SubSymbolChain>();

                var made = MakeCopies(language, shape, origins, grid, rng, geometry, glyphDef.Name, optional,
                    chainsOfShape: other => addedByShape.GetValueOrDefault(other));
                foreach (var (_, copy) in made) next.Chains.Add(FromChain(copy));
                addedByShape[id] = made.Select(m => m.Copy).ToList();
                continue;
            }

            int count = shape.Count is null ? 1 : ResolveLowBiased(shape.Count, rng, 0.0);

            for (int i = 0; i < count; i++)
            {
                var diagnostics = new PlacementDiagnostics();
                var placed = GenerateShape(language, shape, grid, rng, geometry, i, 0.0, diagnostics);
                if (placed is null)
                {
                    if (optional) continue;
                    throw new GlyphGenerationException(
                        $"[{language.Name}] Shape '{id}' could not be added to '{glyphDef.Name}' within its configured attempt budget. " +
                        $"{diagnostics.Describe()} Set \"optional\": true on the step to skip a shape that does not fit.");
                }

                placed.ShapeTag = $"{shape.Id}#{i}";
                geometry.Commit(placed, shape.Arrangement.Crossing);
                next.Chains.Add(FromChain(placed));
                if (!addedByShape.TryGetValue(id, out var list)) addedByShape[id] = list = new List<SubSymbolChain>();
                list.Add(placed);
            }
        }

        return next;
    }

    // ---- whole-glyph transforms ------------------------------------------------------------------------

    private static (int MinX, int MinY, int MaxX, int MaxY) Bounds(DerivState s)
    {
        var points = Degrees(s).Keys.ToList();
        return (points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X), points.Max(p => p.Y));
    }

    private static bool Fits(DerivState s, LatticeGrid grid)
    {
        foreach (var p in Degrees(s).Keys) if (!grid.Contains(p)) return false;
        foreach (var chain in s.Chains)
            foreach (var e in chain.Edges)
            {
                if (!e.Edge.IsCircle) continue;
                var (minX, minY, maxX, maxY) = e.Edge.Extent();
                if (minX < -1e-9 || minY < -1e-9 || maxX > grid.Width - 1 + 1e-9 || maxY > grid.Height - 1 + 1e-9) return false;
            }
        return true;
    }

    /// <summary>Maps every point; a straight edge whose two ends land on the same point is dropped.</summary>
    private static DerivState MapPoints(DerivState s, Func<GridPoint, GridPoint> f, double[] affine)
    {
        var chains = new List<WorkChain>();
        bool reflects = affine[0] * affine[3] - affine[1] * affine[2] < 0;
        foreach (var chain in s.Chains)
        {
            var mapped = new WorkChain { Closed = chain.Closed, MotifId = chain.MotifId, ShapeTag = chain.ShapeTag };
            foreach (var e in chain.Edges)
                {
                var edge = e.Edge.WithPoints(f(e.Edge.From), f(e.Edge.To));
                mapped.Edges.Add(new WorkEdge(reflects ? edge.MirroredBend() : edge, e.Origin));
            }

            chains.AddRange(SplitChain(mapped, e => !e.Edge.IsCircle && e.Edge.From.Equals(e.Edge.To)));
        }

        var next = new DerivState { Affine = Then(s.Affine, affine) };
        next.Chains.AddRange(chains);
        next.Lone.AddRange(s.Lone.Select(f));
        return next;
    }

    private static double[] Then(double[] a, double[] b) => new[]
    {
        b[0] * a[0] + b[1] * a[2], b[0] * a[1] + b[1] * a[3],
        b[2] * a[0] + b[3] * a[2], b[2] * a[1] + b[3] * a[3],
        b[0] * a[4] + b[1] * a[5] + b[4], b[2] * a[4] + b[3] * a[5] + b[5],
    };

    private DerivState StepScale(DerivState s, DerivationStep step, DeterministicRandom rng, LatticeGrid grid)
    {
        var (minX, minY, maxX, maxY) = Bounds(s);
        int cx = (minX + maxX) / 2, cy = (minY + maxY) / 2;

        var options = new List<(DerivState State, double Weight)>();
        foreach (var factor in step.Factors ?? new List<ScaleFactorConfig>())
        {
            int num = factor.Numerator, den = Math.Max(1, factor.Denominator);
            GridPoint Scale(GridPoint p) => new(cx + (p.X - cx) * num / den, cy + (p.Y - cy) * num / den);
            double k = num / (double)den;

            var scaled = MapPoints(s, Scale, new[] { k, 0, 0, k, cx * (1 - k), cy * (1 - k) });
            if (scaled.EdgeCount == 0) continue;

            scaled = ShiftIntoGrid(scaled, grid);
            if (scaled is null || !Fits(scaled, grid)) continue;
            options.Add((scaled, factor.Weight));
        }

        if (options.Count == 0) return s; // nothing fits: leave the glyph as it is
        return options[rng.WeightedIndex(options.Select(o => o.Weight).ToList())].State;
    }

    /// <summary>Slides a state back into the grid if its extent just pokes out; null if it cannot fit.</summary>
    private static DerivState? ShiftIntoGrid(DerivState s, LatticeGrid grid)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        foreach (var p in Degrees(s).Keys)
        {
            minX = Math.Min(minX, p.X); minY = Math.Min(minY, p.Y); maxX = Math.Max(maxX, p.X); maxY = Math.Max(maxY, p.Y);
        }
        foreach (var chain in s.Chains)
            foreach (var e in chain.Edges.Where(e => e.Edge.IsCircle))
            {
                var (a, b, c, d) = e.Edge.Extent();
                minX = Math.Min(minX, a); minY = Math.Min(minY, b); maxX = Math.Max(maxX, c); maxY = Math.Max(maxY, d);
            }

        int dx = minX < 0 ? (int)Math.Ceiling(-minX) : maxX > grid.Width - 1 ? -(int)Math.Ceiling(maxX - (grid.Width - 1)) : 0;
        int dy = minY < 0 ? (int)Math.Ceiling(-minY) : maxY > grid.Height - 1 ? -(int)Math.Ceiling(maxY - (grid.Height - 1)) : 0;
        if (dx == 0 && dy == 0) return s;

        return MapPoints(s, p => p.Translate(dx, dy), new double[] { 1, 0, 0, 1, dx, dy });
    }

    private DerivState StepMirror(DerivState s, DerivationStep step, DeterministicRandom rng)
    {
        var axis = step.Axis ?? MirrorAxis.Horizontal;
        if (axis == MirrorAxis.Random) axis = rng.NextBool(0.5) ? MirrorAxis.Horizontal : MirrorAxis.Vertical;

        var (minX, minY, maxX, maxY) = Bounds(s);
        return axis == MirrorAxis.Horizontal
            ? MapPoints(s, p => new GridPoint(minX + maxX - p.X, p.Y), new double[] { -1, 0, 0, 1, minX + maxX, 0 })
            : MapPoints(s, p => new GridPoint(p.X, minY + maxY - p.Y), new double[] { 1, 0, 0, -1, 0, minY + maxY });
    }

    private DerivState StepRotate(DerivState s, DerivationStep step, DeterministicRandom rng, LatticeGrid grid)
    {
        var allowed = step.Degrees is { Count: > 0 } ? step.Degrees : new List<int> { 90, 180, 270 };
        int degrees = allowed[rng.NextInt(0, allowed.Count)];

        var zero = new GridPoint(0, 0);
        var (minX, minY, _, _) = Bounds(s);
        var rotatedMin = Degrees(s).Keys.Select(p => p.RotateAround(zero, degrees)).ToList();
        int dx = minX - rotatedMin.Min(p => p.X), dy = minY - rotatedMin.Min(p => p.Y);

        var ex = new GridPoint(1, 0).RotateAround(zero, degrees);
        var ey = new GridPoint(0, 1).RotateAround(zero, degrees);
        var rotated = MapPoints(s, p => p.RotateAround(zero, degrees).Translate(dx, dy),
            new double[] { ex.X, ey.X, ex.Y, ey.Y, dx, dy });

        var fitted = ShiftIntoGrid(rotated, grid);
        return fitted is not null && Fits(fitted, grid) ? fitted : s;
    }

    // ---- Nudge -----------------------------------------------------------------------------------------

    private DerivState StepNudge(DerivState s, DerivationStep step, DeterministicRandom rng, LatticeGrid grid)
    {
        var degree = Degrees(s);
        var points = SortedPoints(degree);
        int wanted = ResolveAmount(step.Amount, points.Count, rng);
        int reach = Math.Max(1, step.Distance ?? 1);

        var candidates = points.Where(p => TargetMatches(step.Target, degree[p])).ToList();
        Shuffle(candidates, rng);

        int moved = 0;
        foreach (var p in candidates)
        {
            if (moved >= wanted) break;

            var offsets = new List<(int, int)>();
            for (int dx = -reach; dx <= reach; dx++)
                for (int dy = -reach; dy <= reach; dy++)
                    if (dx != 0 || dy != 0) offsets.Add((dx, dy));
            Shuffle(offsets, rng);

            foreach (var (dx, dy) in offsets)
            {
                var target = p.Translate(dx, dy);
                if (!grid.Contains(target) || Degrees(s).ContainsKey(target)) continue;

                var next = MovePoint(s, p, target);
                if (!Fits(next, grid) || CrossesAny(next, target)) continue;

                s = next;
                moved++;
                break;
            }
        }

        return s;
    }

    private static DerivState MovePoint(DerivState s, GridPoint from, GridPoint to)
    {
        var next = new DerivState { Affine = s.Affine };
        foreach (var chain in s.Chains)
        {
            var moved = new WorkChain { Closed = chain.Closed, MotifId = chain.MotifId, ShapeTag = chain.ShapeTag };
            foreach (var e in chain.Edges)
            {
                if (!Touches(e.Edge, from)) { moved.Edges.Add(e); continue; }
                var a = e.Edge.From.Equals(from) ? to : e.Edge.From;
                var b = e.Edge.To.Equals(from) ? to : e.Edge.To;
                moved.Edges.Add(new WorkEdge(e.Edge.WithPoints(a, b).WithBend(null), null)); // moved: its curve no longer fits, so it is planned again
            }
            next.Chains.Add(moved);
        }
        next.Lone.AddRange(s.Lone.Select(p => p.Equals(from) ? to : p));
        return next;
    }

    /// <summary>True if any edge touching the node crosses an edge that does not touch it.</summary>
    private static bool CrossesAny(DerivState s, GridPoint node)
    {
        var all = s.Chains.SelectMany(c => c.Edges).Select(e => e.Edge).ToList();
        foreach (var e in all.Where(e => Touches(e, node)))
            foreach (var other in all)
                if (!Touches(other, node) && EdgePaths.Crosses(e, other)) return true;
        return false;
    }
}
