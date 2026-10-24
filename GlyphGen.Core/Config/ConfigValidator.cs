using System.Text.RegularExpressions;

namespace GlyphGen.Core.Config;

/// <summary>
/// Value-level validation of a parsed config. Runs only after ConfigStructureChecker has passed,
/// so blocks are never null here (apart from the few nullable ones, which are handled).
/// Everything found is reported, one message per problem, as "[Language] path: message".
/// The point is to turn things that would otherwise crash mid-generation, loop forever, or be
/// silently clamped/ignored into clear config errors.
/// </summary>
internal static class ConfigValidator
{
    private static readonly int[] LatticeRotations = { 0, 90, 180, 270 };
    private static readonly Regex HexColor = new(@"^#?(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$");

    private sealed class Ctx
    {
        public Ctx(List<string> errors, string language) { Errors = errors; Language = language; }
        public List<string> Errors { get; }
        public string Language { get; }
        public void Add(string where, string message) => Errors.Add($"[{Language}] {where}: {message}");
    }

    public static void Validate(RootConfig config, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(config.OutputDirectory))
            errors.Add("outputDirectory must not be empty.");

        if (config.Languages.Count == 0)
            errors.Add("languages must contain at least one language.");

        if (config.MaxParallelism < 0)
            errors.Add($"maxParallelism must be 0 (all cores) or a thread count of 1 or more (got {config.MaxParallelism}).");

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < config.Languages.Count; i++)
        {
            var language = config.Languages[i];
            string label = string.IsNullOrWhiteSpace(language.Name) ? $"language #{i + 1}" : language.Name;

            if (string.IsNullOrWhiteSpace(language.Name))
                errors.Add($"[{label}] name must not be empty.");
            else if (!seen.Add(language.Name))
                errors.Add($"[{label}] name is used by more than one language (names also pick the output folder and the seeds).");

            ValidateLanguage(language, new Ctx(errors, label));
        }
    }

    private static void ValidateLanguage(LanguageDefinition l, Ctx c)
    {
        // Canvas
        if (l.Canvas.SizePx < 16)
            c.Add("canvas.sizePx", $"must be at least 16 (got {l.Canvas.SizePx}).");
        if (l.Canvas.MarginPx < 0)
            c.Add("canvas.marginPx", $"must be >= 0 (got {l.Canvas.MarginPx}).");
        else if (2L * l.Canvas.MarginPx >= l.Canvas.SizePx)
            c.Add("canvas.marginPx", $"({l.Canvas.MarginPx}) leaves no room to draw on a {l.Canvas.SizePx}px canvas.");

        // Grid
        if (l.Grid.Width < 1 || l.Grid.Height < 1)
            c.Add("grid", $"width and height must each be at least 1 (got {l.Grid.Width}x{l.Grid.Height}).");

        ValidateGlyphsAndRecipes(l, c);
        ValidateDerivations(l, c);

        ValidateShapes(l, c);
        ValidateShapeGlyphFilters(l, c);
        ValidateSpacingShapeFilters(l, c);
        ValidateNodeMasks(l, c);
        ValidateNodeSourceCycles(l, c);

        ValidateCurve(l, c);
        ValidateCombinationSettings(l.Combination, c);

        if (!double.IsFinite(l.Quality.MaxSimilarity) || l.Quality.MaxSimilarity <= 0)
            c.Add("quality.maxSimilarity", $"must be a finite number above 0 (1 or more disables the check; got {l.Quality.MaxSimilarity}).");
        if (!double.IsFinite(l.Quality.CompositeMaxSimilarity) || l.Quality.CompositeMaxSimilarity <= 0)
            c.Add("quality.compositeMaxSimilarity", $"must be a finite number above 0 (1 or more disables the check; got {l.Quality.CompositeMaxSimilarity}).");
        ValidateQualityRules(l, c);
        if (l.Quality.MaxRegenerationAttempts < 1)
            c.Add("quality.maxRegenerationAttempts", $"must be at least 1 (got {l.Quality.MaxRegenerationAttempts}).");

        ValidateRender(l.Render, c);

        foreach (var error in l.UsageSizeBias.Validate()) c.Add("usageSizeBias", error);
    }

    // ---- glyph lists and recipes -------------------------------------------------------------

    private static void ValidateGlyphsAndRecipes(LanguageDefinition l, Ctx c)
    {
        var known = new HashSet<string>(StringComparer.Ordinal);
        var baseNames = new HashSet<string>(StringComparer.Ordinal);
        var glyphIndex = new Dictionary<string, int>(StringComparer.Ordinal);

        void CheckList(string label, List<GlyphDefinition> list, bool isBase)
        {
            for (int i = 0; i < list.Count; i++)
            {
                string name = list[i].Name;
                if (string.IsNullOrWhiteSpace(name))
                {
                    c.Add(label, $"item {i + 1} has an empty glyph name.");
                    continue;
                }

                if (!known.Add(name))
                {
                    c.Add(label, $"glyph name '{name}' appears more than once across glyphs/baseGlyphs.");
                    continue;
                }

                if (isBase) baseNames.Add(name); else glyphIndex[name] = i;
            }
        }

        CheckList("baseGlyphs", l.BaseGlyphs, isBase: true);
        CheckList("glyphs", l.Glyphs, isBase: false);

        var recipes = l.Combination.Recipes;

        foreach (var (key, recipe) in recipes)
        {
            var components = recipe.Components;
            string where = $"combination.recipes['{key}']";

            int keyIndex = -1;
            if (baseNames.Contains(key))
                c.Add(where, "is a base glyph but also has a recipe - base glyphs are components, not composites.");
            else if (!glyphIndex.TryGetValue(key, out keyIndex))
                c.Add(where, "is not listed in glyphs, so this recipe would never be used.");

            if (components.Count == 0)
                c.Add(where, "has no components.");

            if (recipe.Arrangement is not null)
            {
                ValidateArrangement(c, where + ".arrangement", recipe.Arrangement, forShape: false);
                ValidateContact(c, where + ".arrangement", recipe.EffectiveArrangement ?? recipe.Arrangement);
                ValidatePlacements(c, l, where + ".arrangement", recipe.Arrangement.Placements, known, components);
            }

            foreach (var component in components)
            {
                if (string.IsNullOrWhiteSpace(component))
                {
                    c.Add(where, "has an empty component name.");
                    continue;
                }

                if (!known.Contains(component))
                {
                    c.Add(where, $"references '{component}', which is not listed in glyphs or baseGlyphs.");
                    continue;
                }

                // A composite built from another composite needs that one generated first:
                // generation walks the composites in the order they are listed in glyphs. This
                // also rules out cycles (a recipe using itself, or two recipes using each other).
                if (recipes.ContainsKey(component) && keyIndex >= 0
                    && (!glyphIndex.TryGetValue(component, out int componentIndex) || componentIndex >= keyIndex))
                {
                    c.Add(where, $"is built from the composite '{component}', which must be listed BEFORE '{key}' in glyphs (this also rules out circular recipes).");
                }
            }
        }

        ValidatePlacements(c, l, "combination.arrangement", l.Combination.Arrangement.Placements, known, null);
    }

    // ---- derivations ---------------------------------------------------------------------------

    private static readonly Dictionary<string, DerivationOp[]> StepOptions = new()
    {
        ["amount"] = new[] { DerivationOp.DeleteNodes, DerivationOp.DeleteEdges, DerivationOp.Keep, DerivationOp.Nudge, DerivationOp.SplitEdges },
        ["target"] = new[] { DerivationOp.DeleteNodes, DerivationOp.Nudge },
        ["mode"] = new[] { DerivationOp.DeleteNodes },
        ["connectionRules"] = new[] { DerivationOp.DeleteNodes },
        ["bridgeMaxMoves"] = new[] { DerivationOp.DeleteNodes },
        ["removeOrphanNodes"] = new[] { DerivationOp.DeleteNodes, DerivationOp.DeleteEdges, DerivationOp.Keep },
        ["keepAtLeast"] = new[] { DerivationOp.DeleteNodes, DerivationOp.DeleteEdges, DerivationOp.Keep },
        ["keepConnected"] = new[] { DerivationOp.DeleteNodes, DerivationOp.DeleteEdges, DerivationOp.Keep },
        ["shapes"] = new[] { DerivationOp.Add },
        ["optional"] = new[] { DerivationOp.Add },
        ["factors"] = new[] { DerivationOp.Scale },
        ["axis"] = new[] { DerivationOp.Mirror },
        ["degrees"] = new[] { DerivationOp.Rotate },
        ["distance"] = new[] { DerivationOp.Nudge },
        ["parts"] = new[] { DerivationOp.SplitEdges },
        ["distribution"] = new[] { DerivationOp.SplitEdges },
        ["snap"] = new[] { DerivationOp.SplitEdges },
        ["maxDeviation"] = new[] { DerivationOp.SplitEdges },
        ["minEdgeLength"] = new[] { DerivationOp.SplitEdges },
    };

    private static void ValidateDerivations(LanguageDefinition l, Ctx c)
    {
        var known = new HashSet<string>(l.BaseGlyphs.Concat(l.Glyphs).Select(g => g.Name), StringComparer.Ordinal);
        var shapeIds = new HashSet<string>(l.Shapes.Select(s => s.Id), StringComparer.Ordinal);

        foreach (var (name, d) in l.Derivations)
        {
            string where = $"derivations['{name}']";

            if (!known.Contains(name)) c.Add(where, "is not listed in glyphs or baseGlyphs, so it would never be generated.");
            if (l.Combination.Recipes.ContainsKey(name)) c.Add(where, "also has a combination recipe - a glyph is either derived or a composite.");

            if (string.IsNullOrWhiteSpace(d.From)) c.Add(where + ".from", "must name the glyph to derive from.");
            else if (!known.Contains(d.From)) c.Add(where + ".from", $"'{d.From}' is not listed in glyphs or baseGlyphs.");
            else if (d.From == name) c.Add(where + ".from", "a glyph cannot be derived from itself.");

            if (d.Steps.Count == 0) c.Add(where + ".steps", "needs at least one step.");
            Number(c, where + ".maxSimilarityToSource", d.MaxSimilarityToSource, 0, 1);

            for (int i = 0; i < d.Steps.Count; i++)
                ValidateStep(c, $"{where}.steps[{i}]", d.Steps[i], shapeIds, l.Shapes);
        }

        // Circles: a derivation's source may itself be derived or a composite, but not (through any chain of those) itself.
        string? DependsOn(string glyph, string target, HashSet<string> seen)
        {
            if (!seen.Add(glyph)) return null;
            var next = new List<string>();
            if (l.Derivations.TryGetValue(glyph, out var d)) next.Add(d.From);
            if (l.Combination.Recipes.TryGetValue(glyph, out var r)) next.AddRange(r.Components);
            foreach (var n in next)
                if (n == target || DependsOn(n, target, seen) is not null) return n;
            return null;
        }

        foreach (var name in l.Derivations.Keys)
            if (DependsOn(name, name, new HashSet<string>()) is not null)
                c.Add($"derivations['{name}']", "depends on itself through derivations and/or recipes (a circle).");
    }

    private static void ValidateStep(Ctx c, string where, DerivationStep step, HashSet<string> shapeIds, List<ShapeDefinition> allShapes)
    {
        Enum(c, where + ".op", step.Op);
        Number(c, where + ".chance", step.Chance, 0, 100);

        if (step.OnlyIf is { } gate)
        {
            foreach (var (label, value) in new[] { ("minEdges", gate.MinEdges), ("maxEdges", gate.MaxEdges), ("minNodes", gate.MinNodes), ("maxNodes", gate.MaxNodes) })
                if (value is < 0) c.Add($"{where}.onlyIf.{label}", $"must be at least 0 (got {value}).");
            if (gate.MinEdges > gate.MaxEdges) c.Add(where + ".onlyIf", $"maxEdges ({gate.MaxEdges}) is smaller than minEdges ({gate.MinEdges}).");
            if (gate.MinNodes > gate.MaxNodes) c.Add(where + ".onlyIf", $"maxNodes ({gate.MaxNodes}) is smaller than minNodes ({gate.MinNodes}).");
        }

        var present = new Dictionary<string, bool>
        {
            ["amount"] = step.Amount is not null, ["target"] = step.Target is not null, ["mode"] = step.Mode is not null,
            ["connectionRules"] = step.ConnectionRules is not null, ["bridgeMaxMoves"] = step.BridgeMaxMoves is not null,
            ["removeOrphanNodes"] = step.RemoveOrphanNodes is not null, ["keepAtLeast"] = step.KeepAtLeast is not null,
            ["keepConnected"] = step.KeepConnected is not null, ["shapes"] = step.Shapes is not null,
            ["optional"] = step.Optional is not null, ["factors"] = step.Factors is not null, ["axis"] = step.Axis is not null,
            ["degrees"] = step.Degrees is not null, ["distance"] = step.Distance is not null,
            ["parts"] = step.Parts is not null, ["distribution"] = step.Distribution is not null, ["snap"] = step.Snap is not null,
            ["maxDeviation"] = step.MaxDeviation is not null, ["minEdgeLength"] = step.MinEdgeLength is not null,
        };
        foreach (var (option, isSet) in present)
            if (isSet && Array.IndexOf(StepOptions[option], step.Op) < 0)
                c.Add(where + "." + option, $"does not apply to {step.Op} (it applies to {string.Join(", ", StepOptions[option])}).");

        if (StepOptions["amount"].Contains(step.Op) && !(step.Op == DerivationOp.SplitEdges && step.Amount is null))
        {
            var amount = step.Amount;
            if (amount is null || (amount.Percent is null) == (amount.Count is null))
                c.Add(where + ".amount", "needs exactly one of 'percent' and 'count'.");
            else if (amount.Percent is { } percent) Percent(c, where + ".amount.percent", percent);
            else IntRange(c, where + ".amount.count", amount.Count!, 0);
        }

        if (step.Target is { } target) Enum(c, where + ".target", target);
        if (step.Axis is { } axis) Enum(c, where + ".axis", axis);
        if (step.Mode is { } mode) Enum(c, where + ".mode", mode);
        if (step.KeepAtLeast is < 0) c.Add(where + ".keepAtLeast", $"must be at least 0 (got {step.KeepAtLeast}).");
        if (step.Distance is < 1) c.Add(where + ".distance", $"must be at least 1 (got {step.Distance}).");
        if (step.BridgeMaxMoves is < 1) c.Add(where + ".bridgeMaxMoves", $"must be at least 1 (got {step.BridgeMaxMoves}).");

        if (step.Op == DerivationOp.DeleteNodes)
        {
            bool bridge = step.Mode == DeleteNodeMode.Bridge;
            if (bridge && (step.ConnectionRules is null || step.ConnectionRules.Count == 0))
                c.Add(where + ".connectionRules", "Mode Bridge needs at least one rule to grow the replacement path from.");
            else if (!bridge && step.ConnectionRules is not null)
                c.Add(where + ".connectionRules", "only applies to Mode Bridge.");
            if (!bridge && step.BridgeMaxMoves is not null) c.Add(where + ".bridgeMaxMoves", "only applies to Mode Bridge.");
            if (bridge && step.ConnectionRules is { Count: > 0 })
                ValidateRules(c, where + ".connectionRules", step.ConnectionRules, required: true);
        }

        if (step.Op == DerivationOp.SplitEdges)
        {
            if (step.Parts is { } parts) IntRange(c, where + ".parts", parts, 2);
            if (step.Distribution is { } distribution) Enum(c, where + ".distribution", distribution);
            if (step.Snap is { } snap) Enum(c, where + ".snap", snap);
            if (step.MaxDeviation is not null && step.Snap != SplitSnap.Nearest) c.Add(where + ".maxDeviation", "only applies to snap Nearest.");
            if (step.MaxDeviation is < 0) c.Add(where + ".maxDeviation", $"must be at least 0 (got {step.MaxDeviation}).");
            if (step.MinEdgeLength is < 0) c.Add(where + ".minEdgeLength", $"must be at least 0 (got {step.MinEdgeLength}).");
        }

        if (step.Op == DerivationOp.Add)
        {
            if (step.Shapes is null || step.Shapes.Count == 0) c.Add(where + ".shapes", "needs at least one shape id.");
            else
                for (int k = 0; k < step.Shapes.Count; k++)
                {
                    string id = step.Shapes[k];
                    if (!shapeIds.Contains(id)) { c.Add(where + ".shapes", $"'{id}' is not the id of a shape."); continue; }
                    var copyOf = allShapes.First(x => x.Id == id).CopyOf;
                    if (copyOf is not null && !step.Shapes.Take(k).Contains(copyOf.Shape))
                        c.Add(where + ".shapes", $"'{id}' copies '{copyOf.Shape}', which must be listed before it in the same step.");
                }
        }

        if (step.Op == DerivationOp.Scale)
        {
            if (step.Factors is null || step.Factors.Count == 0) c.Add(where + ".factors", "needs at least one ratio.");
            else
            {
                double total = 0;
                for (int i = 0; i < step.Factors.Count; i++)
                {
                    var f = step.Factors[i];
                    if (f.Numerator <= 0) c.Add($"{where}.factors[{i}]", $"numerator must be positive (got {f.Numerator}).");
                    if (f.Denominator <= 0) c.Add($"{where}.factors[{i}]", $"denominator must be positive (got {f.Denominator}).");
                    if (Number(c, $"{where}.factors[{i}].weight", f.Weight, 0, double.MaxValue)) total += f.Weight;
                }
                if (total <= 0) c.Add(where + ".factors", "every ratio has weight 0, so none could ever be chosen.");
            }
        }

        if (step.Op == DerivationOp.Rotate && step.Degrees is not null)
        {
            if (step.Degrees.Count == 0) c.Add(where + ".degrees", "must not be empty (leave it out for 90, 180 and 270).");
            foreach (int d in step.Degrees)
                if (d is not (90 or 180 or 270)) c.Add(where + ".degrees", $"{d} is not supported; use 90, 180 or 270.");
        }
    }

    private static void ValidateCombinationSettings(CombinationConfig combination, Ctx c)
    {
        if (combination.MaxRetriesPerRecipe < 1)
            c.Add("combination.maxRetriesPerRecipe", $"must be at least 1 (got {combination.MaxRetriesPerRecipe}).");

        ValidateArrangement(c, "combination.arrangement", combination.Arrangement, forShape: false);
        ValidateContact(c, "combination.arrangement", combination.Arrangement);

        for (int i = 0; i < combination.AllowedScales.Count; i++)
        {
            var scale = combination.AllowedScales[i];
            string where = $"combination.allowedScales[{i}]";

            if (scale.Numerator <= 0) c.Add(where, $"numerator must be positive (got {scale.Numerator}).");
            if (scale.Denominator <= 0) c.Add(where, $"denominator must be positive (got {scale.Denominator}); 0 would divide by zero.");
            Number(c, where + ".weight", scale.Weight, 0, double.MaxValue);
        }

        foreach (var angle in combination.Rotation.Allowed)
        {
            if (Array.IndexOf(LatticeRotations, angle) < 0)
                c.Add("combination.rotation.allowed", $"{angle} is not supported; only 0, 90, 180 and 270 keep nodes on the grid.");
        }
        Number(c, "combination.rotation.weight", combination.Rotation.Weight, 0, double.MaxValue);
    }

    // ---- connection rules and motifs ---------------------------------------------------------

    private static void ValidateRules(Ctx c, string path, List<ConnectionRuleConfig> rules, bool required)
    {
        if (rules.Count == 0)
        {
            if (required) c.Add(path, "needs at least one rule - without one no chain can grow.");
            return;
        }

        double total = 0;
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            string where = $"{path}[{i}]";

            Enum(c, where + ".direction", rule.Direction);
            Enum(c, where + ".anglePreset", rule.AnglePreset);

            bool circle = rule.Direction == ConnectionDirection.Circle;
            bool arc = rule.Direction == ConnectionDirection.Arc;
            int lowest = circle ? 0 : 1; // an Arc needs a circle to draw on: at least 1
            if (rule.MinLength < lowest)
                c.Add(where, circle
                    ? $"minLength must be at least 0 (got {rule.MinLength}); 0 is a dot."
                    : $"minLength must be at least 1 (got {rule.MinLength}).");
            if (rule.MaxLength < rule.MinLength) c.Add(where, $"maxLength ({rule.MaxLength}) is smaller than minLength ({rule.MinLength}).");
            if (Number(c, where + ".weight", rule.Weight, 0, double.MaxValue)) total += rule.Weight;

            Number(c, where + ".minAngle", rule.MinAngle, 0, 180);
            Number(c, where + ".maxAngle", rule.MaxAngle, 0, 180);
            if (rule.MaxAngle < rule.MinAngle) c.Add(where, $"maxAngle ({rule.MaxAngle}) is smaller than minAngle ({rule.MinAngle}).");
            if (rule.AnglePreset != AnglePreset.None && (rule.MinAngle > 0 || rule.MaxAngle < 180))
                c.Add(where + ".anglePreset", "replaces minAngle/maxAngle - set one or the other, not both.");
            if (rule.Direction == ConnectionDirection.Angle && !rule.LimitsAngle)
                c.Add(where, "direction Angle needs an anglePreset or a minAngle/maxAngle narrower than 0..180 (otherwise use Any).");
            if ((circle || arc) && (rule.LimitsAngle))
                c.Add(where, $"angle limits do not apply to a {rule.Direction} rule.");

            if (arc)
            {
                Enum(c, where + ".arc.angles", rule.Arc.Angles);
                Enum(c, where + ".arc.anchor", rule.Arc.Anchor);
                Enum(c, where + ".arc.continueFrom", rule.Arc.ContinueFrom);
                if (rule.Arc.ContinueFrom == ArcContinueFrom.ArcEnd && rule.Arc.Anchor != ArcAnchor.Node)
                    c.Add(where + ".arc.continueFrom", "ArcEnd needs arc.anchor Node: only then does the arc touch the current node, so that the chain really goes on along it (Diameter already ends on the far node).");
                Number(c, where + ".arc.endTolerance", rule.Arc.EndTolerance, 0, 1);
                int step = AngleSets.Step(rule.Arc.Angles);
                if (rule.Arc.Anchor == ArcAnchor.Diameter) { }
                else if (rule.Arc.MinSweep < step || rule.Arc.MinSweep % step != 0)
                    c.Add(where + ".arc.minSweep", $"must be a multiple of {step} (the step of the {rule.Arc.Angles} angles), at least {step} (got {rule.Arc.MinSweep}).");
                if (rule.Arc.Anchor != ArcAnchor.Diameter && (rule.Arc.MaxSweep >= 360 || rule.Arc.MaxSweep % step != 0))
                    c.Add(where + ".arc.maxSweep", $"must be a multiple of {step} below 360 (got {rule.Arc.MaxSweep}); use a Circle rule for a full circle.");
                if (rule.Arc.Anchor != ArcAnchor.Diameter && rule.Arc.MaxSweep < rule.Arc.MinSweep)
                    c.Add(where + ".arc", $"maxSweep ({rule.Arc.MaxSweep}) is smaller than minSweep ({rule.Arc.MinSweep}).");
            }

            Enum(c, where + ".outlineNodes.mode", rule.OutlineNodes.Mode);
            Enum(c, where + ".outlineNodes.angles", rule.OutlineNodes.Angles);
            Enum(c, where + ".outlineNodes.snap", rule.OutlineNodes.Snap);
            Number(c, where + ".outlineNodes.tolerance", rule.OutlineNodes.Tolerance, 0, 0.5);
            if (rule.OutlineNodes.Mode != OutlineNodeMode.None && !(circle || arc))
                c.Add(where + ".outlineNodes", "only applies to a Circle or Arc rule.");
            Number(c, where + ".backToStartChance", rule.BackToStartChance, 0, 100);
            if (rule.ChainLengthCost < 0) c.Add(where + ".chainLengthCost", "must be 0 or more (got " + rule.ChainLengthCost + ").");
            ValidateEdgeCurve(c, where + ".curve", rule.Curve);
        }

        if (total <= 0) c.Add(path, "every rule has weight 0, so none could ever be chosen.");
    }

    /// <summary>onlyForGlyphs / excludeGlyphs on shapes: known plain glyphs, and every plain glyph keeps at least one shape.</summary>
    private static void ValidateQualityRules(LanguageDefinition l, Ctx c)
    {
        var q = l.Quality;
        if (!double.IsFinite(q.AcceptPenalty) || q.AcceptPenalty < 0)
            c.Add("quality.acceptPenalty", $"must be a finite number >= 0 (got {q.AcceptPenalty}).");

        var shapeIds = new HashSet<string>(l.Shapes.Select(s => s.Id), StringComparer.Ordinal);
        var glyphNames = new HashSet<string>(l.BaseGlyphs.Concat(l.Glyphs).Select(g => g.Name), StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void CheckRules(List<QualityRule> list, string label, bool pair)
        {
        for (int i = 0; i < list.Count; i++)
        {
            var r = list[i];
            string where = $"{label}[{i}]" + (string.IsNullOrWhiteSpace(r.Id) ? "" : $" ('{r.Id}')");

            if (string.IsNullOrWhiteSpace(r.Id)) c.Add(where + ".id", "is required.");
            else if (!seen.Add(r.Id)) c.Add(where + ".id", $"'{r.Id}' is used by another rule.");

            Enum(c, where + ".appliesTo", r.AppliesTo);
            Enum(c, where + ".among", r.Among);
            Enum(c, where + ".measure", r.Measure);
            Enum(c, where + ".similarity", r.Similarity);
            Enum(c, where + ".onFail", r.OnFail);
            Enum(c, where + ".redo", r.Redo);
            if (!pair && r.Redo != QualityRedo.Composite && r.AppliesTo == QualityAppliesTo.Plain)
                c.Add(where + ".redo", "redraws a component of a composite, but appliesTo is Plain.");
            if (r.Redo != QualityRedo.Composite && r.OnFail != QualityAction.Reject)
                c.Add(where + ".redo", "only applies to onFail Reject (Score keeps its best attempt, which a redrawn component would invalidate).");
            Enum(c, where + ".subject.kind", r.Subject.Kind);
            Enum(c, where + ".against.kind", r.Against.Kind);
            Enum(c, where + ".window.shape", r.Window.Shape);

            foreach (var (field, selector) in new[] { ("subject", r.Subject), ("against", r.Against) })
            {
                if (pair)
                {
                    if (selector.Glyphs is not { Count: > 0 }) c.Add($"{where}.{field}.glyphs", "is required: the glyph(s) whose strokes this selector picks (\"*\" = any glyph).");
                    foreach (var g in selector.Glyphs ?? new List<string>())
                        if (g != "*" && !glyphNames.Contains(g)) c.Add($"{where}.{field}.glyphs", $"'{g}' is not listed in glyphs or baseGlyphs.");
                }
                else if (selector.Glyphs is { Count: > 0 })
                    c.Add($"{where}.{field}.glyphs", "only applies in quality.pairRules (quality.rules look inside one glyph).");
            }

            foreach (var (field, selector) in new[] { ("subject", r.Subject), ("against", r.Against) })
                foreach (var id in selector.Shapes ?? new List<string>())
                    if (!shapeIds.Contains(id)) c.Add($"{where}.{field}.shapes", $"'{id}' is not the id of a shape.");

            if (pair && (r.OnlyForGlyphs is { Count: > 0 } || r.ExcludeGlyphs is { Count: > 0 } || r.AppliesTo != QualityAppliesTo.Both || r.Among != QualityAmong.Auto))
                c.Add(where, "appliesTo, among, onlyForGlyphs and excludeGlyphs belong to quality.rules; a pair rule names its glyphs in subject.glyphs and against.glyphs.");
            foreach (var (field, names) in new[] { ("onlyForGlyphs", r.OnlyForGlyphs), ("excludeGlyphs", r.ExcludeGlyphs) })
                foreach (var name in names ?? new List<string>())
                    if (!glyphNames.Contains(name)) c.Add($"{where}.{field}", $"'{name}' is not listed in glyphs or baseGlyphs.");

            if (!double.IsFinite(r.Window.Radius) || r.Window.Radius < 0)
                c.Add(where + ".window.radius", $"must be a finite number >= 0 (got {r.Window.Radius}).");
            if (r.Max < 0) c.Add(where + ".max", $"must be >= 0 (got {r.Max}).");
            if (!double.IsFinite(r.MinDistance) || r.MinDistance < 0) c.Add(where + ".minDistance", $"must be a finite number >= 0 (got {r.MinDistance}).");
            if (r.Measure == QualityMeasure.Distance && r.MinDistance <= 0) c.Add(where + ".minDistance", "is required (above 0) for measure Distance.");
            if (r.Measure != QualityMeasure.Distance && r.MinDistance > 0) c.Add(where + ".minDistance", "only applies to measure Distance.");
            if (r.Measure == QualityMeasure.Distance && r.Max > 0) c.Add(where + ".max", "does not apply to measure Distance.");
            if (r.Measure != QualityMeasure.Similar && (r.Similarity != SimilarityMode.Visual || r.SimilarityThreshold != 0.7))
                c.Add(where + ".similarity", "similarity and similarityThreshold only apply to measure Similar.");
            if (!double.IsFinite(r.SimilarityThreshold) || r.SimilarityThreshold <= 0 || r.SimilarityThreshold > 1)
                c.Add(where + ".similarityThreshold", $"must be above 0 and at most 1 (got {r.SimilarityThreshold}).");
            if (!double.IsFinite(r.StrokeWidth) || r.StrokeWidth <= 0) c.Add(where + ".strokeWidth", $"must be a finite number above 0 (got {r.StrokeWidth}).");
            if (!double.IsFinite(r.Weight) || r.Weight < 0) c.Add(where + ".weight", $"must be a finite number >= 0 (got {r.Weight}).");
            if (r.OnFail != QualityAction.Score && r.Weight != 1) c.Add(where + ".weight", "only applies to onFail Score.");

            if (!pair && r.Among == QualityAmong.OtherComponents && r.AppliesTo == QualityAppliesTo.Plain)
                c.Add(where + ".among", "OtherComponents needs composite glyphs, but appliesTo is Plain.");

            if (r.WhenCountsDiffer is { } differ)
            {
                if (!shapeIds.Contains(differ.Shape)) c.Add(where + ".whenCountsDiffer.shape", $"'{differ.Shape}' is not the id of a shape.");
                Enum(c, where + ".whenCountsDiffer.action", differ.Action);
                if (!double.IsFinite(differ.Factor) || differ.Factor < 0 || differ.Factor > 1)
                    c.Add(where + ".whenCountsDiffer.factor", $"must be between 0 and 1 (got {differ.Factor}).");
                if (!pair && (r.AppliesTo == QualityAppliesTo.Plain || r.Among == QualityAmong.OtherChains))
                    c.Add(where + ".whenCountsDiffer", "compares the components of a composite, so it needs appliesTo Both or Composite and among Auto or OtherComponents.");
            }
        }
        }

        CheckRules(q.Rules, "quality.rules", pair: false);
        CheckRules(q.PairRules, "quality.pairRules", pair: true);
    }

    /// <summary>spacing.onlyShapes / excludeShapes name shapes that exist.</summary>
    private static void ValidateSpacingShapeFilters(LanguageDefinition l, Ctx c)
    {
        var ids = new HashSet<string>(l.Shapes.Select(s => s.Id), StringComparer.Ordinal);
        for (int i = 0; i < l.Shapes.Count; i++)
        {
            var spacing = l.Shapes[i].Arrangement.Spacing;
            foreach (var (field, names) in new[] { ("onlyShapes", spacing.OnlyShapes), ("excludeShapes", spacing.ExcludeShapes) })
                foreach (var name in names ?? new List<string>())
                    if (!ids.Contains(name)) c.Add($"shapes[{i}] ('{l.Shapes[i].Id}').arrangement.spacing.{field}", $"'{name}' is not the id of a shape.");
        }
    }

    /// <summary>placement.nodes: every region is exactly one kind and well formed; shape references name shapes placed earlier.</summary>
    private static void ValidateNodeMasks(LanguageDefinition l, Ctx c)
    {
        var order = l.Shapes.Select((s, i) => (s.Id, i)).GroupBy(x => x.Id).ToDictionary(g => g.Key, g => g.First().i);
        for (int i = 0; i < l.Shapes.Count; i++)
        {
            var shape = l.Shapes[i];
            var mask = shape.Placement.Nodes;
            if (mask is null) continue;
            string where = $"shapes[{i}] ('{shape.Id}').placement.nodes";
            if (shape.CopyOf is not null) c.Add(where, "does not apply to a copyOf shape (a copy follows its original).");

            void Refs(string field, IEnumerable<string>? ids)
            {
                foreach (var id in ids ?? Enumerable.Empty<string>())
                {
                    if (!order.TryGetValue(id, out var at)) c.Add($"{where}.{field}", $"'{id}' is not the id of a shape.");
                    else if (at > i) c.Add($"{where}.{field}", $"'{id}' comes after this shape in shapes, so none of its nodes exist when this shape is placed - list it earlier.");
                }
            }

            ValidateMaskRegions(l, c, where, mask, Refs, null);
        }
    }

    /// <summary>The regions of one node mask. <paramref name="components"/> is the set of glyph names nodesOfComponents may name
    /// (null = nodesOfComponents is not allowed here: a shape's mask).</summary>
    private static void ValidateMaskRegions(LanguageDefinition l, Ctx c, string where, NodeMaskConfig mask,
        Action<string, IEnumerable<string>?> Refs, ISet<string>? components, bool forComponent = false)
    {
            foreach (var (list, label) in new[] { (mask.Allow, "allow"), (mask.Forbid, "forbid") })
                for (int r = 0; r < (list?.Count ?? 0); r++)
                {
                    var region = list![r];
                    string at = $"{where}.{label}[{r}]";
                    if (region.KindCount != 1) { c.Add(at, forComponent ? "must set exactly one of points, box, pattern, nodesOfComponents." : "must set exactly one of points, box, pattern, nodesOfShapes, boundsOfShapes, nodesOfGlyph."); continue; }
                    if (forComponent && (region.NodesOfShapes is not null || region.BoundsOfShapes is not null))
                    { c.Add(at, "nodesOfShapes and boundsOfShapes belong to a shape's placement.nodes; for a component use nodesOfComponents (with shapes to pick shapes inside them)."); continue; }
                    if (forComponent && region.NodesOfGlyph is not null)
                    { c.Add(at + ".nodesOfGlyph", "only applies to a shape's placement.nodes, not to a component's placement rule."); continue; }
                    if (region.NodesOfGlyph is { } og)
                    {
                        var names = l.BaseGlyphs.Concat(l.Glyphs).Select(g => g.Name).ToHashSet(StringComparer.Ordinal);
                        if (string.IsNullOrWhiteSpace(og.Glyph)) c.Add(at + ".nodesOfGlyph.glyph", "must not be empty.");
                        else if (og.Glyph != GlyphNodes.AllGlyphs && !names.Contains(og.Glyph)) c.Add(at + ".nodesOfGlyph.glyph", $"'{og.Glyph}' is not listed in glyphs or baseGlyphs.");
                    }
                    if (!forComponent && region.NodesOfComponents is not null)
                    { c.Add(at + ".nodesOfComponents", "only applies to a component's placement rule (combination arrangement placements), not to a shape."); continue; }
                    if (region.NodesOfComponents is { } cn)
                    {
                        foreach (var name in cn.Components ?? new())
                            if (components is not null && !components.Contains(name)) c.Add(at + ".nodesOfComponents.components", $"'{name}' is not a component this rule can see (it must be a component of the recipe, placed before this one).");
                        if (cn.Components is { Count: 0 }) c.Add(at + ".nodesOfComponents.components", "is empty (leave it out for every component).");
                        if (cn.Shapes is { } sh)
                        {
                            if (sh.Count == 0) c.Add(at + ".nodesOfComponents.shapes", "is empty (leave it out for every shape).");
                            Refs($"{label}[{r}].nodesOfComponents.shapes", sh);
                        }
                    }
                    if (region.Points is { Count: 0 }) c.Add(at + ".points", "is empty.");
                    if (label == "allow")
                        foreach (var pt in region.Points ?? new())
                            if (pt.X < 0 || pt.Y < 0 || pt.X >= l.Grid.Width || pt.Y >= l.Grid.Height)
                                c.Add(at + ".points", $"[{pt.X}, {pt.Y}] is outside the grid (nodes run from 0 to {l.Grid.Width - 1} in x and 0 to {l.Grid.Height - 1} in y), so it can never be used - did you mean a larger grid (grid.width / grid.height count nodes, not cells)?");
                    if (region.Box is { } b && (b.MinX > b.MaxX || b.MinY > b.MaxY)) c.Add(at + ".box", "minX/minY must not exceed maxX/maxY.");
                    if (region.Pattern is { } pat)
                    {
                        if (pat.X is null && pat.Y is null) c.Add(at + ".pattern", "needs x and/or y.");
                        foreach (var (axisName, axis) in new[] { ("x", pat.X), ("y", pat.Y) })
                        {
                            if (axis is null) continue;
                            Enum(c, $"{at}.pattern.{axisName}.mode", axis.Mode);
                            if (axis.Step < 1) c.Add($"{at}.pattern.{axisName}.step", $"must be at least 1 (got {axis.Step}).");
                            if (axis.Mode != AxisPatternMode.Every && (axis.Step != 1 || axis.Offset != 0)) c.Add($"{at}.pattern.{axisName}", "step and offset only apply to mode Every.");
                        }
                    }
                    if (region.NodesOfShapes is { } pool) { if (pool.Count == 0) c.Add(at + ".nodesOfShapes", "is empty."); Refs($"{label}[{r}].nodesOfShapes", pool); }
                    if (region.BoundsOfShapes is { } around)
                    {
                        if (around.Shapes.Count == 0) c.Add(at + ".boundsOfShapes.shapes", "is empty.");
                        if (around.Margin < 0) c.Add(at + ".boundsOfShapes.margin", $"must be >= 0 (got {around.Margin}).");
                        Refs($"{label}[{r}].boundsOfShapes.shapes", around.Shapes);
                    }
                }
    }

    private static void ValidateNodeSourceCycles(LanguageDefinition l, Ctx c)
    {
        var names = l.BaseGlyphs.Concat(l.Glyphs).Select(g => g.Name).Distinct().ToList();
        var state = new Dictionary<string, int>(); // 1 = on the path, 2 = done
        bool reported = false;
        void Visit(string name, List<string> path)
        {
            if (reported) return;
            if (state.GetValueOrDefault(name) == 2) return;
            if (state.GetValueOrDefault(name) == 1)
            {
                c.Add("shapes", $"nodesOfGlyph makes glyphs depend on each other in a circle ({string.Join(" -> ", path.SkipWhile(x => x != name).Append(name))}); each glyph must be drawn after the ones it reads.");
                reported = true;
                return;
            }
            state[name] = 1;
            path.Add(name);
            var deps = NodeSources.Of(l, name).ToList();
            if (l.Derivations.TryGetValue(name, out var d)) deps.Add(d.From);
            if (l.Combination.Recipes.TryGetValue(name, out var r)) deps.AddRange(r.Components);
            foreach (var dep in deps.Distinct()) if (names.Contains(dep)) Visit(dep, path);
            path.RemoveAt(path.Count - 1);
            state[name] = 2;
        }
        foreach (var n in names) Visit(n, new List<string>());
    }

    private static void ValidateShapeGlyphFilters(LanguageDefinition l, Ctx c)
    {
        var composites = new HashSet<string>(l.Combination.Recipes.Keys, StringComparer.Ordinal);
        var plain = l.BaseGlyphs.Concat(l.Glyphs).Select(g => g.Name)
            .Where(n => !composites.Contains(n) && !l.Derivations.ContainsKey(n)).ToList();
        var known = new HashSet<string>(l.BaseGlyphs.Concat(l.Glyphs).Select(g => g.Name), StringComparer.Ordinal);

        bool anyFilter = false;
        for (int i = 0; i < l.Shapes.Count; i++)
        {
            var shape = l.Shapes[i];
            string where = $"shapes[{i}] ('{shape.Id}')";
            foreach (var (field, names) in new[] { ("onlyForGlyphs", shape.OnlyForGlyphs), ("excludeGlyphs", shape.ExcludeGlyphs) })
            {
                foreach (var name in names ?? new List<string>())
                {
                    if (!known.Contains(name)) c.Add(where + "." + field, $"'{name}' is not listed in glyphs or baseGlyphs.");
                    else if (composites.Contains(name)) c.Add(where + "." + field, $"'{name}' is a composite built from a recipe, so no shape applies to it - remove it.");
                    else if (l.Derivations.ContainsKey(name)) c.Add(where + "." + field, $"'{name}' is derived from another glyph, so no shape applies to it (a derivation's Add step picks its own shapes) - remove it.");
                }
            }
            if (shape.HasGlyphFilter) anyFilter = true;
        }

        if (!anyFilter) return;

        // A copy is drawn only when everything it copies from is drawn too.
        bool Drawn(ShapeDefinition shape, string glyph)
        {
            for (int guard = 0; guard <= l.Shapes.Count; guard++)
            {
                if (!shape.AppliesTo(glyph)) return false;
                if (shape.CopyOf is not { } up) return true;
                var origin = l.Shapes.FirstOrDefault(x => x.Id == up.Shape);
                if (origin is null) return true; // reported elsewhere
                shape = origin;
            }
            return true;
        }

        for (int i = 0; i < l.Shapes.Count; i++)
        {
            var shape = l.Shapes[i];
            if (shape.CopyOf is not null && shape.OnlyForGlyphs is { } only)
            {
                var origin = l.Shapes.FirstOrDefault(x => x.Id == shape.CopyOf.Shape);
                var lost = only.Where(g => origin is not null && !origin.AppliesTo(g)).ToList();
                if (lost.Count > 0)
                    c.Add($"shapes[{i}] ('{shape.Id}').onlyForGlyphs", $"lists {string.Join(", ", lost.Select(x => $"'{x}'"))}, where the shape it copies ('{origin!.Id}') is not drawn, so the copy never would be.");
            }
        }

        if (l.Shapes.Count == 0) return;
        foreach (var glyph in plain)
            if (!l.Shapes.Any(s => Drawn(s, glyph)))
                c.Add($"glyph '{glyph}'", "no shape is drawn for it: shapes' onlyForGlyphs/excludeGlyphs leave it with nothing to be built from.");
    }

    private static void ValidateShapes(LanguageDefinition l, Ctx c)
    {
        var composites = new HashSet<string>(l.Combination.Recipes.Keys, StringComparer.Ordinal);
        bool needsShapes = l.BaseGlyphs.Any(g => !l.Derivations.ContainsKey(g.Name))
            || l.Glyphs.Any(g => !composites.Contains(g.Name) && !l.Derivations.ContainsKey(g.Name));
        if (l.Shapes.Count == 0 && needsShapes)
            c.Add("shapes", "needs at least one shape - without one every plain glyph would be empty.");

        var ids = new HashSet<string>(StringComparer.Ordinal);
        var labels = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < l.Shapes.Count; i++)
        {
            var shape = l.Shapes[i];
            string where = string.IsNullOrWhiteSpace(shape.Id) ? $"shapes[{i}]" : $"shapes['{shape.Id}']";

            if (string.IsNullOrWhiteSpace(shape.Id))
                c.Add(where, "id must not be empty.");
            else if (!ids.Add(shape.Id))
                c.Add(where, "id is used by more than one shape (the id also seeds the shape, so two would come out identical).");

            if (shape.SeedLabel is not null && !labels.Add(shape.SeedLabel))
                c.Add(where + ".seedLabel", $"'{shape.SeedLabel}' is used by more than one shape.");

            Number(c, where + ".chance", shape.Chance, 0, 100);

            if (shape.CopyOf is not null)
            {
                ValidateCopy(l, c, where, shape, i);
                ValidateArrangement(c, where + ".arrangement", shape.Arrangement, forShape: true);
                continue;
            }

            Enum(c, where + ".kind", shape.Kind);

            if (shape.ChainFrom is { } from)
            {
                var source = l.Shapes.FirstOrDefault(x => x.Id == from);
                if (shape.Kind != ShapeKind.Literal && source is null)
                    c.Add(where + ".chainFrom", $"'{from}' is not a shape id.");
                else if (shape.Kind == ShapeKind.Literal)
                    c.Add(where + ".chainFrom", "only applies to a Generative shape (a Literal shape has no chain).");
                else if (source == shape)
                    c.Add(where + ".chainFrom", "a shape cannot take its chain from itself.");
                else
                {
                    // Follow the links: every one must exist, be generative and not lead back here.
                    var seen = new HashSet<string> { shape.Id };
                    var at = source!;
                    while (true)
                    {
                        if (at.CopyOf is not null || at.Kind == ShapeKind.Literal) { c.Add(where + ".chainFrom", $"'{at.Id}' has no chain of its own to take (it is a copy or a Literal shape)."); break; }
                        if (at.ChainFrom is not { } next) break;
                        if (!seen.Add(at.Id)) { c.Add(where + ".chainFrom", "the chainFrom links lead back round in a loop."); break; }
                        var following = l.Shapes.FirstOrDefault(x => x.Id == next);
                        if (following is null) { c.Add(where + ".chainFrom", $"'{at.Id}' takes its chain from '{next}', which is not a shape id."); break; }
                        if (following == shape) { c.Add(where + ".chainFrom", "the chainFrom links lead back round in a loop."); break; }
                        at = following;
                    }
                }
            }

            if (shape.Count is not null)
                IntRange(c, where + ".count", shape.Count, 0);

            switch (shape.Kind)
            {
                case ShapeKind.Literal:
                    if (shape.Nodes is null)
                        c.Add(where, "kind is Literal but there is no 'nodes' list.");
                    else
                        ValidateLiteral(c, where, shape.Nodes);
                    break;

                case ShapeKind.Generative:
                    if (shape.Nodes is not null)
                        c.Add(where, "kind is Generative but 'nodes' is set - nodes only apply to Literal shapes.");

                    string chain = where + ".chain";
                    IntRange(c, chain + ".length", shape.Chain.Length, 2, "a stroke needs at least 2 nodes");
                    ValidateRules(c, chain + ".connectionRules", shape.Chain.ConnectionRules, required: shape.Count is null || shape.Count.Max > 0);
                    Enum(c, chain + ".connectionWeighting", shape.Chain.ConnectionWeighting);
                    for (int r = 0; r < shape.Chain.SelfSpacing.Count; r++)
                    {
                        var rule = shape.Chain.SelfSpacing[r];
                        if (rule is null) { c.Add(chain + $".selfSpacing[{r}]", "must not be null."); continue; }
                        foreach (var error in rule.Validate()) c.Add(chain + $".selfSpacing[{r}]", error);
                    }
                    break;
            }

            ValidatePlacement(l, c, where + ".placement", shape.Placement, rotationApplies: shape.Kind == ShapeKind.Literal, shape.Arrangement, shapeIndex: i);
            ValidateArrangement(c, where + ".arrangement", shape.Arrangement, forShape: true);
            foreach (var id in shape.Arrangement.Crossing.NodesOfShapes ?? new List<string>())
                if (l.Shapes.All(x => x.Id != id))
                    c.Add(where + ".arrangement.crossing.nodesOfShapes", $"'{id}' is not the id of any shape.");
        }
    }

    private static void ValidateCopy(LanguageDefinition l, Ctx c, string where, ShapeDefinition shape, int index)
    {
        var copy = shape.CopyOf!;
        string path = where + ".copyOf";

        if (shape.Count is not null) c.Add(where + ".count", "copyOf makes one copy per instance of the original, so a count has no effect - remove it.");
        if (shape.Nodes is not null) c.Add(where + ".nodes", "a copy has no nodes of its own - remove them.");
        if (shape.Chain.ConnectionRules.Count > 0) c.Add(where + ".chain", "a copy is not grown by chain rules - remove them.");

        var options = GlyphGenJsonOptions.Default;
        if (System.Text.Json.JsonSerializer.Serialize(shape.Placement, options) != System.Text.Json.JsonSerializer.Serialize(new PlacementConfig(), options))
            c.Add(where + ".placement", "a copy is positioned by its flip, anchor, gap and shift - remove the placement.");

        if (string.IsNullOrWhiteSpace(copy.Shape))
            c.Add(path + ".shape", "must name the shape to copy.");
        else if (copy.Shape == shape.Id)
            c.Add(path + ".shape", "a shape cannot copy itself.");
        else
        {
            int origin = l.Shapes.FindIndex(s => s.Id == copy.Shape);
            if (origin < 0) c.Add(path + ".shape", $"'{copy.Shape}' is not the id of a shape.");
            else if (origin > index) c.Add(path + ".shape", $"'{copy.Shape}' is listed after this shape - the original must come first.");
        }

        if (copy.SelfSpacingIgnoresOriginal && !copy.RespectSelfSpacing)
            c.Add(path + ".selfSpacingIgnoresOriginal", "only applies together with respectSelfSpacing: true.");

        if (copy.RespectSelfSpacing && !string.IsNullOrWhiteSpace(copy.Shape))
        {
            var root = l.Shapes.FirstOrDefault(s => s.Id == copy.Shape);
            for (int guard = 0; guard < l.Shapes.Count && root?.CopyOf is { } up; guard++)
                root = l.Shapes.FirstOrDefault(s => s.Id == up.Shape);
            if (root is not null && !root.Chain.SelfSpacing.Any(r => r is { MinDistance: > 0 } or { MaxDistance: > 0 }))
                c.Add(path + ".respectSelfSpacing", $"the original ('{root.Id}') has no chain.selfSpacing rule with a minDistance or maxDistance, so there is nothing to apply - add one there or turn this off.");
        }

        Enum(c, path + ".flip", copy.Flip);
        Enum(c, path + ".auto.by", copy.Auto.By);
        Enum(c, path + ".auto.toward", copy.Auto.Toward);
        Enum(c, path + ".auto.axis", copy.Auto.Axis);
        if (copy.Auto.Axis != CopyAxis.Perpendicular && (copy.Flip != CopyFlip.Auto || copy.Auto.By != CopyAutoBy.LongestStraight))
            c.Add(path + ".auto.axis", "only applies to flip Auto with by LongestStraight.");
        Enum(c, path + ".anchor", copy.Anchor);

        if (copy.Auto.Shapes is not null)
        {
            if (copy.Flip != CopyFlip.Auto) c.Add(path + ".auto.shapes", "only applies when flip is Auto.");
            foreach (var id in copy.Auto.Shapes)
            {
                int at = l.Shapes.FindIndex(s => s.Id == id);
                if (at < 0) c.Add(path + ".auto.shapes", $"'{id}' is not the id of a shape.");
                else if (at >= index) c.Add(path + ".auto.shapes", $"'{id}' is not listed before this shape, so there is nothing of it to look at yet.");
            }
        }

        if (copy.Flips is not null)
        {
            if (copy.Flip != CopyFlip.Random) c.Add(path + ".flips", "only applies when flip is Random.");
            if (copy.Flips.Count == 0) c.Add(path + ".flips", "needs at least one flip (or leave it out for Horizontal, Vertical and Rotate180).");
            foreach (var f in copy.Flips)
            {
                Enum(c, path + ".flips", f);
                if (f is CopyFlip.Random or CopyFlip.Auto) c.Add(path + ".flips", $"{f} cannot be one of the flips to choose from.");
            }
        }

        if (copy.RegenerateBase < 0) c.Add(path + ".regenerateBase", $"must be 0 or more (got {copy.RegenerateBase}).");
        if (copy.RedrawGlyph < 0) c.Add(path + ".redrawGlyph", $"must be 0 or more (got {copy.RedrawGlyph}).");

        if (copy.Gap.Max < copy.Gap.Min) c.Add(path + ".gap", $"max ({copy.Gap.Max}) is smaller than min ({copy.Gap.Min}).");
        IntRange(c, path + ".shift", copy.Shift, 0, "the sign is drawn at random");
    }

    /// <summary>Placement rules: the glyph must exist (and, in a recipe's own arrangement, be one of its
    /// components); a Prefer rule needs one or two non-cancelling sides; a Fixed rule takes an offset only.</summary>
    private static void ValidatePlacements(
        Ctx c, LanguageDefinition l, string path, List<ComponentPlacementRule> rules, ISet<string> known, IReadOnlyList<string>? recipeComponents)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < rules.Count; i++)
        {
            var rule = rules[i];
            string where = $"{path}.placements[{i}]";
            if (rule is null) { c.Add(where, "must not be null."); continue; }

            if (rule.Glyph is null || rule.Glyph.Names.Count == 0) c.Add(where + ".glyph", "must not be empty.");
            else foreach (var glyphName in rule.Glyph.Names)
            {
                if (string.IsNullOrWhiteSpace(glyphName)) c.Add(where + ".glyph", "must not be empty.");
                else if (glyphName == ComponentPlacementRule.AllGlyphs) { if (!seen.Add(glyphName)) c.Add(where + ".glyph", "has more than one \"*\" rule."); }
                else if (!known.Contains(glyphName)) c.Add(where + ".glyph", $"'{glyphName}' is not listed in glyphs or baseGlyphs.");
                else if (recipeComponents is not null && !recipeComponents.Contains(glyphName))
                    c.Add(where + ".glyph", $"'{glyphName}' is not a component of this recipe, so the rule would never apply.");
                else if (!seen.Add(glyphName)) c.Add(where + ".glyph", $"'{glyphName}' has more than one rule.");
            }

            Enum(c, where + ".mode", rule.Mode);
            Enum(c, where + ".anchor", rule.Anchor);

            if (rule.Nodes is { } nodeMask)
            {
                if (rule.Mode == ComponentPlacementMode.Fixed) c.Add(where + ".nodes", "does not apply to a Fixed placement (its position is given by offset).");
                else
                {
                    var names = new HashSet<string>(recipeComponents ?? (IEnumerable<string>)known, StringComparer.Ordinal);
                    var shapeIds = new HashSet<string>(l.Shapes.Select(x => x.Id), StringComparer.Ordinal);
                    ValidateMaskRegions(l, c, where + ".nodes", nodeMask,
                        (field, ids) => { foreach (var id in ids ?? Enumerable.Empty<string>()) if (!shapeIds.Contains(id)) c.Add($"{where}.nodes.{field}", $"'{id}' is not the id of a shape."); },
                        names, forComponent: true);
                }
            }
            else if (rule.Anchor != ComponentNodeAnchor.FirstThenAny) c.Add(where + ".anchor", "only applies together with nodes.");

            switch (rule.Mode)
            {
                case ComponentPlacementMode.Prefer:
                    if (rule.Offset is not null) c.Add(where + ".offset", "only applies to Fixed placements.");
                    if (rule.IfBlocked != FixedBlockedAction.Regenerate) c.Add(where + ".ifBlocked", "only applies to Fixed placements.");
                    if (rule.Pick is not null) c.Add(where + ".pick", "only applies to SetRandom placements.");
                    if (rule.Scope is not null) c.Add(where + ".scope", "only applies to SetRandom placements.");
                    if (rule.Sides.Count == 0) c.Add(where + ".sides", "a Prefer rule needs at least one side (Left, Right, Top, Bottom or Center).");
                    ValidateSides(c, where + ".sides", rule.Sides);
                    Number(c, where + ".weight", rule.Weight, 0, double.MaxValue);
                    break;

                case ComponentPlacementMode.SetRandom:
                    if (rule.Offset is not null) c.Add(where + ".offset", "only applies to Fixed placements.");
                    if (rule.IfBlocked != FixedBlockedAction.Regenerate) c.Add(where + ".ifBlocked", "only applies to Fixed placements.");
                    if (rule.Sides.Count > 0) c.Add(where + ".sides", "only applies to Prefer placements; SetRandom draws its own (see pick).");
                    if (rule.Pick is { } pick) Enum(c, where + ".pick", pick);
                    if (rule.Scope is { } scope) Enum(c, where + ".scope", scope);
                    Number(c, where + ".weight", rule.Weight, 0, double.MaxValue);
                    break;

                default: // Fixed
                    Enum(c, where + ".ifBlocked", rule.IfBlocked);
                    if (rule.Sides.Count > 0) c.Add(where + ".sides", "only applies to Prefer placements; a Fixed one is placed by its offset.");
                    if (rule.Pick is not null) c.Add(where + ".pick", "only applies to SetRandom placements.");
                    if (rule.Scope is not null) c.Add(where + ".scope", "only applies to SetRandom placements.");
                    break;
            }
        }
    }

    private static void ValidateSides(Ctx c, string path, List<GlyphSide> sides)
    {
        if (sides.Contains(GlyphSide.Center) && sides.Count > 1) c.Add(path, "Center cannot be combined with other sides.");
        if (sides.Count > 2) c.Add(path, "takes at most two sides (one horizontal and one vertical).");
        if (sides.Distinct().Count() != sides.Count) c.Add(path, "lists a side twice.");
        if (sides.Contains(GlyphSide.Left) && sides.Contains(GlyphSide.Right))
            c.Add(path, "Left and Right cancel each other out.");
        if (sides.Contains(GlyphSide.Top) && sides.Contains(GlyphSide.Bottom))
            c.Add(path, "Top and Bottom cancel each other out.");
        foreach (var side in sides) Enum(c, path, side);
    }

    private static void ValidateSidePull(Ctx c, string path, SidePull pull)
    {
        Enum(c, path + ".mode", pull.Mode);
        Number(c, path + ".weight", pull.Weight, 0, double.MaxValue);

        if (pull.Mode == SidePullMode.Prefer)
        {
            if (pull.Pick is not null) c.Add(path + ".pick", "only applies to SetRandom.");
            if (pull.Sides.Count == 0) c.Add(path + ".sides", "a Prefer pull needs at least one side (Left, Right, Top, Bottom or Center).");
            ValidateSides(c, path + ".sides", pull.Sides);
        }
        else
        {
            if (pull.Sides.Count > 0) c.Add(path + ".sides", "only applies to Prefer; SetRandom draws its own (see pick).");
            if (pull.Pick is { } pick) Enum(c, path + ".pick", pick);
        }
    }

    /// <summary>arrangement.contact on a combination: a sensible chance and minimum, and a kind the crossing mode can give.</summary>
    private static void ValidateContact(Ctx c, string path, CombinationArrangementRules a)
    {
        if (a.Contact is not { } contact) return;
        string where = path + ".contact";
        Number(c, where + ".chance", contact.Chance, 0, 100);
        if (contact.Min < 1) c.Add(where + ".min", $"must be at least 1 (got {contact.Min}); leave contact out for no requirement.");
        Enum(c, where + ".kind", contact.Kind);
        if (contact.Kind == ContactKind.Crossing && a.Crossing.Mode == CrossingMode.None)
            c.Add(where + ".kind", "Crossing needs crossing.mode NodesOnly, Anywhere or AnywhereButRespectStrict: with None components never cross. Use Junction or Either.");
    }

    private static void ValidateArrangement(Ctx c, string path, ArrangementRules a, bool forShape)
    {
        foreach (var error in a.Spacing.Validate()) c.Add(path + ".spacing", error);
        foreach (var error in a.Bias.Validate()) c.Add(path + ".bias", error);

        Enum(c, path + ".crossing.mode", a.Crossing.Mode);
        if (forShape && a.Crossing.Mode == CrossingMode.AnywhereButRespectStrict)
            c.Add(path + ".crossing.mode", "AnywhereButRespectStrict is for combination.arrangement (and a recipe's arrangement) only; a shape uses Anywhere, NodesOnly, None or Strict.");
        Number(c, path + ".crossing.nodeTolerance", a.Crossing.NodeTolerance, 0, 5);
        if (a.Crossing.Mode != CrossingMode.NodesOnly && a.Crossing.NodesOfShapes is { Count: > 0 })
            c.Add(path + ".crossing.nodesOfShapes", "only applies with mode NodesOnly.");

        if (a.Candidates < 1)
            c.Add(path + ".candidates", $"must be at least 1 (got {a.Candidates}).");

        if (!forShape && a.Spacing.IgnoreStartNode)
            c.Add(path + ".spacing.ignoreStartNode", "only applies to a shape with placement.startOnExistingNode.");

        if (!forShape && a.Spacing.HasShapeFilter)
            c.Add(path + ".spacing.onlyShapes", "onlyShapes / excludeShapes only apply to shapes; a combination's spacing measures whole components.");

        if (forShape && a.Bias.IncludeScaleAndRotation)
            c.Add(path + ".bias.includeScaleAndRotation", "only applies to combination components; a shape's candidates are never scaled or rotated.");
    }

    private static void ValidateLiteral(Ctx c, string path, List<int[]> nodes)
    {
        if (nodes.Count < 2)
            c.Add(path + ".nodes", $"needs at least 2 points to form a stroke (got {nodes.Count}).");

        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node is null || node.Length != 2)
            {
                c.Add(path + ".nodes", $"item {i + 1} must be an [x, y] pair.");
                continue;
            }

            if (i > 0 && nodes[i - 1] is { Length: 2 } previous && previous[0] == node[0] && previous[1] == node[1])
                c.Add(path + ".nodes", $"items {i} and {i + 1} are the same point, which would make a zero-length edge.");
        }
    }

    // ---- placement ---------------------------------------------------------------------------

    private static void ValidatePlacement(LanguageDefinition l, Ctx c, string path, PlacementConfig p, bool rotationApplies, ArrangementRules arrangement, int shapeIndex = -1)
    {
        if (p.Bounds is { } box)
        {
            if (box.MinX > box.MaxX || box.MinY > box.MaxY)
                c.Add(path + ".bounds", "minX/minY must not be larger than maxX/maxY.");
            else if (box.MinX < 0 || box.MinY < 0 || box.MaxX >= l.Grid.Width || box.MaxY >= l.Grid.Height)
                c.Add(path + ".bounds", $"must lie inside the grid (x 0..{l.Grid.Width - 1}, y 0..{l.Grid.Height - 1}).");
            else if (p.Mode == PlacementMode.Fixed && p.FixedOrigin is { } fo && fo.Points.Any(o => o.X < box.MinX || o.X > box.MaxX || o.Y < box.MinY || o.Y > box.MaxY))
                c.Add(path + ".bounds", "does not contain every fixedOrigin.");
        }
        if (p.RedoShape < 0) c.Add(path + ".redoShape", $"must be 0 or more (got {p.RedoShape}).");
        if (p.RedrawGlyph < 0) c.Add(path + ".redrawGlyph", $"must be 0 or more (got {p.RedrawGlyph}).");
        Enum(c, path + ".mode", p.Mode);

        if (p.Side is not null)
        {
            if (p.Mode != PlacementMode.RandomValid)
                c.Add(path + ".side", "only applies with mode RandomValid: Fixed and CenterAnchor always use the same origin, so there is nothing to pull.");
            ValidateSidePull(c, path + ".side", p.Side);
        }

        if (p.Rotations is not null)
        {
            if (!rotationApplies)
                c.Add(path + ".rotations", "only affects Literal shapes - a generated shape ignores rotation, so this would do nothing here.");
            else if (!p.AllowRotationOnPlace)
                c.Add(path + ".rotations", "is set but allowRotationOnPlace is false, so it would be ignored.");

            foreach (var angle in p.Rotations)
            {
                if (Array.IndexOf(LatticeRotations, angle) < 0)
                    c.Add(path + ".rotations", $"{angle} is not supported; only 0, 90, 180 and 270 keep nodes on the grid.");
            }
        }

        if (p.MaxPlacementAttempts < 1)
            c.Add(path + ".maxPlacementAttempts", $"must be at least 1 (got {p.MaxPlacementAttempts}).");

        if (p.Mode == PlacementMode.Fixed)
        {
            if (p.FixedOrigin is null || p.FixedOrigin.Points.Count == 0)
                c.Add(path, "mode is Fixed but fixedOrigin is not set.");
            else
                for (int k = 0; k < p.FixedOrigin.Points.Count; k++)
                {
                    var o = p.FixedOrigin.Points[k];
                    if (o.X < 0 || o.X >= l.Grid.Width || o.Y < 0 || o.Y >= l.Grid.Height)
                        c.Add(path + ".fixedOrigin", $"{o} is outside the {l.Grid.Width}x{l.Grid.Height} grid.");
                }
        }
        else if (p.FixedOrigin is not null)
        {
            c.Add(path + ".fixedOrigin", $"is set but mode is {p.Mode}, so it would be ignored (use mode Fixed).");
        }

        if (p.DiscardUsedOrigins && p.Mode != PlacementMode.Fixed)
            c.Add(path + ".discardUsedOrigins", $"only applies to mode Fixed (mode is {p.Mode}).");
        if (arrangement.Crossing.Mode == CrossingMode.Strict && p.StartOnExistingNode)
            c.Add(path + ".startOnExistingNode", "cannot be combined with crossing.mode Strict (a shared node is a contact).");

        if (p.StartOnShapes is not null)
        {
            if (!p.StartOnExistingNode)
                c.Add(path + ".startOnShapes", "only applies together with startOnExistingNode: true.");
            if (p.StartOnShapes.Count == 0)
                c.Add(path + ".startOnShapes", "is empty, so there would be nothing to start on - list shape ids, or remove it for every shape.");
            foreach (var id in p.StartOnShapes)
            {
                int at = l.Shapes.FindIndex(x => x.Id == id);
                if (at < 0) c.Add(path + ".startOnShapes", $"'{id}' is not the id of a shape.");
                else if (shapeIndex >= 0 && at >= shapeIndex) c.Add(path + ".startOnShapes", $"'{id}' is not listed before this shape, so it is not drawn yet when this shape is placed.");
            }
        }

        if (arrangement.Spacing.IgnoreStartNode && !p.StartOnExistingNode)
            c.Add(path.Replace(".placement", ".arrangement.spacing") + ".ignoreStartNode", "only applies to a shape with placement.startOnExistingNode: true.");

        Number(c, path + ".startOnExistingNodeChance", p.StartOnExistingNodeChance, 0, 100);
        if (!p.StartOnExistingNode && p.StartOnExistingNodeChance < 100.0)
            c.Add(path + ".startOnExistingNodeChance", "only applies together with startOnExistingNode: true.");

        if (p.StartOnExistingNode)
        {
            if (p.Mode != PlacementMode.RandomValid)
                c.Add(path + ".startOnExistingNode", $"only applies when mode is RandomValid (is {p.Mode}).");

            // A shape starting on an existing node shares that node, i.e. is at distance 0 from it.
            // Unless every shape it may start on is one the spacing does not measure (onlyShapes / excludeShapes).
            bool startsOnlyOnUnmeasured = p.StartOnShapes is { Count: > 0 } starts && starts.All(id => !arrangement.Spacing.Covers(id));
            if (arrangement.Spacing is { Enabled: true, MinNodeDistance: > 0, IgnoreStartNode: false } && p.StartOnExistingNodeChance >= 100.0 && !startsOnlyOnUnmeasured)
                c.Add(path + ".startOnExistingNode", "can never satisfy arrangement.spacing.minNodeDistance > 0 (a shared node is at distance 0) - set arrangement.spacing.ignoreStartNode to leave the start node out of the check.");
        }
    }

    // ---- curves ------------------------------------------------------------------------------

    private static void ValidateCurve(LanguageDefinition l, Ctx c)
    {
        var curve = l.Curve;
        Enum(c, "curve.contextMode", curve.ContextMode);
        ValidateEdgeCurve(c, "curve.edge", curve.Edge);

        if (curve.ForceAllEdgesCurved)
        {
            bool someStraight = curve.Edge.ChanceToBeCurved < 100
                || l.Shapes.Any(s => s.Chain.ConnectionRules.Any(r => r.Curve.ChanceToBeCurved < 100));
            if (someStraight || curve.ProximityGuard.Enabled)
                c.Add("curve.forceAllEdgesCurved", "overrides every chanceToBeCurved and proximityGuard, which would be silently ignored - set the chances to 100 and disable the guard (or turn this off).");
        }

        Number(c, "curve.proximityGuard.minEdgeLength", curve.ProximityGuard.MinEdgeLength, 0, double.MaxValue);
    }

    private static void ValidateEdgeCurve(Ctx c, string path, EdgeCurveConfig edge)
    {
        Number(c, path + ".chanceToBeCurved", edge.ChanceToBeCurved, 0, 100);
        Enum(c, path + ".mode", edge.Mode);
        FloatRange(c, path + ".explicitJitterDegrees", edge.ExplicitJitterDegrees.Min, edge.ExplicitJitterDegrees.Max, 0, 180);

        if (edge.Tension.Options.Count == 0)
        {
            FloatRange(c, path + ".tension.range", edge.Tension.Range.Min, edge.Tension.Range.Max, 0, 0.5);
        }
        else
        {
            double total = 0;
            for (int i = 0; i < edge.Tension.Options.Count; i++)
            {
                var option = edge.Tension.Options[i];
                string where = $"{path}.tension.options[{i}]";
                FloatRange(c, where, option.Min, option.Max, 0, 0.5);
                if (Number(c, where + ".weight", option.Weight, 0, double.MaxValue)) total += option.Weight;
            }
            if (total <= 0) c.Add(path + ".tension.options", "every option has weight 0, so none could ever be chosen.");
        }
    }

    // ---- rendering ---------------------------------------------------------------------------

    private static void ValidateRender(RenderConfig render, Ctx c)
    {
        Enum(c, "render.framing", render.Framing);
        if (!double.IsFinite(render.RotateDegrees))
            c.Add("render.rotateDegrees", $"must be a finite number of degrees (got {render.RotateDegrees}).");
        for (int i = 0; i < render.RotateOptions.Count; i++)
        {
            var option = render.RotateOptions[i];
            string where = $"render.rotateOptions[{i}]";
            if (option is null) { c.Add(where, "must not be null."); continue; }
            if (!double.IsFinite(option.Min) || !double.IsFinite(option.Max)) c.Add(where, "min and max must be finite numbers of degrees.");
            else if (option.Max < option.Min) c.Add(where, $"max ({option.Max}) is smaller than min ({option.Min}).");
            Number(c, where + ".weight", option.Weight, 0, double.MaxValue);
        }
        if (render.RotateOptions.Count > 0 && render.RotateOptions.All(o => o is not null && !(o.Weight > 0)))
            c.Add("render.rotateOptions", "every weight is 0, so no option could ever be drawn.");

        if (render.ContactSheet.Columns < 1)
            c.Add("render.contactSheet.columns", $"must be at least 1 (got {render.ContactSheet.Columns}).");

        if (!HexColor.IsMatch(render.Foreground))
            c.Add("render.foreground", $"'{render.Foreground}' is not a hex colour (#RRGGBB or #AARRGGBB).");
        if (!HexColor.IsMatch(render.Background))
            c.Add("render.background", $"'{render.Background}' is not a hex colour (#RRGGBB or #AARRGGBB).");

        ValidateJoins(c, "render.joins", render.Joins);

        var t = render.Typeface;
        Enum(c, "render.typeface.kind", t.Kind);
        Number(c, "render.typeface.perGlyphJitter", t.PerGlyphJitter, 0, 0.9);

        if (t.Rectangular is { } r)
        {
            const string p = "render.typeface.rectangular";
            Width(c, p + ".width", r.Width);
            Enum(c, p + ".cap", r.Cap);
            Enum(c, p + ".capLevel", r.CapLevel);
            Enum(c, p + ".capLevelFlat", r.CapLevelFlat);
        }

        if (t.ThickEnds is { } e)
        {
            const string p = "render.typeface.thickEnds";
            Width(c, p + ".width", e.Width);
            Width(c, p + ".endWidth", e.EndWidth);
            FloatRange(c, p + ".flareLength", e.FlareLength.Min, e.FlareLength.Max, 0, 0.5);
            Enum(c, p + ".profile", e.Profile);
            Enum(c, p + ".appliesTo", e.AppliesTo);
            Enum(c, p + ".terminal", e.Terminal);
            if (!double.IsFinite(e.BallScale) || e.BallScale <= 0) c.Add(p + ".ballScale", $"must be above 0 (got {e.BallScale}).");
        }

        if (t.Brush is { } b)
        {
            const string p = "render.typeface.brush";
            Enum(c, p + ".mode", b.Mode);
            Width(c, p + ".width", b.Width);
            Width(c, p + ".tipWidth", b.TipWidth);
            Width(c, p + ".thinWidth", b.ThinWidth);
            FloatRange(c, p + ".nibAngleDegrees", b.NibAngleDegrees.Min, b.NibAngleDegrees.Max, -360, 360);
            if (!double.IsFinite(b.TaperSharpness) || b.TaperSharpness <= 0) c.Add(p + ".taperSharpness", $"must be above 0 (got {b.TaperSharpness}).");
            Number(c, p + ".nibRoundness", b.NibRoundness, 0, 1);
            Number(c, p + ".nibAngleWobble", b.NibAngleWobble, 0, 90);
            Number(c, p + ".nibPressure", b.NibPressure, 0, 1);
            Number(c, p + ".nibLeadIn", b.NibLeadIn, 0, 0.5);
            Number(c, p + ".pressureVariation", b.PressureVariation, 0, 1);
            Number(c, p + ".edgeWobble", b.EdgeWobble, 0, double.MaxValue);
            Number(c, p + ".raggedness", b.Raggedness, 0, double.MaxValue);
            Number(c, p + ".startBlobScale", b.StartBlobScale, 0, double.MaxValue);
        }
    }

    private static void ValidateJoins(Ctx c, string path, JoinsConfig joins)
    {
        ValidateJoinContext(c, path + ".corner", joins.Corner);
        ValidateJoinContext(c, path + ".junction", joins.Junction);

        var junction = joins.Junction;
        Number(c, path + ".junction.straightenInnerDegrees", junction.StraightenInnerDegrees, 0, 90);
        Number(c, path + ".junction.straightenOuterDegrees", junction.StraightenOuterDegrees, 0, 90);
        Number(c, path + ".junction.straightenLength", junction.StraightenLength, 0, double.MaxValue);
    }

    private static void ValidateJoinContext(Ctx c, string path, JoinContextConfig context)
    {
        ValidateJoinSide(c, path + ".outer", context.Outer);
        ValidateJoinSide(c, path + ".inner", context.Inner);

        for (int i = 0; i < context.ByAngle.Count; i++)
        {
            var rule = context.ByAngle[i];
            string where = $"{path}.byAngle[{i}]";
            if (rule is null) { c.Add(where, "must not be null."); continue; }

            FloatRange(c, where + ".angle", rule.Angle.Min, rule.Angle.Max, 0, 180);
            if (rule.Outer is null && rule.Inner is null)
                c.Add(where, "sets neither 'outer' nor 'inner', so it would do nothing.");
            if (rule.Outer is not null) ValidateJoinSide(c, where + ".outer", rule.Outer);
            if (rule.Inner is not null) ValidateJoinSide(c, where + ".inner", rule.Inner);
        }
    }

    private static void ValidateJoinSide(Ctx c, string path, JoinSideConfig side)
    {
        Enum(c, path + ".style", side.Style);
        Enum(c, path + ".axis", side.Axis);
        MiterLimit(c, path + ".miterLimit", side.MiterLimit);
        if (!double.IsFinite(side.Size) || side.Size < 0)
            c.Add(path + ".size", $"must be a finite number >= 0 (got {side.Size}).");
    }

    // ---- small helpers -----------------------------------------------------------------------

    /// <summary>Returns true when the value is acceptable.</summary>
    private static bool Number(Ctx c, string where, double value, double min, double max)
    {
        if (double.IsFinite(value) && value >= min && value <= max) return true;

        string range = max == double.MaxValue ? $">= {min}" : $"between {min} and {max}";
        c.Add(where, $"must be a finite number {range} (got {value}).");
        return false;
    }

    private static void IntRange(Ctx c, string where, IntRange range, int minAllowed, string? reason = null)
    {
        if (range.Min < minAllowed)
            c.Add(where, $"min must be at least {minAllowed}{(reason is null ? "" : $" ({reason})")} (got {range.Min}).");
        if (range.Max < range.Min)
            c.Add(where, $"max ({range.Max}) is smaller than min ({range.Min}).");
    }

    private static void Percent(Ctx c, string where, PercentRange range) =>
        FloatRange(c, where, range.Min, range.Max, 0, 100);

    private static void Width(Ctx c, string where, FloatRange range) =>
        FloatRange(c, where, range.Min, range.Max, 0, 1);

    private static void FloatRange(Ctx c, string where, double min, double max, double lo, double hi)
    {
        if (!double.IsFinite(min) || !double.IsFinite(max))
        {
            c.Add(where, "min and max must be finite numbers.");
            return;
        }

        if (min < lo || max > hi)
            c.Add(where, $"must lie between {lo} and {hi} (got {min}..{max}).");
        if (max < min)
            c.Add(where, $"max ({max}) is smaller than min ({min}).");
    }

    private static void MiterLimit(Ctx c, string where, double value)
    {
        if (!double.IsFinite(value) || value < 1)
            c.Add(where, $"must be at least 1 (a miter is never shorter than half the stroke width; got {value}).");
    }

    private static void Enum<T>(Ctx c, string where, T value) where T : struct, System.Enum
    {
        if (!System.Enum.IsDefined(typeof(T), value))
            c.Add(where, $"has an unknown value ({value}).");
    }
}
