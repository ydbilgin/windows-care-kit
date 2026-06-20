using System.Text.Json;

namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>Thrown when a recipe JSON document is structurally or semantically invalid (fail-closed).</summary>
public sealed class RecipeValidationException : Exception
{
    public RecipeValidationException(string message) : base(message) { }
}

/// <summary>
/// Strict, fail-closed loader/validator for <see cref="MigrationRecipe"/> JSON (decision §"strict loader").
/// Unlike the tolerant <c>ManifestLoader</c>, this REJECTS:
/// <list type="bullet">
/// <item>any unknown top-level/nested field (no silent drop — a typo'd or smuggled field fails the whole recipe);</item>
/// <item>any unknown enum value (<c>knownFolder</c>, <c>portabilityClass</c>, <c>restore.strategy</c>, <c>restore.phase</c>);</item>
/// <item>an unsupported <c>schemaVersion</c>, a missing required field, or an empty id.</item>
/// </list>
/// Because recipes can ship from L4 user overrides (decision §"4 katman"), a strict validator is the line
/// that keeps a malformed/hostile recipe from ever reaching the resolver.
/// </summary>
public static class MigrationRecipeLoader
{
    public const int SupportedSchemaVersion = 1;

    /// <summary>Load + validate a single recipe from a JSON string. Throws <see cref="RecipeValidationException"/>.</summary>
    public static MigrationRecipe Load(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException ex)
        {
            throw new RecipeValidationException($"recipe is not valid JSON: {ex.Message}");
        }

        using (doc)
        {
            JsonElement root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw new RecipeValidationException("recipe root must be a JSON object");

            RejectUnknownFields(root, "(root)",
                "schemaVersion", "id", "displayName", "category",
                "detect", "items", "exclude", "secretRule", "portabilityClass", "restore");

            int schemaVersion = RequireInt(root, "schemaVersion");
            if (schemaVersion != SupportedSchemaVersion)
                throw new RecipeValidationException($"unsupported schemaVersion {schemaVersion} (expected {SupportedSchemaVersion})");

            string id = RequireNonEmptyString(root, "id");
            string displayName = RequireNonEmptyString(root, "displayName");
            string category = OptionalString(root, "category") ?? string.Empty;

            RecipeDetect detect = ParseDetect(RequireObject(root, "detect"));
            IReadOnlyList<RecipeItem> items = ParseItems(RequireArray(root, "items"), id);
            IReadOnlyList<string> exclude = ParseStringArray(root, "exclude");
            string secretRule = OptionalString(root, "secretRule") ?? "global";
            if (!string.Equals(secretRule, "global", StringComparison.OrdinalIgnoreCase))
                throw new RecipeValidationException($"unknown secretRule '{secretRule}' (only 'global' is supported)");
            PortabilityClass portability = ParsePortability(RequireNonEmptyString(root, "portabilityClass"));
            RecipeRestore restore = ParseRestore(RequireObject(root, "restore"));

            return new MigrationRecipe(
                schemaVersion, id, displayName, category, detect, items, exclude, secretRule, portability, restore);
        }
    }

    private static RecipeDetect ParseDetect(JsonElement el)
    {
        RejectUnknownFields(el, "detect", "knownFolder", "path", "exists");
        KnownFolder kf = ParseKnownFolder(RequireNonEmptyString(el, "knownFolder"));
        string path = RequireNonEmptyString(el, "path");
        bool exists = el.TryGetProperty("exists", out JsonElement e) ? RequireBool(e, "detect.exists") : true;
        return new RecipeDetect(kf, path, exists);
    }

    private static IReadOnlyList<RecipeItem> ParseItems(JsonElement arr, string recipeId)
    {
        var list = new List<RecipeItem>();
        foreach (JsonElement el in arr.EnumerateArray())
        {
            if (el.ValueKind != JsonValueKind.Object)
                throw new RecipeValidationException("each item must be a JSON object");
            RejectUnknownFields(el, "items[]", "path", "include", "exclude");
            string path = RequireNonEmptyString(el, "path");
            list.Add(new RecipeItem(path, ParseStringArray(el, "include"), ParseStringArray(el, "exclude")));
        }
        if (list.Count == 0)
            throw new RecipeValidationException($"recipe '{recipeId}' has no items");
        return list;
    }

    private static RecipeRestore ParseRestore(JsonElement el)
    {
        RejectUnknownFields(el, "restore", "strategy", "phase", "preconditions");
        RestoreStrategy strategy = ParseStrategy(RequireNonEmptyString(el, "strategy"));
        RestorePhase phase = ParsePhase(RequireNonEmptyString(el, "phase"));
        return new RecipeRestore(strategy, phase, ParseStringArray(el, "preconditions"));
    }

    // ---- enum parsing (fail-closed) --------------------------------------------------------

    private static KnownFolder ParseKnownFolder(string s) => s.ToLowerInvariant() switch
    {
        "userprofile" => KnownFolder.UserProfile,
        "appdata" => KnownFolder.AppData,
        "localappdata" => KnownFolder.LocalAppData,
        _ => throw new RecipeValidationException($"unknown knownFolder '{s}'"),
    };

    private static PortabilityClass ParsePortability(string s) => s.ToLowerInvariant() switch
    {
        "profile-relative" => PortabilityClass.ProfileRelative,
        "machine-locked" => PortabilityClass.MachineLocked,
        "partial" => PortabilityClass.Partial,
        _ => throw new RecipeValidationException($"unknown portabilityClass '{s}'"),
    };

    private static RestoreStrategy ParseStrategy(string s) => s.ToLowerInvariant() switch
    {
        "config-write" => RestoreStrategy.ConfigWrite,
        "merge-after-install" => RestoreStrategy.MergeAfterInstall,
        "replace" => RestoreStrategy.Replace,
        _ => throw new RecipeValidationException($"unknown restore.strategy '{s}'"),
    };

    private static RestorePhase ParsePhase(string s) => s.ToLowerInvariant() switch
    {
        "install" => RestorePhase.Install,
        "firstrunseed" or "first-run-seed" => RestorePhase.FirstRunSeed,
        "configwrite" or "config-write" => RestorePhase.ConfigWrite,
        _ => throw new RecipeValidationException($"unknown restore.phase '{s}'"),
    };

    // ---- field helpers ---------------------------------------------------------------------

    private static void RejectUnknownFields(JsonElement obj, string where, params string[] allowed)
    {
        var set = new HashSet<string>(allowed, StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty p in obj.EnumerateObject())
            if (!set.Contains(p.Name))
                throw new RecipeValidationException($"unknown field '{p.Name}' in {where}");
    }

    private static JsonElement RequireObject(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.Object)
            throw new RecipeValidationException($"missing or non-object field '{name}'");
        return el;
    }

    private static JsonElement RequireArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.Array)
            throw new RecipeValidationException($"missing or non-array field '{name}'");
        return el;
    }

    private static int RequireInt(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.Number || !el.TryGetInt32(out int v))
            throw new RecipeValidationException($"missing or non-integer field '{name}'");
        return v;
    }

    private static bool RequireBool(JsonElement el, string name)
    {
        if (el.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new RecipeValidationException($"field '{name}' must be a boolean");
        return el.GetBoolean();
    }

    private static string RequireNonEmptyString(JsonElement parent, string name)
    {
        string? s = OptionalString(parent, name);
        if (string.IsNullOrWhiteSpace(s))
            throw new RecipeValidationException($"missing or empty field '{name}'");
        return s;
    }

    private static string? OptionalString(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement el))
            return null;
        if (el.ValueKind != JsonValueKind.String)
            throw new RecipeValidationException($"field '{name}' must be a string");
        return el.GetString();
    }

    private static IReadOnlyList<string> ParseStringArray(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement el))
            return Array.Empty<string>();
        if (el.ValueKind != JsonValueKind.Array)
            throw new RecipeValidationException($"field '{name}' must be an array");
        var list = new List<string>();
        foreach (JsonElement item in el.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new RecipeValidationException($"field '{name}' must contain only strings");
            string? s = item.GetString();
            if (!string.IsNullOrWhiteSpace(s))
                list.Add(s);
        }
        return list;
    }
}
