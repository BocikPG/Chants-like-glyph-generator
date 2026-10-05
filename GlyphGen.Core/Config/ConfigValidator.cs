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

        // Free-form sub-symbols
        IntRange(c, "subSymbolCount", l.SubSymbolCount, 0);
        IntRange(c, "subSymbolLength", l.SubSymbolLength, 2, "a stroke needs at least 2 nodes");
        ValidateRules(c, "connectionRules", l.ConnectionRules, required: l.SubSymbolCount.Max > 0);
        Enum(c, "connectionWeighting", l.ConnectionWeighting);
        Number(c, "closedLoopWeight", l.ClosedLoopWeight, 0, double.MaxValue);

        ValidateMotifs(l, c);
        ValidatePlacement(l, c, "subSymbolPlacement", l.SubSymbolPlacement, rotationApplies: false);

        ValidateCurve(l.Curve, c);
        ValidateCombinationSettings(l.Combination, c);

        if (!double.IsFinite(l.Quality.MaxSimilarity) || l.Quality.MaxSimilarity <= 0)
            c.Add("quality.maxSimilarity", $"must be a finite number above 0 (1 or more disables the check; got {l.Quality.MaxSimilarity}).");
        if (!double.IsFinite(l.Quality.CompositeMaxSimilarity) || l.Quality.CompositeMaxSimilarity <= 0)
            c.Add("quality.compositeMaxSimilarity", $"must be a finite number above 0 (1 or more disables the check; got {l.Quality.CompositeMaxSimilarity}).");
        if (l.Quality.MaxRegenerationAttempts < 1)
            c.Add("quality.maxRegenerationAttempts", $"must be at least 1 (got {l.Quality.MaxRegenerationAttempts}).");

        ValidateRender(l.Render, c);

        foreach (var error in l.SubSymbolSpacing.Validate()) c.Add("subSymbolSpacing", error);
        foreach (var error in l.UsageSizeBias.Validate()) c.Add("usageSizeBias", error);
        foreach (var error in l.Combination.Bias.Validate()) c.Add("combination.bias", error);
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

        foreach (var (key, components) in recipes)
        {
            string where = $"combination.recipes['{key}']";

            int keyIndex = -1;
            if (baseNames.Contains(key))
                c.Add(where, "is a base glyph but also has a recipe - base glyphs are components, not composites.");
            else if (!glyphIndex.TryGetValue(key, out keyIndex))
                c.Add(where, "is not listed in glyphs, so this recipe would never be used.");

            if (components.Count == 0)
                c.Add(where, "has no components.");

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
    }

    private static void ValidateCombinationSettings(CombinationConfig combination, Ctx c)
    {
        if (combination.MaxRetriesPerRecipe < 1)
            c.Add("combination.maxRetriesPerRecipe", $"must be at least 1 (got {combination.MaxRetriesPerRecipe}).");

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
            if (rule.MinLength < 1) c.Add(where, $"minLength must be at least 1 (got {rule.MinLength}).");
            if (rule.MaxLength < rule.MinLength) c.Add(where, $"maxLength ({rule.MaxLength}) is smaller than minLength ({rule.MinLength}).");
            if (Number(c, where + ".weight", rule.Weight, 0, double.MaxValue)) total += rule.Weight;
        }

        if (total <= 0) c.Add(path, "every rule has weight 0, so none could ever be chosen.");
    }

    private static void ValidateMotifs(LanguageDefinition l, Ctx c)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < l.GuaranteedMotifs.Count; i++)
        {
            var motif = l.GuaranteedMotifs[i];
            string where = string.IsNullOrWhiteSpace(motif.Id) ? $"guaranteedMotifs[{i}]" : $"guaranteedMotifs['{motif.Id}']";

            if (string.IsNullOrWhiteSpace(motif.Id))
                c.Add(where, "id must not be empty.");
            else if (!ids.Add(motif.Id))
                c.Add(where, "id is used by more than one motif (the id also seeds the motif, so two would come out identical).");

            Enum(c, where + ".type", motif.Type);

            switch (motif.Type)
            {
                case MotifType.Literal:
                    if (motif.Generative is not null)
                        c.Add(where, "type is Literal but a 'generative' block is also present - fill in only the one matching the type.");
                    if (motif.Literal is null)
                        c.Add(where, "type is Literal but there is no 'literal' block.");
                    else
                        ValidateLiteral(c, where + ".literal", motif.Literal);
                    break;

                case MotifType.Generative:
                    if (motif.Literal is not null)
                        c.Add(where, "type is Generative but a 'literal' block is also present - fill in only the one matching the type.");
                    if (motif.Generative is null)
                        c.Add(where, "type is Generative but there is no 'generative' block.");
                    else
                    {
                        string g = where + ".generative";
                        IntRange(c, g + ".lengthRange", motif.Generative.LengthRange, 2, "a stroke needs at least 2 nodes");
                        ValidateRules(c, g + ".connectionRules", motif.Generative.ConnectionRules, required: true);
                        Number(c, g + ".closedLoopWeight", motif.Generative.ClosedLoopWeight, 0, double.MaxValue);
                    }
                    break;
            }

            ValidatePlacement(l, c, where + ".placement", motif.Placement, rotationApplies: motif.Type == MotifType.Literal);
        }
    }

    private static void ValidateLiteral(Ctx c, string path, LiteralMotifConfig literal)
    {
        if (literal.Nodes.Count < 2)
            c.Add(path + ".nodes", $"needs at least 2 points to form a stroke (got {literal.Nodes.Count}).");

        for (int i = 0; i < literal.Nodes.Count; i++)
        {
            var node = literal.Nodes[i];
            if (node is null || node.Length != 2)
            {
                c.Add(path + ".nodes", $"item {i + 1} must be an [x, y] pair.");
                continue;
            }

            if (i > 0 && literal.Nodes[i - 1] is { Length: 2 } previous && previous[0] == node[0] && previous[1] == node[1])
                c.Add(path + ".nodes", $"items {i} and {i + 1} are the same point, which would make a zero-length edge.");
        }
    }

    // ---- placement ---------------------------------------------------------------------------

    private static void ValidatePlacement(LanguageDefinition l, Ctx c, string path, PlacementConfig p, bool rotationApplies)
    {
        Enum(c, path + ".mode", p.Mode);

        if (p.Rotations is not null)
        {
            if (!rotationApplies)
                c.Add(path + ".rotations", "only affects Literal motifs - a generated shape ignores rotation, so this would do nothing here.");
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
            if (p.FixedOrigin is null)
                c.Add(path, "mode is Fixed but fixedOrigin is not set.");
            else if (p.FixedOrigin.Value.X < 0 || p.FixedOrigin.Value.X >= l.Grid.Width
                  || p.FixedOrigin.Value.Y < 0 || p.FixedOrigin.Value.Y >= l.Grid.Height)
                c.Add(path + ".fixedOrigin", $"{p.FixedOrigin.Value} is outside the {l.Grid.Width}x{l.Grid.Height} grid.");
        }
        else if (p.FixedOrigin is not null)
        {
            c.Add(path + ".fixedOrigin", $"is set but mode is {p.Mode}, so it would be ignored (use mode Fixed).");
        }

        if (p.StartOnExistingNode)
        {
            if (p.Mode != PlacementMode.RandomValid)
                c.Add(path + ".startOnExistingNode", $"only applies when mode is RandomValid (is {p.Mode}).");

            // A shape starting on an existing node shares that node, i.e. is at distance 0 from it.
            if (l.SubSymbolSpacing is { Enabled: true, MinNodeDistance: > 0 })
                c.Add(path + ".startOnExistingNode", "can never satisfy subSymbolSpacing.minNodeDistance > 0 (a shared node is at distance 0).");
        }
    }

    // ---- curves ------------------------------------------------------------------------------

    private static void ValidateCurve(CurveConfig curve, Ctx c)
    {
        Enum(c, "curve.mode", curve.Mode);
        Enum(c, "curve.contextMode", curve.ContextMode);

        Percent(c, "curve.straightLineBias", curve.StraightLineBias);
        FloatRange(c, "curve.explicitJitterDegrees", curve.ExplicitJitterDegrees.Min, curve.ExplicitJitterDegrees.Max, 0, 180);

        if (curve.Tension.Options.Count == 0)
        {
            FloatRange(c, "curve.tension.range", curve.Tension.Range.Min, curve.Tension.Range.Max, 0, 0.5);
        }
        else
        {
            double total = 0;
            for (int i = 0; i < curve.Tension.Options.Count; i++)
            {
                var option = curve.Tension.Options[i];
                string where = $"curve.tension.options[{i}]";
                FloatRange(c, where, option.Min, option.Max, 0, 0.5);
                if (Number(c, where + ".weight", option.Weight, 0, double.MaxValue)) total += option.Weight;
            }
            if (total <= 0) c.Add("curve.tension.options", "every option has weight 0, so none could ever be chosen.");
        }

        if (curve.RecheckProximityInCombined && !curve.ProximityGuard.Enabled)
            c.Add("curve.recheckProximityInCombined", "needs curve.proximityGuard.enabled, otherwise there is no guard to re-run.");
        if (curve.ForceAllEdgesCurved && (curve.StraightLineBias.Max > 0 || curve.ProximityGuard.Enabled))
            c.Add("curve.forceAllEdgesCurved", "overrides straightLineBias and proximityGuard, which would be silently ignored - set straightLineBias to 0 and disable the guard (or turn this off).");

        Number(c, "curve.proximityGuard.minEdgeLength", curve.ProximityGuard.MinEdgeLength, 0, double.MaxValue);
        Number(c, "curve.proximityGuard.minDistanceToOtherNodes", curve.ProximityGuard.MinDistanceToOtherNodes, 0, double.MaxValue);
    }

    // ---- rendering ---------------------------------------------------------------------------

    private static void ValidateRender(RenderConfig render, Ctx c)
    {
        Enum(c, "render.framing", render.Framing);

        if (render.ContactSheet.Columns < 1)
            c.Add("render.contactSheet.columns", $"must be at least 1 (got {render.ContactSheet.Columns}).");

        if (!HexColor.IsMatch(render.Foreground))
            c.Add("render.foreground", $"'{render.Foreground}' is not a hex colour (#RRGGBB or #AARRGGBB).");
        if (!HexColor.IsMatch(render.Background))
            c.Add("render.background", $"'{render.Background}' is not a hex colour (#RRGGBB or #AARRGGBB).");

        var t = render.Typeface;
        Enum(c, "render.typeface.kind", t.Kind);
        Number(c, "render.typeface.perGlyphJitter", t.PerGlyphJitter, 0, 0.9);

        if (t.Rectangular is { } r)
        {
            const string p = "render.typeface.rectangular";
            Width(c, p + ".width", r.Width);
            Enum(c, p + ".join", r.Join);
            Enum(c, p + ".cap", r.Cap);
            MiterLimit(c, p + ".miterLimit", r.MiterLimit);
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
            Enum(c, p + ".join", e.Join);
            MiterLimit(c, p + ".miterLimit", e.MiterLimit);
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
            Number(c, p + ".pressureVariation", b.PressureVariation, 0, 1);
            Number(c, p + ".edgeWobble", b.EdgeWobble, 0, double.MaxValue);
            Number(c, p + ".raggedness", b.Raggedness, 0, double.MaxValue);
            Number(c, p + ".startBlobScale", b.StartBlobScale, 0, double.MaxValue);
            Enum(c, p + ".join", b.Join);
            MiterLimit(c, p + ".miterLimit", b.MiterLimit);
        }
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
