using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace GlyphGen.Core.Config;

/// <summary>
/// Converts a config written for the pre-shapes layout (language-level subSymbol*/connectionRules/
/// guaranteedMotifs/allowSymbolsToCross/... and combination.allowComponentsToCross/bias) into the
/// current one. The result generates the SAME glyphs: motifs become shapes without a count, the
/// free-form sub-symbols become one shape with a count, and each gets a seedLabel naming the old
/// random stream. Language-wide chain flags are copied onto every generative shape (the old code
/// shared them silently), and selfSpacing is dropped when subSymbolSpacing was disabled (it was
/// inactive then). Values the old config left out keep their defaults, which did not change.
/// </summary>
public static class ConfigMigrator
{
    private static readonly string[] LegacyLanguageKeys =
    {
        "subSymbolCount", "subSymbolLength", "connectionRules", "connectionWeighting", "allowNodeRevisit",
        "allowEdgeRevisit", "allowClosedLoop", "closedLoopWeight", "allowSymbolsToCross", "guaranteedMotifs",
        "subSymbolPlacement", "subSymbolSpacing",
    };

    /// <summary>Returns the migrated JSON, or null when the config is already in the new layout.</summary>
    public static string? Migrate(string json)
    {
        var root = JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip })
            as JsonObject ?? throw new JsonException("The config must be a JSON object.");

        bool changed = false;
        if (root["languages"] is JsonArray languages)
        {
            foreach (var node in languages)
                if (node is JsonObject language) changed |= MigrateLanguage(language);
        }

        if (!changed) return null;
        return root.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
    }

    private static bool MigrateLanguage(JsonObject language)
    {
        var combination = Get(language, "combination") as JsonObject;
        bool legacyLanguage = LegacyLanguageKeys.Any(k => Find(language, k) is not null);
        bool legacyCombination = combination is not null
            && (Find(combination, "allowComponentsToCross") is not null || Find(combination, "bias") is not null);
        if (!legacyLanguage && !legacyCombination) return false;

        var spacing = Get(language, "subSymbolSpacing") as JsonObject;
        bool spacingEnabled = spacing is not null && Get(spacing, "enabled") is JsonValue v && v.TryGetValue(out bool on) && on;
        var spacingBlock = spacing is null ? null : CloneWithout(spacing, "selfSpacing");
        var selfSpacing = spacingEnabled && spacing is not null ? Get(spacing, "selfSpacing") : null;
        bool crossing = Get(language, "allowSymbolsToCross") is JsonValue c && c.TryGetValue(out bool cross) && cross;

        JsonObject Arrangement()
        {
            var a = new JsonObject();
            if (crossing) a["allowCrossing"] = true;
            if (spacingBlock is not null && spacingBlock.Count > 0) a["spacing"] = spacingBlock.DeepClone();
            return a;
        }

        JsonObject ChainFlags(JsonObject chain)
        {
            foreach (var key in new[] { "connectionWeighting", "allowNodeRevisit", "allowEdgeRevisit" })
                if (Get(language, key) is { } value) chain[key] = value.DeepClone();
            if (selfSpacing is not null) chain["selfSpacing"] = selfSpacing.DeepClone();
            return chain;
        }

        var shapes = new JsonArray();
        var ids = new HashSet<string>(StringComparer.Ordinal);

        if (Get(language, "guaranteedMotifs") is JsonArray motifs)
        {
            foreach (var item in motifs)
            {
                if (item is not JsonObject motif) continue;
                string id = Get(motif, "id") is JsonValue idv && idv.TryGetValue(out string? s) ? s ?? "" : "";
                ids.Add(id);

                var shape = new JsonObject { ["id"] = id };
                string kind = Get(motif, "type") is JsonValue tv && tv.TryGetValue(out string? t) ? t ?? "Generative" : "Generative";
                shape["kind"] = kind;
                shape["seedLabel"] = "motif-" + id;

                if (kind == "Literal")
                {
                    if (Get(Get(motif, "literal") as JsonObject, "nodes") is { } nodes) shape["nodes"] = nodes.DeepClone();
                }
                else if (Get(motif, "generative") as JsonObject is { } generative)
                {
                    var chain = new JsonObject();
                    if (Get(generative, "lengthRange") is { } len) chain["length"] = len.DeepClone();
                    if (Get(generative, "connectionRules") is { } rules) chain["connectionRules"] = rules.DeepClone();
                    foreach (var key in new[] { "allowClosedLoop", "closedLoopWeight" })
                        if (Get(generative, key) is { } value) chain[key] = value.DeepClone();
                    shape["chain"] = ChainFlags(chain);
                }

                if (Get(motif, "placement") is { } placement) shape["placement"] = placement.DeepClone();
                var arrangement = Arrangement();
                if (arrangement.Count > 0) shape["arrangement"] = arrangement;
                shapes.Add(shape);
            }
        }

        // The free-form sub-symbols always become a shape: the old generator rolled a count for
        // them unconditionally, and that roll is part of the random stream.
        string freeId = "strokes";
        while (!ids.Add(freeId)) freeId += "_";
        var free = new JsonObject { ["id"] = freeId, ["seedLabel"] = "subsymbol" };
        free["count"] = Get(language, "subSymbolCount")?.DeepClone() ?? new JsonObject { ["min"] = 1, ["max"] = 1 };
        var freeChain = new JsonObject();
        if (Get(language, "subSymbolLength") is { } length) freeChain["length"] = length.DeepClone();
        if (Get(language, "connectionRules") is { } connection) freeChain["connectionRules"] = connection.DeepClone();
        foreach (var key in new[] { "allowClosedLoop", "closedLoopWeight" })
            if (Get(language, key) is { } value) freeChain[key] = value.DeepClone();
        free["chain"] = ChainFlags(freeChain);
        if (Get(language, "subSymbolPlacement") is { } freePlacement) free["placement"] = freePlacement.DeepClone();
        var freeArrangement = Arrangement();
        if (freeArrangement.Count > 0) free["arrangement"] = freeArrangement;
        shapes.Add(free);

        // Combination: allowComponentsToCross and bias move into combination.arrangement, and the
        // old shared spacing applied to components as well.
        if (combination is not null)
        {
            var comboArrangement = new JsonObject();
            if (Get(combination, "allowComponentsToCross") is { } cc) comboArrangement["allowCrossing"] = cc.DeepClone();
            if (spacingBlock is not null && spacingBlock.Count > 0) comboArrangement["spacing"] = spacingBlock.DeepClone();
            if (Get(combination, "bias") is { } bias) comboArrangement["bias"] = bias.DeepClone();
            Remove(combination, "allowComponentsToCross");
            Remove(combination, "bias");
            if (comboArrangement.Count > 0) combination["arrangement"] = comboArrangement;
        }
        else if (spacingBlock is not null && spacingBlock.Count > 0)
        {
            language["combination"] = new JsonObject { ["arrangement"] = new JsonObject { ["spacing"] = spacingBlock.DeepClone() } };
        }

        // Rebuild the language so "shapes" sits where the first old option was.
        var entries = language.Select(kv => (kv.Key, Node: kv.Value)).ToList();
        foreach (var (key, _) in entries) language.Remove(key);
        bool placed = false;
        foreach (var (key, node) in entries)
        {
            if (LegacyLanguageKeys.Any(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)))
            {
                if (!placed) { language["shapes"] = shapes; placed = true; }
                continue;
            }
            language[key] = node;
        }
        if (!placed) language["shapes"] = shapes;
        return true;
    }

    private static JsonNode? Get(JsonObject? obj, string key) => obj is null ? null : Find(obj, key)?.Value;

    private static KeyValuePair<string, JsonNode?>? Find(JsonObject obj, string key)
    {
        foreach (var kv in obj)
            if (string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase)) return kv;
        return null;
    }

    private static void Remove(JsonObject obj, string key)
    {
        if (Find(obj, key) is { } kv) obj.Remove(kv.Key);
    }

    private static JsonObject CloneWithout(JsonObject obj, string key)
    {
        var copy = new JsonObject();
        foreach (var kv in obj)
            if (!string.Equals(kv.Key, key, StringComparison.OrdinalIgnoreCase))
                copy[kv.Key] = kv.Value?.DeepClone();
        return copy;
    }
}
