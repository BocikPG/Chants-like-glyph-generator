namespace GlyphGen.Core.Config;

/// <summary>
/// Which options only apply in which mode, declared once and turned into the schema's if/then rules by
/// <see cref="ConfigSchemaGenerator"/>: an editor then flags an option that has no effect (or a missing one) as soon as it is typed,
/// and each affected option's hover text says when it applies. The loader's own checks (ConfigValidator) stay the authority; these
/// rules mirror what it rejects, plus the cases it used to leave silently ignored.
///
/// A rule names a "discriminator" property of the same class (a mode, a kind, a flag) and the values it must have. When the
/// discriminator is left out its class default counts, so "mode" left out on a placement means RandomValid. Property names are C#
/// names; the generator checks that every one exists, so a rename cannot leave a stale rule behind.
/// </summary>
internal abstract record SchemaRule(Type Type);

/// <summary>The listed options are allowed only while <paramref name="When"/> is one of <paramref name="Values"/> (enum names, or "true").</summary>
internal sealed record OnlyWhen(Type Type, string[] Properties, string When, string[] Values) : SchemaRule(Type);

/// <summary>The option must be written while <paramref name="When"/> is one of <paramref name="Values"/>.</summary>
internal sealed record RequiredWhen(Type Type, string Property, string When, string[] Values) : SchemaRule(Type);

/// <summary>At least one of the options must be written while <paramref name="When"/> is one of <paramref name="Values"/>.</summary>
internal sealed record RequiredAnyWhen(Type Type, string[] Properties, string When, string[] Values) : SchemaRule(Type);

/// <summary>When <paramref name="Trigger"/> is written, none of the listed options may be.</summary>
internal sealed record ForbidWhenSet(Type Type, string Trigger, string[] Properties) : SchemaRule(Type);

/// <summary>Exactly one of the options must be written.</summary>
internal sealed record ExactlyOne(Type Type, string[] Properties) : SchemaRule(Type);

/// <summary>At most one of the options may be written.</summary>
internal sealed record NotTogether(Type Type, string[] Properties) : SchemaRule(Type);

/// <summary>Options reached through nested blocks (a path such as arrangement, spacing, ignoreStartNode) that are allowed only while
/// another nested option (<paramref name="WhenPath"/>) has one of the values. The discriminator must not default to one of the values.</summary>
internal sealed record OnlyWhenAt(Type Type, string[][] Targets, string[] WhenPath, string[] Values) : SchemaRule(Type);

/// <summary>Options reached through nested blocks that this class never accepts (it shares a block type with a class that does).</summary>
internal sealed record ForbidAt(Type Type, string[][] Targets, string Why) : SchemaRule(Type);

/// <summary>A hand-written condition, for the few rules no other record can say.</summary>
internal sealed record Raw(Type Type, string Property, string Note, Func<System.Text.Json.Nodes.JsonNode> Build) : SchemaRule(Type);

internal static class SchemaRules
{
    private static string Json(string property) => char.ToLowerInvariant(property[0]) + property[1..];

    private static string ValuesText(string[] values) => values.Length == 1 ? values[0] : string.Join(", ", values.Take(values.Length - 1)) + " or " + values[^1];

    private static string WhenText(string when, string[] values) =>
        values is ["true"] ? $"`{Json(when)}` is true" : $"`{Json(when)}` is {ValuesText(values)}";

    /// <summary>The sentence added to an option's hover text when a rule limits it; null when none does.</summary>
    public static string? NoteFor(Type type, string property)
    {
        var notes = new List<string>();
        foreach (var rule in All.Where(r => r.Type == type))
        {
            switch (rule)
            {
                case OnlyWhen only when only.Properties.Contains(property):
                    notes.Add($"Only used when {WhenText(only.When, only.Values)}.");
                    break;
                case RequiredWhen need when need.Property == property:
                    notes.Add($"Required when {WhenText(need.When, need.Values)}.");
                    break;
                case RequiredAnyWhen any when any.Properties.Contains(property):
                    notes.Add($"One of {string.Join(", ", any.Properties.Select(p => "`" + Json(p) + "`"))} is required when {WhenText(any.When, any.Values)}.");
                    break;
                case ForbidWhenSet forbid when forbid.Properties.Contains(property):
                    notes.Add($"Leave out on a shape with `{Json(forbid.Trigger)}` (a copy follows its original).");
                    break;
                case ExactlyOne one when one.Properties.Contains(property):
                    notes.Add($"Set exactly one of {string.Join(", ", one.Properties.Select(p => "`" + Json(p) + "`"))}.");
                    break;
                case Raw raw when raw.Property == property:
                    notes.Add(raw.Note);
                    break;
                case NotTogether apart when apart.Properties.Contains(property):
                    notes.Add($"Not together with {string.Join(" or ", apart.Properties.Where(p => p != property).Select(p => "`" + Json(p) + "`"))}.");
                    break;
            }
        }

        return notes.Count == 0 ? null : string.Join(" ", notes);
    }

    /// <summary>The if/then/else entries of a class schema's allOf (empty when no rule names the class). The class's own defaults
    /// stand in for a discriminator that is left out.</summary>
    public static System.Text.Json.Nodes.JsonArray ConditionsFor(Type type, object? defaults)
    {
        var result = new System.Text.Json.Nodes.JsonArray();
        foreach (var rule in All.Where(r => r.Type == type))
        {
            Check(type, rule);
            foreach (var entry in Emit(type, rule, defaults)) result.Add(entry);
        }

        return result;
    }

    private static void Check(Type type, SchemaRule rule)
    {
        var names = rule switch
        {
            OnlyWhen r => r.Properties.Append(r.When),
            RequiredWhen r => new[] { r.Property, r.When },
            RequiredAnyWhen r => r.Properties.Append(r.When),
            ForbidWhenSet r => r.Properties.Append(r.Trigger),
            ExactlyOne r => r.Properties,
            NotTogether r => r.Properties,
            Raw r => new[] { r.Property },
            _ => Array.Empty<string>(),
        };
        foreach (var name in names)
            if (type.GetProperty(name) is null)
                throw new InvalidOperationException($"Schema rule names {type.Name}.{name}, which does not exist.");

        var paths = rule switch
        {
            OnlyWhenAt r => r.Targets.Append(r.WhenPath),
            ForbidAt r => r.Targets,
            _ => Array.Empty<string[]>(),
        };
        foreach (var path in paths)
        {
            var at = type;
            foreach (var segment in path)
            {
                var step = at.GetProperty(segment) ?? throw new InvalidOperationException($"Schema rule names {at.Name}.{segment}, which does not exist.");
                at = Nullable.GetUnderlyingType(step.PropertyType) ?? step.PropertyType;
            }
        }
    }

    private static System.Text.Json.Nodes.JsonNode Required(params string[] properties) =>
        new System.Text.Json.Nodes.JsonObject { ["required"] = new System.Text.Json.Nodes.JsonArray(properties.Select(p => (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(Json(p))!).ToArray()) };

    /// <summary>The options must not be written. Each is a property with the schema false, so an editor marks the option itself
    /// ("Property x is not allowed") rather than the whole block.</summary>
    private static System.Text.Json.Nodes.JsonNode Absent(params string[] properties)
    {
        var props = new System.Text.Json.Nodes.JsonObject();
        foreach (var p in properties) props[Json(p)] = false;
        return new System.Text.Json.Nodes.JsonObject { ["properties"] = props };
    }

    /// <summary>"when is one of values" as a schema. A discriminator that is left out counts as its class default.</summary>
    private static System.Text.Json.Nodes.JsonNode Holds(Type type, object? defaults, string when, string[] values)
    {
        var json = new System.Text.Json.Nodes.JsonArray(values.Select(v => v is "true" or "false"
            ? (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(v == "true")!
            : System.Text.Json.Nodes.JsonValue.Create(v)!).ToArray());
        var written = new System.Text.Json.Nodes.JsonObject
        {
            ["properties"] = new System.Text.Json.Nodes.JsonObject { [Json(when)] = new System.Text.Json.Nodes.JsonObject { ["enum"] = json } },
            ["required"] = new System.Text.Json.Nodes.JsonArray(Json(when)),
        };

        object? fallback = defaults is null ? null : type.GetProperty(when)!.GetValue(defaults);
        string? fallbackText = fallback is bool b ? (b ? "true" : "false") : fallback?.ToString();
        if (fallbackText is null || !values.Contains(fallbackText)) return written;

        return new System.Text.Json.Nodes.JsonObject
        {
            ["anyOf"] = new System.Text.Json.Nodes.JsonArray(written, Absent(when)),
        };
    }

    /// <summary>The nested properties named by paths (C# names) must all be absent.</summary>
    private static System.Text.Json.Nodes.JsonNode AbsentAt(string[][] targets)
    {
        var all = targets.Select(AbsentPath).ToArray();
        return all.Length == 1 ? all[0] : new System.Text.Json.Nodes.JsonObject { ["allOf"] = new System.Text.Json.Nodes.JsonArray(all) };
    }

    private static System.Text.Json.Nodes.JsonNode AbsentPath(string[] path)
    {
        if (path.Length == 1) return Absent(path[0]);
        return new System.Text.Json.Nodes.JsonObject
        {
            ["properties"] = new System.Text.Json.Nodes.JsonObject { [Json(path[0])] = AbsentPath(path[1..]) },
        };
    }

    /// <summary>"the nested option at path is one of values", written. The default of the option must not be one of the values (a
    /// left-out option could then not be told apart from a written one).</summary>
    private static System.Text.Json.Nodes.JsonNode HoldsAt(Type type, object? defaults, string[] path, string[] values)
    {
        var at = type;
        object? instance = defaults;
        for (int i = 0; i < path.Length; i++)
        {
            var step = type == at ? at.GetProperty(path[i])! : at.GetProperty(path[i])!;
            if (i == path.Length - 1)
            {
                object? fallback = instance is null ? null : step.GetValue(instance);
                string? fallbackText = fallback is bool b ? (b ? "true" : "false") : fallback?.ToString();
                if (fallbackText is not null && values.Contains(fallbackText))
                    throw new InvalidOperationException($"Schema rule on {type.Name}: {string.Join('.', path)} defaults to a value the rule needs.");
            }
            else
            {
                instance = instance is null ? null : step.GetValue(instance);
                at = Nullable.GetUnderlyingType(step.PropertyType) ?? step.PropertyType;
            }
        }

        var json = new System.Text.Json.Nodes.JsonArray(values.Select(v => v is "true" or "false"
            ? (System.Text.Json.Nodes.JsonNode)System.Text.Json.Nodes.JsonValue.Create(v == "true")!
            : System.Text.Json.Nodes.JsonValue.Create(v)!).ToArray());
        System.Text.Json.Nodes.JsonNode node = new System.Text.Json.Nodes.JsonObject
        {
            ["properties"] = new System.Text.Json.Nodes.JsonObject { [Json(path[^1])] = new System.Text.Json.Nodes.JsonObject { ["enum"] = json } },
            ["required"] = new System.Text.Json.Nodes.JsonArray(Json(path[^1])),
        };
        for (int i = path.Length - 2; i >= 0; i--)
        {
            node = new System.Text.Json.Nodes.JsonObject
            {
                ["properties"] = new System.Text.Json.Nodes.JsonObject { [Json(path[i])] = node },
                ["required"] = new System.Text.Json.Nodes.JsonArray(Json(path[i])),
            };
        }

        return node;
    }

    private static IEnumerable<System.Text.Json.Nodes.JsonNode> Emit(Type type, SchemaRule rule, object? defaults)
    {
        switch (rule)
        {
            case OnlyWhen r:
                yield return new System.Text.Json.Nodes.JsonObject { ["if"] = Holds(type, defaults, r.When, r.Values), ["else"] = Absent(r.Properties) };
                break;
            case RequiredWhen r:
                yield return new System.Text.Json.Nodes.JsonObject { ["if"] = Holds(type, defaults, r.When, r.Values), ["then"] = Required(r.Property) };
                break;
            case RequiredAnyWhen r:
                yield return new System.Text.Json.Nodes.JsonObject
                {
                    ["if"] = Holds(type, defaults, r.When, r.Values),
                    ["then"] = new System.Text.Json.Nodes.JsonObject { ["anyOf"] = new System.Text.Json.Nodes.JsonArray(r.Properties.Select(p => Required(p)).ToArray()) },
                };
                break;
            case ForbidWhenSet r:
                yield return new System.Text.Json.Nodes.JsonObject { ["if"] = Required(r.Trigger), ["then"] = Absent(r.Properties) };
                break;
            case ExactlyOne r:
                yield return new System.Text.Json.Nodes.JsonObject { ["oneOf"] = new System.Text.Json.Nodes.JsonArray(r.Properties.Select(p => Required(p)).ToArray()) };
                break;
            case NotTogether r:
                yield return new System.Text.Json.Nodes.JsonObject { ["not"] = Required(r.Properties) };
                break;
            case OnlyWhenAt r:
                yield return new System.Text.Json.Nodes.JsonObject { ["if"] = HoldsAt(type, defaults, r.WhenPath, r.Values), ["else"] = AbsentAt(r.Targets) };
                break;
            case ForbidAt r:
                yield return AbsentAt(r.Targets);
                break;
            case Raw r:
                yield return r.Build();
                break;
        }
    }

    // ---- run-time use of the same rules: warnings about options that do nothing ----

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, bool> _hasWarnRules = new();

    private static bool WarnsAbout(SchemaRule rule) => rule is OnlyWhen or OnlyWhenAt or ForbidAt or ForbidWhenSet or Raw;

    /// <summary>Whether any rule that produces warnings names this class.</summary>
    public static bool HasWarnRules(Type type) => _hasWarnRules.GetOrAdd(type, t => All.Any(r => r.Type == t && WarnsAbout(r)));

    /// <summary>The options written in <paramref name="written"/> (the raw json of one object of <paramref name="type"/>) that the
    /// rules say have no effect, as sentences. The loader reports them as warnings and does not reject the config.</summary>
    public static IEnumerable<string> Warnings(Type type, System.Text.Json.Nodes.JsonObject written, string path)
    {
        object? defaults = null;
        try { defaults = Activator.CreateInstance(type); } catch { }

        foreach (var rule in All.Where(r => r.Type == type && WarnsAbout(r)))
        {
            foreach (var node in Emit(type, rule, defaults))
            {
                if (Valid(node, written)) continue;
                // A switch written out as false on purpose ("enabled": false) keeps its numbers for later: that is not a mistake.
                if (rule is OnlyWhen { Values: ["true"] } off && Find(written, Json(off.When)) is System.Text.Json.Nodes.JsonValue sw && sw.TryGetValue<bool>(out var on) && !on) continue;
                yield return $"{(path.Length == 0 ? "top level" : path)}: {RuleSentence(rule, written)}";
            }
        }
    }

    private static string RuleSentence(SchemaRule rule, System.Text.Json.Nodes.JsonObject written)
    {
        bool Has(string name) => Find(written, Json(name)) is { } v && v is not null;
        static string Names(IEnumerable<string> names) => string.Join(", ", names.Select(n => "`" + n + "`"));
        switch (rule)
        {
            case OnlyWhen r:
                return $"{Names(r.Properties.Where(Has).Select(Json))} is ignored: it is only used when {WhenText(r.When, r.Values)}.";
            case OnlyWhenAt r:
                return $"{Names(r.Targets.Select(t => string.Join('.', t.Select(Json))))} is ignored: it is only used when `{string.Join('.', r.WhenPath.Select(Json))}` is {ValuesText(r.Values)}.";
            case ForbidAt r:
                return $"{Names(r.Targets.Select(t => string.Join('.', t.Select(Json))))} has no effect here ({r.Why}).";
            case ForbidWhenSet r:
                return $"{Names(r.Properties.Where(Has).Select(Json))} is ignored next to `{Json(r.Trigger)}`.";
            case Raw r:
                return r.Note;
            default:
                return "an option has no effect.";
        }
    }

    private static System.Text.Json.Nodes.JsonNode? Find(System.Text.Json.Nodes.JsonObject obj, string name)
    {
        foreach (var kv in obj)
            if (string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)) return kv.Value;
        return null;
    }

    /// <summary>Checks a value against the small part of json schema that the rules produce (properties, required, enum, const,
    /// maximum, items, if/then/else, allOf/anyOf/oneOf/not). A property written as null counts as not written.</summary>
    private static bool Valid(System.Text.Json.Nodes.JsonNode schema, System.Text.Json.Nodes.JsonNode? value)
    {
        if (schema is System.Text.Json.Nodes.JsonValue flag && flag.TryGetValue<bool>(out var allowed)) return allowed;
        var s = schema.AsObject();
        if (s["if"] is { } cond)
        {
            if (Valid(cond, value)) { if (s["then"] is { } then && !Valid(then, value)) return false; }
            else if (s["else"] is { } other && !Valid(other, value)) return false;
        }
        if (s["allOf"] is System.Text.Json.Nodes.JsonArray all && !all.All(x => Valid(x!, value))) return false;
        if (s["anyOf"] is System.Text.Json.Nodes.JsonArray any && !any.Any(x => Valid(x!, value))) return false;
        if (s["oneOf"] is System.Text.Json.Nodes.JsonArray one && one.Count(x => Valid(x!, value)) != 1) return false;
        if (s["not"] is { } not && Valid(not, value)) return false;
        if (s["required"] is System.Text.Json.Nodes.JsonArray req)
        {
            if (value is not System.Text.Json.Nodes.JsonObject o) return false;
            if (req.Any(n => Find(o, n!.GetValue<string>()) is null)) return false;
        }
        if (s["properties"] is System.Text.Json.Nodes.JsonObject props && value is System.Text.Json.Nodes.JsonObject obj)
            foreach (var kv in props)
                if (Find(obj, kv.Key) is { } child && !Valid(kv.Value!, child)) return false;
        if (s["enum"] is System.Text.Json.Nodes.JsonArray values && !values.Any(v => Same(v, value))) return false;
        if (s["const"] is { } constant && !Same(constant, value)) return false;
        if (s["minimum"] is { } least && value is System.Text.Json.Nodes.JsonValue lv && AsNumber(lv) is { } lowest && AsNumber(least) is { } lm && lowest < lm) return false;
        if (s["exclusiveMinimum"] is { } above && value is System.Text.Json.Nodes.JsonValue av && AsNumber(av) is { } an && AsNumber(above) is { } am && an <= am) return false;
        if (s["exclusiveMaximum"] is { } below && value is System.Text.Json.Nodes.JsonValue bv && AsNumber(bv) is { } bn && AsNumber(below) is { } bm && bn >= bm) return false;
        if (s["contains"] is { } needle && value is System.Text.Json.Nodes.JsonArray haystack && !haystack.Any(x => x is not null && Valid(needle, x))) return false;
        if (s["maximum"] is { } max && value is System.Text.Json.Nodes.JsonValue number && AsNumber(number) is { } d && AsNumber(max) is { } m && d > m) return false;
        if (s["items"] is { } items && value is System.Text.Json.Nodes.JsonArray list && !list.All(x => x is null || Valid(items, x))) return false;
        return true;
    }

    private static double? AsNumber(System.Text.Json.Nodes.JsonNode? node)
    {
        if (node is not System.Text.Json.Nodes.JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<int>(out var i)) return i;
        if (v.TryGetValue<long>(out var l)) return l;
        return null;
    }

    private static bool Same(System.Text.Json.Nodes.JsonNode? a, System.Text.Json.Nodes.JsonNode? b)
    {
        if (a is null || b is null) return false;
        if (a is System.Text.Json.Nodes.JsonValue x && b is System.Text.Json.Nodes.JsonValue y)
        {
            if (x.TryGetValue<string>(out var sx) && y.TryGetValue<string>(out var sy)) return string.Equals(sx, sy, StringComparison.OrdinalIgnoreCase);
            if (x.TryGetValue<bool>(out var bx) && y.TryGetValue<bool>(out var by)) return bx == by;
        }
        return false;
    }

    private static string[] P(params string[] names) => names;
    private static string[] V(params object[] values) => values.Select(v => v is bool b ? (b ? "true" : "false") : v.ToString()!).ToArray();

    public static readonly IReadOnlyList<SchemaRule> All = new SchemaRule[]
    {
        // ---- shapes ----
        new OnlyWhen(typeof(ShapeDefinition), P(nameof(ShapeDefinition.Nodes)), nameof(ShapeDefinition.Kind), V(ShapeKind.Literal)),
        new RequiredWhen(typeof(ShapeDefinition), nameof(ShapeDefinition.Nodes), nameof(ShapeDefinition.Kind), V(ShapeKind.Literal)),
        new OnlyWhen(typeof(ShapeDefinition), P(nameof(ShapeDefinition.Chain), nameof(ShapeDefinition.ChainFrom)), nameof(ShapeDefinition.Kind), V(ShapeKind.Generative)),
        new ForbidWhenSet(typeof(ShapeDefinition), nameof(ShapeDefinition.CopyOf),
            P(nameof(ShapeDefinition.Kind), nameof(ShapeDefinition.Count), nameof(ShapeDefinition.Chain), nameof(ShapeDefinition.ChainFrom), nameof(ShapeDefinition.Nodes), nameof(ShapeDefinition.Placement))),

        new ForbidWhenSet(typeof(ShapeDefinition), nameof(ShapeDefinition.ChainFrom), P(nameof(ShapeDefinition.Chain))),
        new OnlyWhenAt(typeof(ShapeDefinition), new[] { new[] { "Placement", "Rotation", "Angles" } }, new[] { "Kind" }, V(ShapeKind.Literal)),
        new ForbidAt(typeof(CombinationArrangementRules), new[] { new[] { "Spacing", "OnlyShapes" }, new[] { "Spacing", "ExcludeShapes" } },
            "applies to shapes only"),
        new ForbidAt(typeof(CombinationArrangementRules), new[] { new[] { "Candidates" } }, "components are placed by searching every position, so there is no best-of-N"),
        new Raw(typeof(ChainRules), nameof(ChainRules.ConnectionRules), "A rule's `backToStartChance` above 0 needs `allowClosedLoop` to be true.", () => new System.Text.Json.Nodes.JsonObject
        {
            ["if"] = new System.Text.Json.Nodes.JsonObject
            {
                ["properties"] = new System.Text.Json.Nodes.JsonObject { ["allowClosedLoop"] = new System.Text.Json.Nodes.JsonObject { ["const"] = true } },
                ["required"] = new System.Text.Json.Nodes.JsonArray("allowClosedLoop"),
            },
            ["else"] = new System.Text.Json.Nodes.JsonObject
            {
                ["properties"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["connectionRules"] = new System.Text.Json.Nodes.JsonObject
                    {
                        ["items"] = new System.Text.Json.Nodes.JsonObject
                        {
                            ["properties"] = new System.Text.Json.Nodes.JsonObject { ["backToStartChance"] = new System.Text.Json.Nodes.JsonObject { ["maximum"] = 0 } },
                        },
                    },
                },
            },
        }),

        // ---- options that depend on a number or on other blocks (written as json schema) ----
        new Raw(typeof(EdgeCurveConfig), nameof(EdgeCurveConfig.Tension), "Only used when `chanceToBeCurved` is above 0 (with 0 no edge is ever curved).", () => System.Text.Json.Nodes.JsonNode.Parse("""
            { "if": { "properties": { "chanceToBeCurved": { "exclusiveMinimum": 0 } }, "required": ["chanceToBeCurved"] },
              "else": { "properties": { "tension": false, "explicitJitterDegrees": false } } }
            """)!),
        new Raw(typeof(SpacingConfig), nameof(SpacingConfig.MaxDistanceMode), "Only used when `maxNodeDistance` is above 0 (0 = no maximum).", () => System.Text.Json.Nodes.JsonNode.Parse("""
            { "if": { "properties": { "maxNodeDistance": { "exclusiveMinimum": 0 } }, "required": ["maxNodeDistance"] },
              "else": { "properties": { "maxDistanceMode": false } } }
            """)!),
        new Raw(typeof(SimilarityConfig), nameof(SimilarityConfig.Basis), "Only used when `max` is below 1 (1 turns the similarity check off).", () => System.Text.Json.Nodes.JsonNode.Parse("""
            { "if": { "properties": { "max": { "minimum": 1 } }, "required": ["max"] },
              "then": { "properties": { "basis": false, "comparePosition": false } } }
            """)!),
        new Raw(typeof(QualityConfig), nameof(QualityConfig.AcceptPenalty), "Only used when a rule or pair rule has `onFail` Score.", () => System.Text.Json.Nodes.JsonNode.Parse("""
            { "if": { "anyOf": [
                { "properties": { "rules": { "contains": { "properties": { "onFail": { "const": "Score" } }, "required": ["onFail"] } } }, "required": ["rules"] },
                { "properties": { "pairRules": { "contains": { "properties": { "onFail": { "const": "Score" } }, "required": ["onFail"] } } }, "required": ["pairRules"] } ] },
              "else": { "properties": { "acceptPenalty": { "maximum": 0 } } } }
            """)!),
        new Raw(typeof(ShapeDefinition), nameof(ShapeDefinition.Arrangement), "On a shape, `crossing.mode` cannot be AnywhereButRespectStrict and `bias.includeScaleAndRotation` must be false (both are for combinations).", () => System.Text.Json.Nodes.JsonNode.Parse("""
            { "properties": { "arrangement": { "properties": {
                "crossing": { "properties": { "mode": { "not": { "const": "AnywhereButRespectStrict" } } } },
                "bias": { "properties": { "includeScaleAndRotation": { "const": false } } } } } } }
            """)!),

        // ---- placement ----
        new RequiredWhen(typeof(PlacementStart), nameof(PlacementStart.Origin), nameof(PlacementStart.Mode), V(PlacementMode.Fixed)),
        new OnlyWhen(typeof(PlacementStart), P(nameof(PlacementStart.Origin), nameof(PlacementStart.DiscardUsed)), nameof(PlacementStart.Mode), V(PlacementMode.Fixed)),
        new OnlyWhen(typeof(PlacementStart), P(nameof(PlacementStart.OnExistingNode), nameof(PlacementStart.OnPartnerNode)), nameof(PlacementStart.Mode), V(PlacementMode.RandomValid)),
        new Raw(typeof(PlacementConfig), nameof(PlacementConfig.Side), "Only used with `start.mode` RandomValid (Fixed and CenterAnchor always use the same origin).", () => System.Text.Json.Nodes.JsonNode.Parse("""
            { "if": { "properties": { "start": { "properties": { "mode": { "enum": ["Fixed", "CenterAnchor"] } }, "required": ["mode"] } }, "required": ["start"] },
              "then": { "properties": { "side": false } } }
            """)!),
        new OnlyWhen(typeof(SidePull), P(nameof(SidePull.Sides)), nameof(SidePull.Mode), V(SidePullMode.Prefer)),
        new OnlyWhen(typeof(SidePull), P(nameof(SidePull.Pick)), nameof(SidePull.Mode), V(SidePullMode.SetRandom)),
        new ExactlyOne(typeof(NodeRegion), P(nameof(NodeRegion.Points), nameof(NodeRegion.Box), nameof(NodeRegion.Pattern), nameof(NodeRegion.NodesOfShapes), nameof(NodeRegion.BoundsOfShapes), nameof(NodeRegion.NodesOfComponents), nameof(NodeRegion.NodesOfGlyph))),
        new OnlyWhen(typeof(AxisPattern), P(nameof(AxisPattern.Step), nameof(AxisPattern.Offset)), nameof(AxisPattern.Mode), V(AxisPatternMode.Every)),

        // ---- connection rules ----
        new OnlyWhen(typeof(ConnectionRuleConfig), P(nameof(ConnectionRuleConfig.Arc)), nameof(ConnectionRuleConfig.Direction), V(ConnectionDirection.Arc)),
        new OnlyWhen(typeof(ConnectionRuleConfig), P(nameof(ConnectionRuleConfig.OutlineNodes)), nameof(ConnectionRuleConfig.Direction), V(ConnectionDirection.Circle, ConnectionDirection.Arc)),
        new OnlyWhen(typeof(ConnectionRuleConfig), P(nameof(ConnectionRuleConfig.MinAngle), nameof(ConnectionRuleConfig.MaxAngle), nameof(ConnectionRuleConfig.AnglePreset), nameof(ConnectionRuleConfig.Curve), nameof(ConnectionRuleConfig.Split)),
            nameof(ConnectionRuleConfig.Direction), V(ConnectionDirection.Orthogonal, ConnectionDirection.Diagonal, ConnectionDirection.Both, ConnectionDirection.Any, ConnectionDirection.Angle)),
        new RequiredAnyWhen(typeof(ConnectionRuleConfig), P(nameof(ConnectionRuleConfig.MinAngle), nameof(ConnectionRuleConfig.MaxAngle), nameof(ConnectionRuleConfig.AnglePreset)),
            nameof(ConnectionRuleConfig.Direction), V(ConnectionDirection.Angle)),
        new OnlyWhen(typeof(ArcConfig), P(nameof(ArcConfig.Angles), nameof(ArcConfig.MinSweep), nameof(ArcConfig.MaxSweep)), nameof(ArcConfig.Anchor), V(ArcAnchor.None, ArcAnchor.Node)),
        new OnlyWhen(typeof(ArcConfig), P(nameof(ArcConfig.ContinueFrom)), nameof(ArcConfig.Anchor), V(ArcAnchor.Node)),
        new OnlyWhen(typeof(ArcConfig), P(nameof(ArcConfig.EndTolerance)), nameof(ArcConfig.ContinueFrom), V(ArcContinueFrom.ArcEnd)),
        new OnlyWhen(typeof(SelfSpacingRule), P(nameof(SelfSpacingRule.Lookback)), nameof(SelfSpacingRule.Mode), V(SelfSpacingMode.Adjacent)),
        new OnlyWhen(typeof(EdgeSplitConfig), P(nameof(EdgeSplitConfig.MaxDeviation)), nameof(EdgeSplitConfig.Snap), V(SplitSnap.Nearest)),
        new OnlyWhen(typeof(OutlineNodesConfig), P(nameof(OutlineNodesConfig.Angles), nameof(OutlineNodesConfig.Snap)), nameof(OutlineNodesConfig.Mode), V(OutlineNodeMode.AtAngles)),
        new OnlyWhen(typeof(OutlineNodesConfig), P(nameof(OutlineNodesConfig.Tolerance)), nameof(OutlineNodesConfig.Mode), V(OutlineNodeMode.AtAngles, OutlineNodeMode.EveryLatticePoint)),
        new OnlyWhen(typeof(EdgeCurveConfig), P(nameof(EdgeCurveConfig.ExplicitJitterDegrees)), nameof(EdgeCurveConfig.Mode), V(CurveMode.Explicit, CurveMode.Hybrid)),

        // ---- arrangement ----
        new OnlyWhen(typeof(CrossingConfig), P(nameof(CrossingConfig.NodeTolerance), nameof(CrossingConfig.NodesOfShapes)), nameof(CrossingConfig.Mode), V(CrossingMode.NodesOnly)),
        new OnlyWhen(typeof(SpacingConfig),
            P(nameof(SpacingConfig.MinCenterDistance), nameof(SpacingConfig.MinNodeDistance), nameof(SpacingConfig.MaxNodeDistance), nameof(SpacingConfig.MinEdgeDistance),
              nameof(SpacingConfig.OnlyShapes), nameof(SpacingConfig.ExcludeShapes), nameof(SpacingConfig.MaxDistanceMode)),
            nameof(SpacingConfig.Enabled), V(true)),
        new NotTogether(typeof(SpacingConfig), P(nameof(SpacingConfig.OnlyShapes), nameof(SpacingConfig.ExcludeShapes))),
        new OnlyWhen(typeof(ComponentPlacementRule), P(nameof(ComponentPlacementRule.Sides)), nameof(ComponentPlacementRule.Mode), V(ComponentPlacementMode.Prefer)),
        new OnlyWhen(typeof(ComponentPlacementRule), P(nameof(ComponentPlacementRule.Pick), nameof(ComponentPlacementRule.Scope)), nameof(ComponentPlacementRule.Mode), V(ComponentPlacementMode.SetRandom)),
        new OnlyWhen(typeof(ComponentPlacementRule), P(nameof(ComponentPlacementRule.Offset), nameof(ComponentPlacementRule.IfBlocked)), nameof(ComponentPlacementRule.Mode), V(ComponentPlacementMode.Fixed)),
        new OnlyWhen(typeof(ComponentPlacementRule), P(nameof(ComponentPlacementRule.Nodes), nameof(ComponentPlacementRule.Anchor)), nameof(ComponentPlacementRule.Mode), V(ComponentPlacementMode.Prefer, ComponentPlacementMode.SetRandom)),
        new OnlyWhen(typeof(CombinationConfig), P(nameof(CombinationConfig.AllowedScales)), nameof(CombinationConfig.AllowScale), V(true)),
        new OnlyWhen(typeof(CombinationConfig), P(nameof(CombinationConfig.Rotation)), nameof(CombinationConfig.AllowRotate), V(true)),

        // ---- copies ----
        new OnlyWhen(typeof(CopyConfig), P(nameof(CopyConfig.Flips)), nameof(CopyConfig.Flip), V(CopyFlip.Random)),
        new OnlyWhen(typeof(CopyConfig), P(nameof(CopyConfig.Auto)), nameof(CopyConfig.Flip), V(CopyFlip.Auto)),
        new OnlyWhen(typeof(CopyAutoConfig), P(nameof(CopyAutoConfig.Axis)), nameof(CopyAutoConfig.By), V(CopyAutoBy.LongestStraight)),
        new OnlyWhen(typeof(CopyAutoConfig), P(nameof(CopyAutoConfig.Toward)), nameof(CopyAutoConfig.By), V(CopyAutoBy.CenterWeight)),
        new OnlyWhen(typeof(CopyConfig), P(nameof(CopyConfig.SelfSpacingIgnoresOriginal)), nameof(CopyConfig.RespectSelfSpacing), V(true)),

        // ---- derivations: one set of options per op ----
        new OnlyWhen(typeof(DerivationStep), P(nameof(DerivationStep.Amount)), nameof(DerivationStep.Op), V(DerivationOp.DeleteNodes, DerivationOp.DeleteEdges, DerivationOp.Keep, DerivationOp.Nudge, DerivationOp.SplitEdges)),
        new OnlyWhen(typeof(DerivationStep), P(nameof(DerivationStep.Target)), nameof(DerivationStep.Op), V(DerivationOp.DeleteNodes, DerivationOp.Nudge)),
        new OnlyWhen(typeof(DerivationStep), P(nameof(DerivationStep.Mode)), nameof(DerivationStep.Op), V(DerivationOp.DeleteNodes)),
        new OnlyWhen(typeof(DerivationStep), P(nameof(DerivationStep.ConnectionRules), nameof(DerivationStep.BridgeMaxMoves)), nameof(DerivationStep.Mode), V(DeleteNodeMode.Bridge)),
        new OnlyWhen(typeof(DerivationStep), P(nameof(DerivationStep.RemoveOrphanNodes), nameof(DerivationStep.KeepAtLeast), nameof(DerivationStep.KeepConnected)), nameof(DerivationStep.Op),
            V(DerivationOp.DeleteNodes, DerivationOp.DeleteEdges, DerivationOp.Keep)),
        new OnlyWhen(typeof(DerivationStep), P(nameof(DerivationStep.Shapes), nameof(DerivationStep.Optional)), nameof(DerivationStep.Op), V(DerivationOp.Add)),
        new OnlyWhen(typeof(DerivationStep), P(nameof(DerivationStep.Factors)), nameof(DerivationStep.Op), V(DerivationOp.Scale)),
        new OnlyWhen(typeof(DerivationStep), P(nameof(DerivationStep.Axis)), nameof(DerivationStep.Op), V(DerivationOp.Mirror)),
        new OnlyWhen(typeof(DerivationStep), P(nameof(DerivationStep.Degrees)), nameof(DerivationStep.Op), V(DerivationOp.Rotate)),
        new OnlyWhen(typeof(DerivationStep), P(nameof(DerivationStep.Distance)), nameof(DerivationStep.Op), V(DerivationOp.Nudge)),
        new OnlyWhen(typeof(DerivationStep), P(nameof(DerivationStep.Parts), nameof(DerivationStep.Distribution), nameof(DerivationStep.Snap), nameof(DerivationStep.MinEdgeLength)), nameof(DerivationStep.Op), V(DerivationOp.SplitEdges)),
        new OnlyWhen(typeof(DerivationStep), P(nameof(DerivationStep.MaxDeviation)), nameof(DerivationStep.Snap), V(SplitSnap.Nearest)),
        new ExactlyOne(typeof(DerivationAmount), P(nameof(DerivationAmount.Percent), nameof(DerivationAmount.Count))),

        // ---- quality ----
        new OnlyWhen(typeof(QualityRule), P(nameof(QualityRule.Window), nameof(QualityRule.Max)), nameof(QualityRule.Measure), V(QualityMeasure.Count, QualityMeasure.Similar)),
        new OnlyWhen(typeof(QualityRule), P(nameof(QualityRule.MinDistance)), nameof(QualityRule.Measure), V(QualityMeasure.Distance)),
        new OnlyWhen(typeof(QualityRule), P(nameof(QualityRule.Similarity), nameof(QualityRule.SimilarityThreshold), nameof(QualityRule.StrokeWidth)), nameof(QualityRule.Measure), V(QualityMeasure.Similar)),
        new OnlyWhen(typeof(QualityRule), P(nameof(QualityRule.Weight)), nameof(QualityRule.OnFail), V(QualityAction.Score)),
        new OnlyWhen(typeof(QualityRule), P(nameof(QualityRule.Redo)), nameof(QualityRule.OnFail), V(QualityAction.Reject)),
        new OnlyWhen(typeof(CountDiffersConfig), P(nameof(CountDiffersConfig.Factor)), nameof(CountDiffersConfig.Action), V(CountDiffersAction.Loosen)),

        // ---- typeface and joins ----
        new OnlyWhen(typeof(TypefaceConfig), P(nameof(TypefaceConfig.Rectangular)), nameof(TypefaceConfig.Kind), V(TypefaceKind.Rectangular)),
        new OnlyWhen(typeof(TypefaceConfig), P(nameof(TypefaceConfig.ThickEnds)), nameof(TypefaceConfig.Kind), V(TypefaceKind.ThickEnds)),
        new OnlyWhen(typeof(TypefaceConfig), P(nameof(TypefaceConfig.Brush)), nameof(TypefaceConfig.Kind), V(TypefaceKind.Brush)),
        new OnlyWhen(typeof(RectangularTypefaceConfig), P(nameof(RectangularTypefaceConfig.CapLevel), nameof(RectangularTypefaceConfig.CapLevelFlat)), nameof(RectangularTypefaceConfig.Cap), V(CapStyle.Level)),
        new OnlyWhen(typeof(ThickEndsTypefaceConfig), P(nameof(ThickEndsTypefaceConfig.BallScale)), nameof(ThickEndsTypefaceConfig.Terminal), V(TerminalStyle.Ball)),
        new OnlyWhen(typeof(BrushTypefaceConfig), P(nameof(BrushTypefaceConfig.TipWidth), nameof(BrushTypefaceConfig.TaperSharpness)), nameof(BrushTypefaceConfig.Mode), V(BrushMode.Taper)),
        new OnlyWhen(typeof(BrushTypefaceConfig),
            P(nameof(BrushTypefaceConfig.NibAngleDegrees), nameof(BrushTypefaceConfig.ThinWidth), nameof(BrushTypefaceConfig.NibRoundness), nameof(BrushTypefaceConfig.NibAngleWobble),
              nameof(BrushTypefaceConfig.NibPressure), nameof(BrushTypefaceConfig.NibLeadIn)),
            nameof(BrushTypefaceConfig.Mode), V(BrushMode.BroadNib)),
        new OnlyWhen(typeof(BrushTypefaceConfig),
            P(nameof(BrushTypefaceConfig.PressureVariation), nameof(BrushTypefaceConfig.EdgeWobble), nameof(BrushTypefaceConfig.Raggedness), nameof(BrushTypefaceConfig.StartBlobScale)),
            nameof(BrushTypefaceConfig.Mode), V(BrushMode.Ink)),
        new OnlyWhen(typeof(JoinSideConfig), P(nameof(JoinSideConfig.MiterLimit)), nameof(JoinSideConfig.Style), V(JoinStyle.Miter)),
        new OnlyWhen(typeof(JoinSideConfig), P(nameof(JoinSideConfig.Size)), nameof(JoinSideConfig.Style), V(JoinStyle.Bevel, JoinStyle.Round)),
        new OnlyWhen(typeof(JoinSideConfig), P(nameof(JoinSideConfig.Axis)), nameof(JoinSideConfig.Style), V(JoinStyle.Level)),
    };
}
