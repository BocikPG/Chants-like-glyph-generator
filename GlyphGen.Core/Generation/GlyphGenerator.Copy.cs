using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Shapes with copyOf: instead of being drawn, the shape becomes a flipped, moved copy of every instance of an
/// earlier shape. Every random choice (flip, anchor, offset order) comes from streams derived from the
/// copy's own label, so shapes without copyOf, and glyphs without copies, draw exactly what they always did.
/// </summary>
/// <summary>A copy shape that could not be placed; carries the shape so the glyph can be retried.</summary>
public sealed class CopyPlacementException : GlyphGenerationException
{
    public ShapeDefinition Shape { get; }
    public CopyPlacementException(ShapeDefinition shape, string message) : base(message) { Shape = shape; }
}

public sealed partial class GlyphGenerator
{
    /// <summary>Copy = A * original + (Tx, Ty), with A the 2x2 matrix [A11 A12; A21 A22].</summary>
    private readonly record struct CopyMap(int A11, int A12, int A21, int A22, int Tx, int Ty)
    {
        public GridPoint Apply(GridPoint p) => new(A11 * p.X + A12 * p.Y + Tx, A21 * p.X + A22 * p.Y + Ty);
    }

    /// <summary>Why the positions tried for one copy were rejected, so the error can say what to change. Each position
    /// is counted once, under the first rule it broke.</summary>
    private sealed class CopyDiagnostics
    {
        public CopyFlip Flip;
        public int Tried;
        public bool CapHit;
        public int OffGrid;
        public int TooBigToMove;
        public int Spacing;
        public int SelfSpacing;
        public int Crossing;
        public int MinW = int.MaxValue, MinH = int.MaxValue;
        public bool MoveAllowed;

        public string Describe(ShapeDefinition shape, LatticeGrid grid, IReadOnlyList<string> forbidsCrossing)
        {
            var cfg = shape.CopyOf!;
            string flip = cfg.Flip == CopyFlip.Auto ? $"Auto, {cfg.Auto.By} -> {Flip}" : cfg.Flip == CopyFlip.Random ? $"Random -> {Flip}" : Flip.ToString();
            string text = $"Flip {flip}, anchor {cfg.Anchor}, gap {cfg.Gap.Min}..{cfg.Gap.Max}, shift {cfg.Shift.Min}..{cfg.Shift.Max}: " +
                $"{Tried} position(s) tried{(CapHit ? $" (the search stops at {MaxCopyCandidates})" : "")}. ";

            var parts = new List<string>();
            if (OffGrid > 0)
            {
                parts.Add(MoveAllowed
                    ? $"{OffGrid} would leave the grid" + (TooBigToMove == 0 ? "" :
                        $", {TooBigToMove} of them even after moving the glyph (the glyph and its copy together need at least {MinW} x {MinH} nodes, the grid is {grid.Width} x {grid.Height})")
                    : $"{OffGrid} left the grid" + (cfg.MoveGlyphToFit ? " (copies in a derivation step cannot move the glyph)" : " (copyOf.moveGlyphToFit is off)"));
            }
            if (Spacing > 0) parts.Add($"{Spacing} broke an arrangement.spacing rule");
            if (SelfSpacing > 0) parts.Add($"{SelfSpacing} broke the original's chain.selfSpacing (copyOf.respectSelfSpacing)");
            if (Crossing > 0) parts.Add($"{Crossing} crossed existing strokes");
            text += parts.Count == 0 ? "No position was available at all." : string.Join(", ", parts) + ".";

            string hint = "";
            if (Crossing > 0 && Crossing >= OffGrid && Crossing >= Spacing)
            {
                // A crossing is only allowed when BOTH shapes allow it, so the shapes before this one count too.
                var fixes = new List<string>();
                if (shape.Arrangement.Crossing.Mode != CrossingMode.Anywhere) fixes.Add("on this shape");
                if (forbidsCrossing.Count > 0) fixes.Add($"on the earlier shape(s) that forbid being crossed ({string.Join(", ", forbidsCrossing.Select(x => $"'{x}'"))})");
                hint = fixes.Count > 0
                    ? $" A crossing needs both shapes to allow it: set arrangement.crossing.mode to NodesOnly or Anywhere {string.Join(" and ", fixes)}."
                    : " Use a larger gap or shift, or another anchor or flip, to move the copy off the existing strokes.";
            }
            else if (TooBigToMove > 0 && TooBigToMove >= Spacing)
                hint = " The two are simply too big for the grid together: use a smaller gap, shorter strokes or a larger grid.";
            else if (OffGrid > 0 && OffGrid >= Spacing)
                hint = MoveAllowed ? " Try a smaller gap/shift or a flip that turns toward the middle." : " Turn copyOf.moveGlyphToFit on, or use a smaller gap/shift or a flip that turns toward the middle.";
            else if (Spacing > 0)
                hint = " Spacing rejects most positions: relax arrangement.spacing (a gap of 0 makes the copy share a node with the original).";

            if (SelfSpacing > 0 && hint == "" && SelfSpacing >= Crossing)
                hint = " The original's selfSpacing rejects most positions: use a larger gap/shift, or turn copyOf.respectSelfSpacing off.";

            if (Tried <= 1) hint += " Only one position exists: widen gap/shift to give it more to choose from.";
            return text + hint;
        }
    }

    private const int MaxAnchors = 60;
    private const int MaxCopyCandidates = 800;

    /// <summary>Makes one copy of each origin chain and commits it to the geometry. A copy that cannot be placed
    /// throws, unless optional, in which case it is skipped. With a world (the glyph being built) the copies are added to
    /// it, and when a copy only fits after the whole glyph is moved, the glyph is moved.</summary>
    private List<(SubSymbolChain Origin, SubSymbolChain Copy)> MakeCopies(
        LanguageDefinition language,
        ShapeDefinition shape,
        IReadOnlyList<SubSymbolChain> origins,
        LatticeGrid grid,
        DeterministicRandom glyphRng,
        GeometryContext geometry,
        string glyphName,
        bool optional,
        Glyph? world = null,
        Func<string, IReadOnlyList<SubSymbolChain>?>? chainsOfShape = null)
    {
        var made = new List<(SubSymbolChain, SubSymbolChain)>();
        IReadOnlyList<SelfSpacingRule>? selfRules = null;
        bool ignoreOriginal = shape.CopyOf!.SelfSpacingIgnoresOriginal;
        if (shape.CopyOf!.RespectSelfSpacing)
        {
            string rootId = RootShapeId(language, shape);
            selfRules = language.Shapes.FirstOrDefault(x => x.Id == rootId)?.Chain.SelfSpacing;
        }

        for (int i = 0; i < origins.Count; i++)
        {
            var rng = glyphRng.Derive($"copy-{shape.Id}-{i}");
            var diag = new CopyDiagnostics();
            var copy = CopyChain(shape, origins[i], grid, rng, geometry, out var map, out var shift,
                allowShift: world is not null && shape.CopyOf!.MoveGlyphToFit, diag, chainsOfShape, selfRules, ignoreOriginal);
            if (copy is null)
            {
                if (optional) continue;
                throw new CopyPlacementException(
                    shape,
                    $"[{language.Name}] Shape '{shape.Id}' (a copy of '{shape.CopyOf!.Shape}'" +
                    (origins.Count > 1 ? $", instance {i + 1}/{origins.Count}" : "") +
                    $") for glyph '{glyphName}' could not be placed. {diag.Describe(shape, grid, language.Shapes.TakeWhile(x => x != shape).Where(x => x.Arrangement.Crossing.Mode != CrossingMode.Anywhere).Select(x => x.Id).ToList())}");
            }

            if (shift != (0, 0))
            {
                var (dx, dy) = shift;
                foreach (var chain in world!.Chains) MoveChain(chain, dx, dy);
                geometry.Translate(dx, dy);
                MoveChain(copy, dx, dy);

                // The copy was measured before the move; its map follows the same move.
                map = map with
                {
                    Tx = map.Tx + dx - (map.A11 * dx + map.A12 * dy),
                    Ty = map.Ty + dy - (map.A21 * dx + map.A22 * dy),
                };
            }

            geometry.Commit(copy, shape.Arrangement.Crossing);

            // Mirrored copies already carry their bends (see Transformed); anything still unplanned gets its own.
            BendPlanner.Plan(copy, language.Curve, rng.Derive("bend"), curved: BendPlanner.AnyCurved(copy, language.Curve));

            if (world is not null)
                world.Chains.Add(copy);

            made.Add((origins[i], copy));
        }

        return made;
    }

    private static void MoveChain(SubSymbolChain chain, int dx, int dy)
    {
        for (int i = 0; i < chain.Nodes.Count; i++) chain.Nodes[i] = chain.Nodes[i].Translate(dx, dy);
        for (int i = 0; i < chain.Edges.Count; i++)
            chain.Edges[i] = chain.Edges[i].WithPoints(chain.Edges[i].From.Translate(dx, dy), chain.Edges[i].To.Translate(dx, dy));
    }


    private SubSymbolChain? CopyChain(
        ShapeDefinition shape, SubSymbolChain origin, LatticeGrid grid, DeterministicRandom rng,
        GeometryContext geometry, out CopyMap map, out (int X, int Y) shift, bool allowShift, CopyDiagnostics diag,
        Func<string, IReadOnlyList<SubSymbolChain>?>? chainsOfShape = null, IReadOnlyList<SelfSpacingRule>? selfRules = null, bool ignoreOriginal = false)
    {
        map = default;
        shift = (0, 0);
        if (origin.Nodes.Count == 0) return null;

        var cfg = shape.CopyOf!;
        geometry.SetCurrent(shape.Id, shape.Arrangement.Crossing);

        var start = origin.Nodes[0];
        // What the Auto rule looks at: the listed shapes' strokes, or just the chain being copied.
        var basis = new List<SubSymbolChain>();
        if (cfg.Flip == CopyFlip.Auto && cfg.Auto.Shapes is { Count: > 0 } listed && chainsOfShape is not null)
            foreach (var id in listed) basis.AddRange(chainsOfShape(id) ?? Array.Empty<SubSymbolChain>());
        basis.RemoveAll(c => c.Nodes.Count == 0);
        if (basis.Count == 0) basis.Add(origin);

        // The copy is thrown away from where the original lies; the Auto rule's own centre is the basis' (below).
        double cx = origin.Nodes.Average(n => n.X), cy = origin.Nodes.Average(n => n.Y);
        double bx = basis.SelectMany(c => c.Nodes).Average(n => n.X), by = basis.SelectMany(c => c.Nodes).Average(n => n.Y);
        double midX = (grid.Width - 1) / 2.0, midY = (grid.Height - 1) / 2.0;

        var flipRng = rng.Derive("flip");
        (double X, double Y)? pushTo = null; // the way the copy is pushed, when the rule says so
        CopyFlip flip;

        if (cfg.Flip == CopyFlip.Auto && cfg.Auto.By == CopyAutoBy.CenterWeight)
        {
            double dx = midX - bx, dy = midY - by;
            flip = Math.Abs(dx) > Math.Abs(dy) + 1e-9 ? CopyFlip.Horizontal
                 : Math.Abs(dy) > Math.Abs(dx) + 1e-9 ? CopyFlip.Vertical
                 : flipRng.NextBool(0.5) ? CopyFlip.Horizontal : CopyFlip.Vertical;
            pushTo = cfg.Auto.Toward == CopyToward.Middle ? (dx, dy) : (-dx, -dy);
        }
        else if (cfg.Flip == CopyFlip.Auto)
        {
            flip = LongestStraightFlip(basis, flipRng, cfg.Auto.Axis);
        }
        else if (cfg.Flip == CopyFlip.Random)
        {
            var pool = cfg.Flips is { Count: > 0 } ? cfg.Flips : new List<CopyFlip> { CopyFlip.Horizontal, CopyFlip.Vertical, CopyFlip.Rotate180 };
            flip = pool[flipRng.NextInt(0, pool.Count)];
        }
        else
        {
            flip = cfg.Flip;
        }

        diag.Flip = flip;
        diag.MoveAllowed = allowShift;

        var (a11, a12, a21, a22) = flip switch
        {
            CopyFlip.Horizontal => (-1, 0, 0, 1),
            CopyFlip.Vertical => (1, 0, 0, -1),
            CopyFlip.Rotate180 => (-1, 0, 0, -1),
            CopyFlip.Diagonal => (0, 1, 1, 0),
            CopyFlip.AntiDiagonal => (0, -1, -1, 0),
            _ => (1, 0, 0, 1),
        };

        // The direction "apart" runs in: the way the flip throws the copy (away from the origin's middle through its
        // start), or the way the CenterWeight rule pushes it. Gap moves along it, shift across it.
        var (nx, ny) = NormalFor(flip, (start.X - cx, start.Y - cy), pushTo, flipRng);
        int lsx = a11 * start.X + a12 * start.Y, lsy = a21 * start.X + a22 * start.Y;

        var anchors = AnchorsFor(cfg.Anchor, start, geometry, rng.Derive("anchor"));

        var offsets = new List<(int Gap, int Shift)>();
        for (int g = cfg.Gap.Min; g <= cfg.Gap.Max; g++)
            for (int sh = cfg.Shift.Min; sh <= cfg.Shift.Max; sh++)
            {
                offsets.Add((g, sh));
                if (sh != 0) offsets.Add((g, -sh));
            }

        var combos = new List<(GridPoint Anchor, int Gap, int Shift)>();
        foreach (var a in anchors)
            foreach (var (g, sh) in offsets)
                combos.Add((a, g, sh));

        var order = rng.Derive("order");
        Shuffle(combos, order);
        diag.CapHit = combos.Count > MaxCopyCandidates;
        if (combos.Count > MaxCopyCandidates) combos.RemoveRange(MaxCopyCandidates, combos.Count - MaxCopyCandidates);

        var bias = shape.Arrangement.Bias;
        int need = bias.IsActive ? Math.Max(1, shape.Arrangement.Candidates) : 1;
        bool prefer = cfg.PreferNoCrossing;
        bool crossingAllowed = shape.Arrangement.Crossing.Mode is CrossingMode.Anywhere or CrossingMode.NodesOnly;

        var clean = new List<(SubSymbolChain Chain, CopyMap Map)>();
        (SubSymbolChain Chain, CopyMap Map, int Soft)? fallback = null;

        foreach (var (anchor, gap, sideShift) in combos)
        {
            int offX = gap * nx + sideShift * -ny, offY = gap * ny + sideShift * nx;
            var candidateMap = new CopyMap(a11, a12, a21, a22, anchor.X - lsx + offX, anchor.Y - lsy + offY);
            var chain = Transformed(origin, candidateMap, shape);
            diag.Tried++;
            if (!FitsGrid(chain, grid)) { diag.OffGrid++; continue; }
            if (!SubSymbolSpacingChecker.SatisfiesSpacing(chain.AllNodes(), geometry.NodeGroups, shape.Arrangement.Spacing)) { diag.Spacing++; continue; }
            if (selfRules is not null && !RespectsOriginSelfSpacing(chain, origin, selfRules, ignoreOriginal, geometry)) { diag.SelfSpacing++; continue; }
            if (geometry.CountBlocked(chain) > 0) { diag.Crossing++; continue; }

            int soft = prefer ? geometry.CountCrossings(chain) : 0;
            if (soft == 0)
            {
                clean.Add((chain, candidateMap));
                if (clean.Count >= need) break;
            }
            else if (fallback is null || soft < fallback.Value.Soft)
            {
                fallback = (chain, candidateMap, soft);
            }
        }

        if (clean.Count == 0 && allowShift)
        {
            // Nothing fits where the glyph is now: look for a position that would, once the whole glyph is moved.
            var moved = ShiftedCandidate(origin, combos, shape, grid, geometry, a11, a12, a21, a22, lsx, lsy, nx, ny, crossingAllowed, prefer, diag, selfRules, ignoreOriginal);
            if (moved is { } m && (m.Soft == 0 || (fallback is null && crossingAllowed)))
            {
                map = m.Map;
                shift = m.Shift;
                return m.Chain;
            }
        }

        if (clean.Count == 0)
        {
            // Only crossing positions are left: kept only when the destination may cross at all.
            if (fallback is null || !crossingAllowed) return null;
            map = fallback.Value.Map;
            return fallback.Value.Chain;
        }

        var best = clean[0];
        if (clean.Count > 1)
        {
            double bestScore = BiasScoring.ScoreWithCandidate(bias, geometry.NodeGroups, best.Chain.Nodes, grid);
            for (int i = 1; i < clean.Count; i++)
            {
                double score = BiasScoring.ScoreWithCandidate(bias, geometry.NodeGroups, clean[i].Chain.Nodes, grid);
                if (score < bestScore) { best = clean[i]; bestScore = score; }
            }
        }

        map = best.Map;
        return best.Chain;
    }

    /// <summary>The position that needs the smallest move of the whole glyph (existing strokes and copy together) to
    /// fit the grid; positions that cross nothing come before positions that cross.</summary>
    private (SubSymbolChain Chain, CopyMap Map, (int X, int Y) Shift, int Soft)? ShiftedCandidate(
        SubSymbolChain origin, List<(GridPoint Anchor, int Gap, int Shift)> combos, ShapeDefinition shape, LatticeGrid grid,
        GeometryContext geometry, int a11, int a12, int a21, int a22, int lsx, int lsy, int nx, int ny,
        bool crossingAllowed, bool prefer, CopyDiagnostics diag, IReadOnlyList<SelfSpacingRule>? selfRules, bool ignoreOriginal)
    {
        const double eps = 1e-9;
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Grow(double x0, double y0, double x1, double y1)
        {
            minX = Math.Min(minX, x0); minY = Math.Min(minY, y0); maxX = Math.Max(maxX, x1); maxY = Math.Max(maxY, y1);
        }

        foreach (var group in geometry.NodeGroups) foreach (var n in group) Grow(n.X, n.Y, n.X, n.Y);
        foreach (var e in geometry.ExistingEdges) { var x = e.Extent(); Grow(x.MinX, x.MinY, x.MaxX, x.MaxY); }

        (SubSymbolChain Chain, CopyMap Map, (int X, int Y) Shift, int Soft)? best = null;
        foreach (var (anchor, gap, shiftAmount) in combos)
        {
            int offX = gap * nx + shiftAmount * -ny, offY = gap * ny + shiftAmount * nx;
            var map = new CopyMap(a11, a12, a21, a22, anchor.X - lsx + offX, anchor.Y - lsy + offY);
            var chain = Transformed(origin, map, shape);

            // Positions that were off the grid were counted as such; they are re-sorted here by what stops them next.
            bool wasOffGrid = !FitsGrid(chain, grid);
            if (!SubSymbolSpacingChecker.SatisfiesSpacing(chain.AllNodes(), geometry.NodeGroups, shape.Arrangement.Spacing))
            {
                if (wasOffGrid) { diag.OffGrid--; diag.Spacing++; }
                continue;
            }
            if (selfRules is not null && !RespectsOriginSelfSpacing(chain, origin, selfRules, ignoreOriginal, geometry))
            {
                if (wasOffGrid) { diag.OffGrid--; diag.SelfSpacing++; }
                continue;
            }
            if (geometry.CountBlocked(chain) > 0)
            {
                if (wasOffGrid) { diag.OffGrid--; diag.Crossing++; }
                continue;
            }

            double x0 = minX, y0 = minY, x1 = maxX, y1 = maxY;
            foreach (var n in chain.Nodes) { x0 = Math.Min(x0, n.X); y0 = Math.Min(y0, n.Y); x1 = Math.Max(x1, n.X); y1 = Math.Max(y1, n.Y); }
            foreach (var e in chain.Edges) { var x = e.Extent(); x0 = Math.Min(x0, x.MinX); y0 = Math.Min(y0, x.MinY); x1 = Math.Max(x1, x.MaxX); y1 = Math.Max(y1, x.MaxY); }

            int dx = x0 < -eps ? (int)Math.Ceiling(-x0 - eps) : x1 > grid.Width - 1 + eps ? -(int)Math.Ceiling(x1 - (grid.Width - 1) - eps) : 0;
            int dy = y0 < -eps ? (int)Math.Ceiling(-y0 - eps) : y1 > grid.Height - 1 + eps ? -(int)Math.Ceiling(y1 - (grid.Height - 1) - eps) : 0;
            if (x0 + dx < -eps || x1 + dx > grid.Width - 1 + eps || y0 + dy < -eps || y1 + dy > grid.Height - 1 + eps)
            {
                if (wasOffGrid)
                {
                    diag.TooBigToMove++;
                    int w = (int)Math.Ceiling(x1 - x0 - eps) + 1, h = (int)Math.Ceiling(y1 - y0 - eps) + 1;
                    if (w + h < diag.MinW + diag.MinH || diag.MinW == int.MaxValue) { diag.MinW = w; diag.MinH = h; }
                }
                continue;
            }

            int soft = prefer ? geometry.CountCrossings(chain) : 0;
            if (soft > 0 && !crossingAllowed)
            {
                if (wasOffGrid) { diag.OffGrid--; diag.Crossing++; }
                continue;
            }

            bool better = best is null || soft < best.Value.Soft
                || (soft == best.Value.Soft && Math.Abs(dx) + Math.Abs(dy) < Math.Abs(best.Value.Shift.X) + Math.Abs(best.Value.Shift.Y));
            if (better) best = (chain, map, (dx, dy), soft);
        }

        return best;
    }

    /// <summary>The original shape's selfSpacing rules between the copy and the chain it copies (see CopyConfig.RespectSelfSpacing).
    /// With ignoreOriginal, Existing and AllNodes rules measure against the rest of the glyph instead of the original.</summary>
    private static bool RespectsOriginSelfSpacing(
        SubSymbolChain copy, SubSymbolChain origin, IReadOnlyList<SelfSpacingRule> rules, bool ignoreOriginal, GeometryContext geometry)
    {
        List<GridPoint>? others = null;
        foreach (var rule in rules)
        {
            if (rule.MinDistance <= 0 && rule.MaxDistance <= 0) continue;
            double minSq = rule.MinDistance > 0 ? rule.MinDistance * rule.MinDistance - 1e-9 : 0;
            double maxSq = rule.MaxDistance > 0 ? rule.MaxDistance * rule.MaxDistance + 1e-9 : double.MaxValue;
            bool strict = rule.Mode == SelfSpacingMode.AllNodes;

            IEnumerable<GridPoint> against = origin.Nodes;
            if (ignoreOriginal && rule.Mode != SelfSpacingMode.Adjacent)
            {
                if (others is null)
                {
                    others = new List<GridPoint>();
                    bool skipped = false;
                    foreach (var group in geometry.NodeGroups)
                    {
                        if (!skipped && group.SequenceEqual(origin.Nodes)) { skipped = true; continue; }
                        others.AddRange(group);
                    }
                }
                against = others;
            }

            foreach (var c in copy.Nodes)
                foreach (var o in against)
                {
                    double dx = c.X - o.X, dy = c.Y - o.Y, d = dx * dx + dy * dy;
                    if (d > maxSq) return false;
                    if (!strict && c == o) continue; // a node the two share
                    if (d < minSq) return false;
                }
        }
        return true;
    }

    private static SubSymbolChain Transformed(SubSymbolChain origin, CopyMap map, ShapeDefinition shape)
    {
        var chain = new SubSymbolChain
        {
            IsClosedLoop = origin.IsClosedLoop,
            MotifId = origin.MotifId is null ? null : shape.Id,
            ShapeTag = origin.ShapeTag is { } tag ? $"{shape.Id}#{tag[(tag.LastIndexOf('#') + 1)..]}" : null,
        };
        foreach (var n in origin.Nodes) chain.Nodes.Add(map.Apply(n));
        bool reflects = map.A11 * map.A22 - map.A12 * map.A21 < 0;
        foreach (var e in origin.Edges)
        {
            var moved = e.WithPoints(map.Apply(e.From), map.Apply(e.To));
            // A reflection turns a curve's bulge to the other side of its edge; a copy that does not mirror curves is replanned.
            if (!shape.CopyOf!.MirrorCurves) moved = moved.IsCircle ? moved : moved.WithBend(null);
            else if (reflects) moved = moved.MirroredBend();
            if (reflects) moved = moved.MirroredArc();
            chain.Edges.Add(moved);
        }
        return chain;
    }

    private static bool FitsGrid(SubSymbolChain chain, LatticeGrid grid)
    {
        foreach (var n in chain.Nodes) if (!grid.Contains(n)) return false;
        foreach (var e in chain.Edges)
        {
            if (!grid.Contains(e.From) || !grid.Contains(e.To)) return false;
            if (!e.IsCircle) continue;
            var (minX, minY, maxX, maxY) = e.Extent();
            if (minX < -1e-9 || minY < -1e-9 || maxX > grid.Width - 1 + 1e-9 || maxY > grid.Height - 1 + 1e-9) return false;
        }
        return true;
    }


    /// <summary>The unit step "apart" for a flip: the flip's own axis, signed by where the push is aimed
    /// (or by which side of the origin's middle the start lies on); a tie is broken at random.</summary>
    private static (int X, int Y) NormalFor(CopyFlip flip, (double X, double Y) startFromMiddle, (double X, double Y)? pushTo, DeterministicRandom rng)
    {
        var aim = pushTo ?? startFromMiddle;

        (int X, int Y) axis;
        switch (flip)
        {
            case CopyFlip.Horizontal: axis = (1, 0); break;
            case CopyFlip.Vertical: axis = (0, 1); break;
            case CopyFlip.Diagonal: axis = (1, -1); break;
            case CopyFlip.AntiDiagonal: axis = (1, 1); break;
            case CopyFlip.Rotate180:
                axis = Math.Abs(aim.X) > Math.Abs(aim.Y) + 1e-9 ? (1, 0)
                     : Math.Abs(aim.Y) > Math.Abs(aim.X) + 1e-9 ? (0, 1)
                     : rng.NextBool(0.5) ? (1, 0) : (0, 1);
                break;
            default: // None: a random orthogonal direction
                axis = rng.NextBool(0.5) ? (1, 0) : (0, 1);
                aim = (0, 0);
                break;
        }

        double dot = aim.X * axis.X + aim.Y * axis.Y;
        bool positive = dot > 1e-9 ? true : dot < -1e-9 ? false : rng.NextBool(0.5);
        return positive ? axis : (-axis.X, -axis.Y);
    }

    /// <summary>Finds the longest run of consecutive edges continuing in one straight horizontal, vertical or
    /// diagonal line and returns the flip ACROSS it. Equal runs are broken at random; no run at all
    /// (only circles or odd angles) picks any of the plain flips.</summary>
    private static CopyFlip LongestStraightFlip(IReadOnlyList<SubSymbolChain> chains, DeterministicRandom rng, CopyAxis axis)
    {
        var best = new int[4];
        foreach (var chain in chains)
        {
        int prevX = 0, prevY = 0, run = 0; // a run never continues from one chain into the next

        foreach (var e in chain.Edges)
        {
            if (e.IsCircle) { prevX = prevY = run = 0; continue; }
            int dx = e.To.X - e.From.X, dy = e.To.Y - e.From.Y;
            int g = Gcd(Math.Abs(dx), Math.Abs(dy));
            if (g == 0) { prevX = prevY = run = 0; continue; }

            int ux = dx / g, uy = dy / g;
            run = ux == prevX && uy == prevY ? run + g : g;
            prevX = ux; prevY = uy;

            int cls = (ux, uy) switch
            {
                (1, 0) or (-1, 0) => 0,
                (0, 1) or (0, -1) => 1,
                (1, 1) or (-1, -1) => 2,
                (1, -1) or (-1, 1) => 3,
                _ => -1,
            };
            if (cls >= 0) best[cls] = Math.Max(best[cls], run);
        }
        }

        int max = best.Max();
        if (max == 0)
        {
            var plain = new[] { CopyFlip.Horizontal, CopyFlip.Vertical, CopyFlip.Rotate180 };
            return plain[rng.NextInt(0, plain.Length)];
        }

        var tied = Enumerable.Range(0, 4).Where(i => best[i] == max).ToList();
        int pick = tied[tied.Count == 1 ? 0 : rng.NextInt(0, tied.Count)];
        bool parallel = axis == CopyAxis.Parallel || (axis == CopyAxis.Random && rng.NextBool(0.5));
        return (pick, parallel) switch
        {
            (0, false) => CopyFlip.Horizontal,
            (1, false) => CopyFlip.Vertical,
            (2, false) => CopyFlip.AntiDiagonal,
            (3, false) => CopyFlip.Diagonal,
            (0, true) => CopyFlip.Vertical,
            (1, true) => CopyFlip.Horizontal,
            (2, true) => CopyFlip.Diagonal,
            _ => CopyFlip.AntiDiagonal,
        };
    }

    private static List<GridPoint> AnchorsFor(CopyAnchor anchor, GridPoint start, GeometryContext geometry, DeterministicRandom rng)
    {
        var points = new List<GridPoint>();
        switch (anchor)
        {
            case CopyAnchor.ExistingNode:
                points.AddRange(geometry.NodeGroups.SelectMany(g => g).Distinct());
                break;

            case CopyAnchor.ExistingEdge:
                var seen = new HashSet<GridPoint>();
                foreach (var e in geometry.ExistingEdges)
                {
                    if (e.IsCircle) continue;
                    int dx = e.To.X - e.From.X, dy = e.To.Y - e.From.Y;
                    int g = Gcd(Math.Abs(dx), Math.Abs(dy));
                    for (int k = 0; k <= g; k++)
                    {
                        var p = g == 0 ? e.From : new GridPoint(e.From.X + dx / g * k, e.From.Y + dy / g * k);
                        if (seen.Add(p)) points.Add(p);
                    }
                }
                break;
        }

        if (points.Count == 0) return new List<GridPoint> { start };

        Shuffle(points, rng);
        if (points.Count > MaxAnchors) points.RemoveRange(MaxAnchors, points.Count - MaxAnchors);
        return points;
    }

    private static void Shuffle<T>(List<T> list, DeterministicRandom rng)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = rng.NextInt(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }

    private static int Gcd(int a, int b)
    {
        while (b != 0) (a, b) = (b, a % b);
        return a;
    }
}
