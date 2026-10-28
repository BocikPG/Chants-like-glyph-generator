using System.Text.Json.Nodes;

/// <summary>Test-only helper: rewrites a config written with the previous option names into the current layout (run block, render.canvas /
/// render.debug / render.transform, quality.similarity, placement.start / area / rotation / retries, retries everywhere). The product itself
/// knows nothing about old names; the self-check's many inline configs are kept in the old spelling and pass through this.
/// Idempotent: a config already in the new layout comes back unchanged.</summary>
internal static class LayoutUpgrade
{
    public static string Apply(string json)
    {
        var root = JsonNode.Parse(json) as JsonObject;
        if (root is null) return json;

        var run = root["run"] as JsonObject ?? new JsonObject();
        Move(root, "rootSeed", run, "seed");
        Move(root, "outputDirectory", run, "outputDirectory");
        Move(root, "maxParallelism", run, "maxParallelism");

        if (root["languages"] is JsonArray languages)
            foreach (var node in languages)
                if (node is JsonObject language) Language(language, run);

        if (run.Count > 0 && root["run"] is null)
        {
            // keep "run" first for readability
            var entries = root.ToList();
            foreach (var (k, _) in entries) root.Remove(k);
            var rebuilt = new JsonObject();
            foreach (var (k, v) in entries) if (k == "$schema") rebuilt[k] = v;
            rebuilt["run"] = run;
            foreach (var (k, v) in entries) if (k != "$schema") rebuilt[k] = v;
            root = rebuilt;
        }

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static void Language(JsonObject language, JsonObject run)
    {
        var render = language["render"] as JsonObject;
        if (language["canvas"] is not null || render is not null)
        {
            render ??= new JsonObject();
            language["render"] = render;
            Move(language, "canvas", render, "canvas");

            var debug = render["debug"] as JsonObject ?? new JsonObject();
            Move(render, "debugShowNodes", debug, "showNodes");
            Move(render, "debugColorComponents", debug, "colorComponents");
            Move(render, "debugColorParts", debug, "colorParts");
            Move(render, "debugColorShapes", debug, "colorShapes");
            Move(render, "debugShowUndrawn", debug, "showUndrawn");
            if (debug.Count > 0) render["debug"] = debug;

            var transform = render["transform"] as JsonObject ?? new JsonObject();
            Move(render, "rotateDegrees", transform, "rotateDegrees");
            Move(render, "rotateOptions", transform, "rotateOptions");
            if (transform.Count > 0) render["transform"] = transform;

            if (render["contactSheet"] is JsonNode sheet)
            {
                render.Remove("contactSheet");
                if (run["contactSheet"] is null) run["contactSheet"] = sheet;
            }
        }

        if (language["usageSizeBias"] is JsonObject bias)
        {
            Rename(bias, "affectSubSymbolCount", "affectCount");
            Rename(bias, "affectSubSymbolLength", "affectLength");
        }

        if (language["combination"] is JsonObject combination) Rename(combination, "maxRetriesPerRecipe", "retries");

        if (language["quality"] is JsonObject quality)
        {
            var similarity = quality["similarity"] as JsonObject ?? new JsonObject();
            Move(quality, "maxSimilarity", similarity, "max");
            Move(quality, "compositeMaxSimilarity", similarity, "compositeMax");
            Move(quality, "similarityBasis", similarity, "basis");
            Move(quality, "comparePosition", similarity, "comparePosition");
            if (similarity.Count > 0) quality["similarity"] = similarity;
            Rename(quality, "maxRegenerationAttempts", "retries");
        }

        if (language["shapes"] is JsonArray shapes)
            foreach (var node in shapes)
                if (node is JsonObject shape) Shape(shape);
    }

    private static void Shape(JsonObject shape)
    {
        // spacing.ignoreStartNode lives with the start rule now
        bool ignoreStart = false;
        if (shape["arrangement"] is JsonObject arrangement && arrangement["spacing"] is JsonObject spacing && spacing["ignoreStartNode"] is JsonNode ig)
        {
            ignoreStart = ig.GetValue<bool>();
            spacing.Remove("ignoreStartNode");
        }

        if (shape["placement"] is JsonObject p)
        {
            var start = p["start"] as JsonObject ?? new JsonObject();
            Move(p, "mode", start, "mode");
            Move(p, "fixedOrigin", start, "origin");
            Move(p, "discardUsedOrigins", start, "discardUsed");

            bool on = p["startOnExistingNode"] is JsonNode so && so.GetValue<bool>();
            p.Remove("startOnExistingNode");
            var chance = Take(p, "startOnExistingNodeChance");
            var shapesNode = Take(p, "startOnShapes");
            if (on)
            {
                var existing = new JsonObject();
                if (chance is not null) existing["chance"] = chance;
                if (shapesNode is not null) existing["shapes"] = shapesNode;
                if (ignoreStart) existing["ignoreStartNode"] = true;
                start["onExistingNode"] = existing;
            }
            if (start.Count > 0) p["start"] = start;

            var area = p["area"] as JsonObject ?? new JsonObject();
            Move(p, "bounds", area, "bounds");
            Move(p, "nodes", area, "nodes");
            if (area.Count > 0) p["area"] = area;

            var rotation = p["rotation"] as JsonObject ?? new JsonObject();
            Move(p, "allowRotationOnPlace", rotation, "allowed");
            Move(p, "rotations", rotation, "angles");
            if (rotation.Count > 0) p["rotation"] = rotation;

            var retries = p["retries"] as JsonObject ?? new JsonObject();
            Move(p, "maxPlacementAttempts", retries, "placement");
            Move(p, "redoShape", retries, "shape");
            Move(p, "redrawGlyph", retries, "glyph");
            if (retries.Count > 0) p["retries"] = retries;
        }

        if (shape["copyOf"] is JsonObject copy)
        {
            var retries = copy["retries"] as JsonObject ?? new JsonObject();
            Move(copy, "regenerateBase", retries, "base");
            Move(copy, "redrawGlyph", retries, "glyph");
            if (retries.Count > 0) copy["retries"] = retries;
        }
    }

    private static JsonNode? Take(JsonObject from, string key)
    {
        if (!from.TryGetPropertyValue(key, out var value)) return null;
        from.Remove(key);
        return value;
    }

    private static void Move(JsonObject from, string key, JsonObject to, string newKey)
    {
        if (Take(from, key) is { } value) to[newKey] = value;
    }

    private static void Rename(JsonObject o, string key, string newKey)
    {
        if (Take(o, key) is { } value) o[newKey] = value;
    }
}
