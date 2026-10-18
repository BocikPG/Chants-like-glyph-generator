using GlyphGen.Core.Config;
using GlyphGen.Core.Grid;
using GlyphGen.Core.Model;
using GlyphGen.Core.Rng;

namespace GlyphGen.Core.Generation;

/// <summary>
/// Default ILanguageGenerator. Ordering: base glyphs first (components that exist only for
/// recipes), then every glyph that is NOT a combination-recipe key, then every glyph that IS
/// one (composites are generated in the order they are listed, so a composite built from
/// another composite must come after it - the config loader enforces this). The returned list
/// is BaseGlyphs followed by Glyphs, in their configured order.
///
/// Every plain and base glyph goes through the quality gate (see QualityConfig): it is compared
/// against every plain/base glyph accepted before it, and regenerated with a fresh seed if it is
/// too similar to one of them or is just a single straight line. Composites contain their
/// components by construction, so they are never compared with plain or base glyphs; they are
/// only compared with each other, and only when quality.compositeMaxSimilarity is below 1.
/// Doing this per glyph, in generation order, means all standalone glyphs are finalized BEFORE
/// any composite is built - so a standalone glyph never changes after a composite has already
/// used it (which would leave that composite stale).
/// </summary>
public sealed class LanguageGenerator : ILanguageGenerator
{
    private readonly IGlyphGenerator _glyphGenerator;

    /// <summary>Problems reported by quality rules with onFail Warn (and Score rules that could not be satisfied), for the host to
    /// show after the run. Cleared at the start of every <see cref="GenerateAll"/>.</summary>
    public List<string> Warnings { get; } = new();
    IReadOnlyList<string> ILanguageGenerator.Warnings => Warnings;

    public LanguageGenerator(IGlyphGenerator glyphGenerator)
    {
        _glyphGenerator = glyphGenerator;
    }

    public IReadOnlyList<Glyph> GenerateAll(LanguageDefinition language, LatticeGrid grid, ulong languageSeed)
    {
        Warnings.Clear();
        _redraws.Clear();
        _coverage.Subjects.Clear();
        _coverage.Against.Clear();
        var generated = new Dictionary<string, Glyph>();
        var accepted = new List<Glyph>();
        var acceptedComposites = new List<Glyph>();
        var usageTracker = new GlyphUsageTracker();
        var composites = new HashSet<string>(language.Combination.Recipes.Keys);

        // Every glyph is generated after the glyphs it is built from (a recipe's components, a derivation's
        // source), pulling those in first when they come later in the lists. Without derivations nothing moves.
        var defs = language.BaseGlyphs.Concat(language.Glyphs).GroupBy(g => g.Name).ToDictionary(g => g.Key, g => g.First());
        var visiting = new HashSet<string>();

        void Ensure(GlyphDefinition glyphDef)
        {
            if (generated.ContainsKey(glyphDef.Name) || !visiting.Add(glyphDef.Name)) return;

            var dependencies = new List<string>();
            if (language.Derivations.TryGetValue(glyphDef.Name, out var derivation)) dependencies.Add(derivation.From);
            if (language.Combination.Recipes.TryGetValue(glyphDef.Name, out var recipe)) dependencies.AddRange(recipe.Components);
            foreach (var name in dependencies)
                if (defs.TryGetValue(name, out var dependency)) Ensure(dependency);

            GenerateInto(language, grid, languageSeed, glyphDef, generated, accepted, acceptedComposites, usageTracker, defs);
        }

        foreach (var glyphDef in language.BaseGlyphs) Ensure(glyphDef);
        foreach (var glyphDef in language.Glyphs.Where(g => !composites.Contains(g.Name))) Ensure(glyphDef);
        foreach (var glyphDef in language.Glyphs.Where(g => composites.Contains(g.Name))) Ensure(glyphDef);

        // A rule whose selectors never matched anything did nothing: almost always a wrong kind or shape id.
        foreach (var rule in language.Quality.Rules.Concat(language.Quality.PairRules))
        {
            if (!_coverage.Subjects.Contains(rule.Id))
                Warnings.Add($"[{language.Name}] quality rule '{rule.Id}' never found a stroke matching its subject (kind {rule.Subject.Kind}, shapes {(rule.Subject.Shapes is { Count: > 0 } ? string.Join("/", rule.Subject.Shapes) : "any")}) in any glyph it applies to, so it checked nothing. Check subject.kind (Dot = a circle of diameter 0, Circle = any circle including dots, Ring = circles without dots) and subject.shapes.");
            else if (!_coverage.Against.Contains(rule.Id))
                Warnings.Add($"[{language.Name}] quality rule '{rule.Id}' never found a stroke matching 'against' (kind {rule.Against.Kind}, shapes {(rule.Against.Shapes is { Count: > 0 } ? string.Join("/", rule.Against.Shapes) : "any")}), so it checked nothing.");
        }

        return language.BaseGlyphs.Concat(language.Glyphs).Select(g => generated[g.Name]).ToList();
    }

    // How many times each base glyph has been redrawn by a quality rule in this run (so every redraw gets a new seed).
    private readonly Dictionary<string, int> _redraws = new();

    // Which quality rules ever found strokes to judge / to compare with in this run.
    private readonly QualityRuleChecker.Coverage _coverage = new();

    /// <summary>Draws a plain/base glyph again and rebuilds every finished composite that uses it. Puts everything back and returns
    /// false when that fails.</summary>
    private bool RedrawComponent(
        LanguageDefinition language, LatticeGrid grid, ulong languageSeed, GlyphDefinition def,
        Dictionary<string, Glyph> generated, List<Glyph> accepted, List<Glyph> acceptedComposites,
        GlyphUsageTracker usageTracker, IReadOnlyDictionary<string, GlyphDefinition> defs)
    {
        if (!generated.TryGetValue(def.Name, out var old)) return false;
        var savedGenerated = new Dictionary<string, Glyph>(generated);
        var savedAccepted = accepted.ToList();
        var savedComposites = acceptedComposites.ToList();

        void Restore()
        {
            generated.Clear();
            foreach (var (k, v) in savedGenerated) generated[k] = v;
            accepted.Clear(); accepted.AddRange(savedAccepted);
            acceptedComposites.Clear(); acceptedComposites.AddRange(savedComposites);
        }

        try
        {
            accepted.Remove(old);
            // A different seed from every earlier redraw of this glyph, however many redraws there were before.
            _redraws[def.Name] = _redraws.GetValueOrDefault(def.Name) + 1;
            GenerateInto(language, grid, languageSeed, def, generated, accepted, acceptedComposites, usageTracker, defs, 1000 + _redraws[def.Name]);

            var dependents = language.Combination.Recipes
                .Where(r => r.Value.Components.Contains(def.Name) && generated.ContainsKey(r.Key))
                .Select(r => r.Key).ToList();
            foreach (var name in dependents)
            {
                var oldComposite = generated[name];
                acceptedComposites.Remove(oldComposite);
                var rebuilt = _glyphGenerator.GenerateGlyph(language, defs[name], grid, SeedTree.ForGlyph(languageSeed, name), generated, usageTracker, variation: 0, languageSeed: languageSeed);
                if (language.Quality.Rules.Count > 0
                    && QualityRuleChecker.Evaluate(rebuilt, language.Quality.Rules, true, _coverage).Any(f => f.Rule.OnFail == QualityAction.Reject))
                {
                    Restore();
                    return false;
                }

                generated[name] = rebuilt;
                acceptedComposites.Add(rebuilt);
            }

            return true;
        }
        catch (GlyphGenerationException)
        {
            Restore();
            return false;
        }
    }

    private void GenerateInto(
        LanguageDefinition language,
        LatticeGrid grid,
        ulong languageSeed,
        GlyphDefinition glyphDef,
        Dictionary<string, Glyph> generated,
        List<Glyph> accepted,
        List<Glyph> acceptedComposites,
        GlyphUsageTracker usageTracker,
        IReadOnlyDictionary<string, GlyphDefinition> defs,
        int seedSalt = 0)
    {
        ulong baseSeed = SeedTree.ForGlyph(languageSeed, glyphDef.Name);
        if (seedSalt != 0) baseSeed = SplitMix64.DeriveSeed(baseSeed, $"quality-redo-{seedSalt}");
        var quality = language.Quality;

        // Plain/base glyphs: the full gate against every accepted plain/base glyph. Composites:
        // no straight-line check, and similarity only against other composites - which with the
        // default compositeMaxSimilarity of 1 means no checks at all, so the first build is
        // accepted exactly as before. (A rebuilt composite re-records its components' usage in
        // the tracker; that only influences which component is regenerated first on a later
        // placement failure.)
        bool isComposite = language.Combination.Recipes.ContainsKey(glyphDef.Name);
        var pool = isComposite ? acceptedComposites : accepted;
        double maxSimilarity = isComposite ? quality.CompositeMaxSimilarity : quality.MaxSimilarity;
        bool rejectStraightLine = !isComposite && quality.RejectStraightLineOnly;

        int maxAttempts = Math.Max(1, quality.MaxRegenerationAttempts);

        var rejections = new List<string>();

        // Score rules: the best attempt so far (lowest penalty), kept if no attempt gets under quality.acceptPenalty.
        Glyph? best = null;
        double bestPenalty = double.MaxValue;
        List<string> bestWarnings = new();
        Dictionary<string, Glyph>? bestSnapshot = null;

        void Accept(Glyph glyph, IEnumerable<string> warnings)
        {
            pool.Add(glyph);
            generated[glyphDef.Name] = glyph;
            foreach (var w in warnings) Warnings.Add($"[{language.Name}] '{glyphDef.Name}': {w}");
        }


        // Rules with redo: redraw the component the finding points at (a plain/base glyph), rebuilding every composite already
        // built from it. All or nothing: if a rebuilt composite no longer fits or breaks a Reject rule, everything is put back.
        void RedoComponents(IEnumerable<QualityFinding> findings, int attempt)
        {
            foreach (var f in findings)
            {
                if (f.Rule.Redo == QualityRedo.Composite) continue;
                string? target = f.Rule.Redo switch
                {
                    QualityRedo.Subject => f.SubjectComponent,
                    QualityRedo.Other => f.OtherComponent,
                    _ => attempt % 2 == 0 ? f.SubjectComponent : f.OtherComponent,
                };
                if (target is null || target == glyphDef.Name || !defs.TryGetValue(target, out var targetDef)) continue;
                if (language.Combination.Recipes.ContainsKey(target) || language.Derivations.ContainsKey(target)) continue;

                RedrawComponent(language, grid, languageSeed, targetDef, generated, accepted, acceptedComposites, usageTracker, defs);
                return; // one redraw per attempt
            }
        }

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            // Attempt 0 uses the glyph's normal seed; later attempts derive a fresh, still
            // fully deterministic seed from it, so a quality retry is reproducible too.
            ulong seed = attempt == 0
                ? baseSeed
                : SplitMix64.DeriveSeed(baseSeed, $"quality-retry-{attempt}");

            var before = new Dictionary<string, Glyph>(generated);
            Glyph candidate;
            try
            {
                candidate = _glyphGenerator.GenerateGlyph(language, glyphDef, grid, seed, generated, usageTracker, variation: attempt, languageSeed: languageSeed);
            }
            catch (GlyphGenerationException) when (best is not null)
            {
                break; // a later attempt could not be built: fall back on the best one so far
            }

            // Building this glyph may have regenerated a component and rebuilt finished composites around it. Those rebuilds
            // do not pass through this loop, so hold them to the Reject rules here; if one breaks, undo all of it.
            var changed = generated.Where(kv => before.TryGetValue(kv.Key, out var old) && !ReferenceEquals(old, kv.Value)).ToList();
            if (changed.Count > 0 && (quality.Rules.Count > 0 || quality.PairRules.Count > 0))
            {
                var broken = changed
                    .Where(kv => language.Combination.Recipes.ContainsKey(kv.Key))
                    .SelectMany(kv => QualityRuleChecker.Evaluate(kv.Value, quality.Rules, true, _coverage)
                        .Where(f => f.Rule.OnFail == QualityAction.Reject).Select(f => (kv.Key, f)))
                    .Concat(changed.SelectMany(kv => QualityRuleChecker.EvaluatePairs(kv.Value, generated, quality.PairRules, _coverage)
                        .Where(f => f.Rule.OnFail == QualityAction.Reject).Select(f => (kv.Key, f))))
                    .ToList();
                if (broken.Count > 0)
                {
                    generated.Clear();
                    foreach (var (k, v) in before) generated[k] = v;
                    rejections.Add("it rebuilt '" + broken[0].Key + $"' so that rule '{broken[0].f.Rule.Id}' broke: {broken[0].f.Example}");
                    continue;
                }
            }
            foreach (var (k, v) in changed)
            {
                var old = before[k];
                int i = accepted.IndexOf(old); if (i >= 0) accepted[i] = v;
                i = acceptedComposites.IndexOf(old); if (i >= 0) acceptedComposites[i] = v;
            }

            bool trivialLine = rejectStraightLine && GlyphQualityChecker.IsTrivialStraightLine(candidate);

            // A derived glyph resembles its source by design: it is held to the (looser) maxSimilarityToSource
            // against that one glyph, and to the normal limit against every other.
            (Glyph Other, double Similarity)? similar = null;
            if (!trivialLine)
            {
                bool derived = language.Derivations.TryGetValue(glyphDef.Name, out var derivation) && generated.ContainsKey(derivation.From);
                similar = GlyphQualityChecker.FindTooSimilar(
                    candidate, derived ? pool.Where(g => g.Name != derivation!.From) : pool, maxSimilarity);
                if (similar is null && derived)
                    similar = GlyphQualityChecker.FindTooSimilar(candidate, new[] { generated[derivation!.From] }, derivation!.MaxSimilarityToSource);
            }

            if (trivialLine || similar is not null)
            {
                rejections.Add(trivialLine
                    ? "it is a single straight line"
                    : $"too similar to '{similar!.Value.Other.Name}' ({similar.Value.Similarity:0.00})");
                continue;
            }

            if (quality.Rules.Count > 0 || quality.PairRules.Count > 0)
            {
                var findings = QualityRuleChecker.Evaluate(candidate, quality.Rules, isComposite, _coverage);
                if (quality.PairRules.Count > 0)
                    findings.AddRange(QualityRuleChecker.EvaluatePairs(candidate, generated, quality.PairRules, _coverage));
                var rejecting = findings.Where(f => f.Rule.OnFail == QualityAction.Reject).ToList();
                if (rejecting.Count > 0)
                {
                    rejections.Add(string.Join(" and ", rejecting.Select(f => $"rule '{f.Rule.Id}' broken ({f.Violations}x): {f.Example}")));
                    RedoComponents(rejecting, attempt);
                    continue;
                }

                var warnings = findings.Where(f => f.Rule.OnFail == QualityAction.Warn)
                    .Select(f => $"rule '{f.Rule.Id}' broken ({f.Violations}x): {f.Example}").ToList();
                var scored = findings.Where(f => f.Rule.OnFail == QualityAction.Score).ToList();
                double penalty = scored.Sum(f => f.Penalty);

                if (penalty > quality.AcceptPenalty)
                {
                    string why = string.Join(" and ", scored.Select(f => $"rule '{f.Rule.Id}' ({f.Violations}x): {f.Example}"));
                    rejections.Add($"penalty {penalty:0.##} above acceptPenalty {quality.AcceptPenalty:0.##} - {why}");
                    bool keep = penalty < bestPenalty;
                    if (keep)
                    {
                        best = candidate;
                        bestPenalty = penalty;
                        bestWarnings = warnings.Concat(scored.Select(f => $"kept with penalty {penalty:0.##}: rule '{f.Rule.Id}' ({f.Violations}x): {f.Example}")).ToList();
                        bestSnapshot = isComposite && language.Combination.Recipes.TryGetValue(glyphDef.Name, out var rec)
                            ? rec.Components.Distinct().Where(generated.ContainsKey).ToDictionary(n => n, n => generated[n])
                            : new Dictionary<string, Glyph>();
                    }
                    continue;
                }

                Accept(candidate, warnings);
                return;
            }

            Accept(candidate, Array.Empty<string>());
            return;
        }

        // Nothing got under the penalty limit: keep the best attempt, unless one of its components was redrawn since (then it
        // no longer matches the registry and is useless).
        if (best is not null && bestSnapshot is not null && bestSnapshot.All(kv => generated.TryGetValue(kv.Key, out var now) && ReferenceEquals(now, kv.Value)))
        {
            Accept(best, bestWarnings);
            return;
        }

        // Say WHY each attempt was rejected (capped, so a huge attempt budget doesn't flood the console).
        const int shown = 8;
        string reasons = string.Join("; ", rejections.Take(shown).Select((r, i) => $"attempt {i + 1}: {r}"))
            + (rejections.Count > shown ? $"; ... and {rejections.Count - shown} more" : "");

        bool mostlySimilar = rejections.Count(r => r.StartsWith("too similar")) * 2 >= rejections.Count;
        bool derivedGlyph = language.Derivations.TryGetValue(glyphDef.Name, out var derivedFrom);
        bool similarToSource = derivedGlyph && rejections.Any(r => r.Contains($"'{derivedFrom!.From}'"));
        string moreAttempts = maxAttempts <= 3 ? $" quality.maxRegenerationAttempts is only {maxAttempts}: raising it gives the generator more tries." : "";
        string hint = isComposite
            ? "Try raising quality.compositeMaxSimilarity, or giving the recipes different components or more placement freedom (scale/rotation, a larger grid)." + moreAttempts
            : similarToSource
                ? $"A derived glyph must differ from its source: raise derivations['{glyphDef.Name}'].maxSimilarityToSource, or make its steps change more (delete more, add a shape, mirror or rotate)." + moreAttempts
            : derivedGlyph && mostlySimilar
                ? "Derived glyphs are held to quality.maxSimilarity against every glyph except their source: raise it, or make the steps change the source more." + moreAttempts
            : mostlySimilar
                ? "Try raising quality.maxSimilarity, widening the shapes' chain.length or count ranges, adding connectionRules or shapes, or using a larger grid." + moreAttempts
                : "Try longer chains (chain.length) or a higher count on the shapes, or set quality.rejectStraightLineOnly to false." + moreAttempts;

        string what = isComposite ? "a composite sufficiently distinct from the other composites" : "a sufficiently distinct, non-trivial glyph";

        throw new GlyphGenerationException(
            $"[{language.Name}] Could not generate {what} for " +
            $"'{glyphDef.Name}' after {maxAttempts} attempts " +
            $"(max similarity {maxSimilarity:0.##}, reject straight line: {rejectStraightLine}). " +
            $"Rejected because: {reasons}. {hint}");
    }
}
