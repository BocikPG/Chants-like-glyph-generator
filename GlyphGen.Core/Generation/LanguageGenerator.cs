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

    /// <summary>Threads used to try quality retries of a plain/base glyph ahead of time (1 = none, 0 = all cores). The glyph kept is the
    /// first attempt, in attempt order, that passes - the same one a single thread finds.</summary>
    public int MaxParallelism { get; set; } = 1;

    // First attempts of the independent plain/base glyphs, drawn ahead of time on several threads (see PrefetchPlainGlyphs).
    private readonly Dictionary<string, (Glyph? Glyph, System.Runtime.ExceptionServices.ExceptionDispatchInfo? Error)> _prefetched = new();

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
        _prefetched.Clear();

        // Every glyph is generated after the glyphs it is built from (a recipe's components, a derivation's
        // source), pulling those in first when they come later in the lists. Without derivations nothing moves.
        var defs = language.BaseGlyphs.Concat(language.Glyphs).GroupBy(g => g.Name).ToDictionary(g => g.Key, g => g.First());
        var visiting = new HashSet<string>();
        PrefetchPlainGlyphs(language, grid, languageSeed, defs, composites, generated, usageTracker);

        void Ensure(GlyphDefinition glyphDef)
        {
            if (generated.ContainsKey(glyphDef.Name) || !visiting.Add(glyphDef.Name)) return;

            var dependencies = new List<string>();
            if (language.Derivations.TryGetValue(glyphDef.Name, out var derivation)) dependencies.Add(derivation.From);
            if (language.Combination.Recipes.TryGetValue(glyphDef.Name, out var recipe)) dependencies.AddRange(recipe.Components);
            dependencies.AddRange(NodeSources.Of(language, glyphDef.Name));
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

    /// <summary>
    /// With more than one thread: draws the FIRST attempt of every plain/base glyph that depends on no other glyph (no recipe, no
    /// derivation) at the same time. Such an attempt depends only on the glyph's own seed, so it is the very glyph the sequential
    /// run would draw; the quality checks still run one glyph at a time in the usual order and take the prepared draw (or retry).
    /// </summary>
    private void PrefetchPlainGlyphs(
        LanguageDefinition language, LatticeGrid grid, ulong languageSeed, IReadOnlyDictionary<string, GlyphDefinition> defs,
        HashSet<string> composites, Dictionary<string, Glyph> generated, GlyphUsageTracker usageTracker)
    {
        int threads = MaxParallelism <= 0 ? Environment.ProcessorCount : MaxParallelism;
        if (threads <= 1) return;

        var todo = language.BaseGlyphs.Concat(language.Glyphs)
            .Select(g => g.Name).Distinct()
            .Where(n => !composites.Contains(n) && !language.Derivations.ContainsKey(n) && NodeSources.Of(language, n).Count == 0).ToArray();
        var results = new (Glyph? Glyph, System.Runtime.ExceptionServices.ExceptionDispatchInfo? Error)[todo.Length];
        Parallel.For(0, todo.Length, new ParallelOptions { MaxDegreeOfParallelism = threads }, i =>
        {
            try
            {
                var def = defs[todo[i]];
                results[i] = (_glyphGenerator.GenerateGlyph(language, def, grid, SeedTree.ForGlyph(languageSeed, def.Name), generated, usageTracker, variation: 0, languageSeed: languageSeed), null);
            }
            catch (Exception e) { results[i] = (null, System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(e)); }
        });
        for (int i = 0; i < todo.Length; i++) _prefetched[todo[i]] = results[i];
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

            // Glyphs derived from it were cut from the old version: they are derived again from the new one, in order, each through the
            // same quality loop as any plain glyph.
            var changed = new HashSet<string> { def.Name };
            var chain = new List<string>();
            for (int i = -1; i < chain.Count; i++)
            {
                string parent = i < 0 ? def.Name : chain[i];
                foreach (var (derivedName, d) in language.Derivations)
                    if (d.From == parent && generated.ContainsKey(derivedName) && !chain.Contains(derivedName)) chain.Add(derivedName);
                // Glyphs whose shapes read this glyph's nodes (nodesOfGlyph) were drawn against the old one.
                foreach (var other in language.BaseGlyphs.Concat(language.Glyphs).Select(g => g.Name).Distinct())
                    if (other != def.Name && !language.Combination.Recipes.ContainsKey(other) && generated.ContainsKey(other) && !chain.Contains(other)
                        && NodeSources.Of(language, other).Contains(parent)) chain.Add(other);
            }
            foreach (var derivedName in chain)
            {
                accepted.Remove(generated[derivedName]);
                GenerateInto(language, grid, languageSeed, defs[derivedName], generated, accepted, acceptedComposites, usageTracker, defs, 1000 + _redraws[def.Name]);
                changed.Add(derivedName);
            }

            var dependents = language.Combination.Recipes
                .Where(r => r.Value.Components.Any(changed.Contains) && generated.ContainsKey(r.Key))
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

        // Attempt 0 uses the glyph's normal seed; later attempts derive a fresh, still
        // fully deterministic seed from it, so a quality retry is reproducible too.
        Glyph Draw(int attemptIndex)
        {
            if (attemptIndex == 0 && seedSalt == 0 && _prefetched.Remove(glyphDef.Name, out var drawn))
            {
                drawn.Error?.Throw();
                return drawn.Glyph!;
            }

            return DrawNow(attemptIndex);
        }

        Glyph DrawNow(int attemptIndex) => _glyphGenerator.GenerateGlyph(
            language, glyphDef, grid,
            attemptIndex == 0 ? baseSeed : SplitMix64.DeriveSeed(baseSeed, $"quality-retry-{attemptIndex}"),
            generated, usageTracker, variation: attemptIndex, languageSeed: languageSeed);

        // A plain/base glyph's attempts do not depend on each other (only on the finished glyphs, which do not change meanwhile),
        // so once the first attempt has failed, the next few are drawn at the same time. They are used in attempt order, so the
        // glyph kept is exactly the one a single thread would have kept. Anything that changes the finished glyphs (a redraw)
        // throws the prepared attempts away.
        int threads = MaxParallelism <= 0 ? Environment.ProcessorCount : MaxParallelism;
        bool speculate = threads > 1 && !isComposite;
        var prepared = new Dictionary<int, (Glyph? Glyph, GlyphGenerationException? Error)>();

        void Prepare(int from)
        {
            int to = Math.Min(maxAttempts, from + threads);
            var todo = Enumerable.Range(from, to - from).Where(i => !prepared.ContainsKey(i)).ToArray();
            var results = new (Glyph? Glyph, GlyphGenerationException? Error)[todo.Length];
            Parallel.For(0, todo.Length, new ParallelOptions { MaxDegreeOfParallelism = threads }, k =>
            {
                try { results[k] = (Draw(todo[k]), null); }
                catch (GlyphGenerationException e) { results[k] = (null, e); }
            });
            for (int k = 0; k < todo.Length; k++) prepared[todo[k]] = results[k];
        }


        // Glyphs that building this one regenerated, moved or rebuilt do not pass through this loop, so each is held to what the loop
        // applies to a glyph of its kind: not a single straight line, similarity (to the source, for a derived glyph), compositeMaxSimilarity
        // for a composite, every Reject rule, the Score penalty limit, and the pair rules. Null when it passes.
        string? WhyChangedGlyphBroken(string key, Glyph g)
        {
            bool composite = language.Combination.Recipes.ContainsKey(key);
            bool derived = !composite && language.Derivations.TryGetValue(key, out var dv) && generated.ContainsKey(dv.From);
            if (!composite)
            {
                if (quality.RejectStraightLineOnly && GlyphQualityChecker.IsTrivialStraightLine(g))
                    return $"'{key}' as a single straight line";
                string? source = derived ? language.Derivations[key].From : null;
                var others = generated.Where(kv => kv.Key != key && kv.Key != source && !language.Combination.Recipes.ContainsKey(kv.Key)).Select(kv => kv.Value);
                if (GlyphQualityChecker.FindTooSimilar(g, others, quality.MaxSimilarity, quality) is { } sim)
                    return $"'{key}' too similar to '{sim.Other.Name}' ({sim.Similarity:0.00})";
                if (source is not null && GlyphQualityChecker.FindTooSimilar(g, new[] { generated[source] }, language.Derivations[key].MaxSimilarityToSource, quality) is { } srcSim)
                    return $"'{key}' too similar to its source '{srcSim.Other.Name}' ({srcSim.Similarity:0.00})";
            }
            else if (quality.CompositeMaxSimilarity < 1.0)
            {
                var others = generated.Where(kv => kv.Key != key && language.Combination.Recipes.ContainsKey(kv.Key)).Select(kv => kv.Value);
                if (GlyphQualityChecker.FindTooSimilar(g, others, quality.CompositeMaxSimilarity, quality) is { } sim)
                    return $"'{key}' too similar to '{sim.Other.Name}' ({sim.Similarity:0.00})";
            }

            if (quality.Rules.Count > 0 || quality.PairRules.Count > 0)
            {
                var findings = quality.Rules.Count > 0 ? QualityRuleChecker.Evaluate(g, quality.Rules, composite, _coverage) : new List<QualityFinding>();
                findings.AddRange(QualityRuleChecker.EvaluatePairs(g, generated, quality.PairRules, _coverage));
                var reject = findings.FirstOrDefault(f => f.Rule.OnFail == QualityAction.Reject);
                if (reject is not null) return $"'{key}' so that rule '{reject.Rule.Id}' broke: {reject.Example}";
                double penalty = findings.Where(f => f.Rule.OnFail == QualityAction.Score).Sum(f => f.Penalty);
                if (penalty > quality.AcceptPenalty) return $"'{key}' with penalty {penalty:0.##} above acceptPenalty {quality.AcceptPenalty:0.##}";
            }

            return null;
        }

        for (int attempt = 0; attempt < maxAttempts; attempt++)
        {
            if (speculate && attempt > 0 && !prepared.ContainsKey(attempt)) Prepare(attempt);

            var before = new Dictionary<string, Glyph>(generated);
            Glyph candidate;
            try
            {
                if (prepared.Remove(attempt, out var ready))
                {
                    if (ready.Error is not null) throw ready.Error;
                    candidate = ready.Glyph!;
                }
                else candidate = Draw(attempt);
            }
            catch (GlyphGenerationException) when (best is not null)
            {
                break; // a later attempt could not be built: fall back on the best one so far
            }

            // Building this glyph may have regenerated a component and rebuilt finished composites around it. Those rebuilds
            // do not pass through this loop, so hold them to the Reject rules here; if one breaks, undo all of it.
            var changed = generated.Where(kv => before.TryGetValue(kv.Key, out var old) && !ReferenceEquals(old, kv.Value)).ToList();
            if (changed.Count > 0)
            {
                string? brokenWhy = null;
                foreach (var (key, glyph) in changed)
                {
                    brokenWhy = WhyChangedGlyphBroken(key, glyph);
                    if (brokenWhy is not null) break;
                }

                if (brokenWhy is not null)
                {
                    generated.Clear();
                    foreach (var (k, v) in before) generated[k] = v;
                    rejections.Add("it rebuilt " + brokenWhy);
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
                    candidate, derived ? pool.Where(g => g.Name != derivation!.From) : pool, maxSimilarity, quality);
                if (similar is null && derived)
                    similar = GlyphQualityChecker.FindTooSimilar(candidate, new[] { generated[derivation!.From] }, derivation!.MaxSimilarityToSource, quality);
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
                    prepared.Clear();
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
