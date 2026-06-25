using System.Text.Json;
using WindowsCareKit.Core.Modules.Migration.Conversion;

namespace WindowsCareKit.Core.Modules.Migration;

public sealed record CommunityRecipeCandidate(string Name, string Json);

public sealed record CommunityRecipeRejection(string Id, string Reason);

public sealed record CommunityRecipeLoadResult(
    IReadOnlyList<MigrationRecipe> Loaded,
    IReadOnlyList<CommunityRecipeRejection> Rejected);

/// <summary>
/// Loads caller-supplied community recipes from a separate directory and applies the same strict schema and
/// capability honesty gates used for trusted recipes. Bad files are isolated and reported; they do not stop
/// other recipes in the same pack from loading.
/// </summary>
public static class CommunityRecipeSource
{
    public static CommunityRecipeLoadResult LoadFromDirectory(
        string directoryPath,
        IEnumerable<MigrationRecipe> trustedRecipes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directoryPath);
        ArgumentNullException.ThrowIfNull(trustedRecipes);

        if (!Directory.Exists(directoryPath))
            return new CommunityRecipeLoadResult([], []);

        CommunityRecipeCandidate[] candidates = Directory
            .EnumerateFiles(directoryPath, "*.json", SearchOption.TopDirectoryOnly)
            .OrderBy(path => path, StringComparer.Ordinal)
            .Select(path => new CommunityRecipeCandidate(path, File.ReadAllText(path)))
            .ToArray();

        return LoadCandidates(candidates, trustedRecipes.Select(r => r.Id));
    }

    public static CommunityRecipeLoadResult LoadCandidates(
        IEnumerable<CommunityRecipeCandidate> candidates,
        IEnumerable<string> trustedRecipeIds)
    {
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(trustedRecipeIds);

        var trustedIds = new HashSet<string>(trustedRecipeIds, StringComparer.Ordinal);
        var loaded = new List<MigrationRecipe>();
        var rejected = new List<CommunityRecipeRejection>();

        foreach (CommunityRecipeCandidate candidate in candidates)
        {
            string rejectionId = CandidateId(candidate);

            try
            {
                MigrationRecipe recipe = MigrationRecipeLoader.Load(candidate.Json) with
                {
                    CatalogTier = CatalogTier.Community,
                };
                rejectionId = recipe.Id;

                // Feed the gate the DECLARED tier (raw JSON), NOT the loader's post-cap RestoreTier: the loader
                // force-caps machine-locked/non-profile recipes to InventoryOnly, which would short-circuit the
                // gate to Ok and silently accept an over-claimer. The null-fallback to the capped tier is only
                // reachable for tier-less v1/v2 recipes (v3 requires restoreTier) — those make no claim to
                // over-state, so accepting at the capped tier is honest, not a hole.
                MigrationRecipe gateRecipe = recipe with
                {
                    RestoreTier = TryReadDeclaredRestoreTier(candidate.Json) ?? recipe.RestoreTier,
                };
                RecipeCapabilityGateResult gate = RecipeCapabilityHonestyGate.Evaluate(gateRecipe);
                if (gate is RecipeCapabilityGateResult.Violation violation)
                {
                    rejected.Add(new CommunityRecipeRejection(recipe.Id, violation.Reason));
                    continue;
                }

                if (trustedIds.Contains(recipe.Id))
                {
                    rejected.Add(new CommunityRecipeRejection(
                        recipe.Id,
                        $"community recipe id '{recipe.Id}' collides with a trusted builtin recipe"));
                    continue;
                }

                loaded.Add(recipe);
            }
            catch (RecipeValidationException ex)
            {
                rejected.Add(new CommunityRecipeRejection(rejectionId, ex.Message));
            }
            catch (JsonException ex)
            {
                rejected.Add(new CommunityRecipeRejection(rejectionId, $"recipe is not valid JSON: {ex.Message}"));
            }
        }

        loaded.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return new CommunityRecipeLoadResult(loaded, rejected);
    }

    /// <summary>
    /// Build a single tier-tagged view of trusted builtins + already-accepted community recipes for a future
    /// detection/UI slice. CONTRACT: <paramref name="trustedRecipes"/> MUST be the vetted builtin set
    /// (<see cref="BuiltinRecipeSource.LoadAll"/>) — it is re-stamped <see cref="CatalogTier.Trusted"/>
    /// unconditionally, so passing a community recipe here would silently promote it to Trusted. The accepted
    /// community list must come from <see cref="LoadCandidates"/>/<see cref="LoadFromDirectory"/>'s
    /// <see cref="CommunityRecipeLoadResult.Loaded"/> (already gated + forced Community). Unwired this slice.
    /// </summary>
    public static IReadOnlyList<MigrationRecipe> CombineTrustedAndCommunity(
        IEnumerable<MigrationRecipe> trustedRecipes,
        IEnumerable<MigrationRecipe> acceptedCommunityRecipes)
    {
        ArgumentNullException.ThrowIfNull(trustedRecipes);
        ArgumentNullException.ThrowIfNull(acceptedCommunityRecipes);

        return trustedRecipes
            .Select(r => r with { CatalogTier = CatalogTier.Trusted })
            .Concat(acceptedCommunityRecipes.Select(r => r with { CatalogTier = CatalogTier.Community }))
            .OrderBy(r => r.Id, StringComparer.Ordinal)
            .ToArray();
    }

    private static string CandidateId(CommunityRecipeCandidate candidate)
    {
        string? parsedId = TryReadId(candidate.Json);
        if (!string.IsNullOrWhiteSpace(parsedId))
            return parsedId;

        string fileName = Path.GetFileNameWithoutExtension(candidate.Name);
        return string.IsNullOrWhiteSpace(fileName) ? "(unknown)" : fileName;
    }

    private static string? TryReadId(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("id", out JsonElement id)
                && id.ValueKind == JsonValueKind.String)
            {
                return id.GetString();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static RestoreTier? TryReadDeclaredRestoreTier(string json)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("restoreTier", out JsonElement tier)
                || tier.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return tier.GetString()?.ToLowerInvariant() switch
            {
                "inventoryonly" or "inventory-only" => RestoreTier.InventoryOnly,
                "configcopy" or "config-copy" => RestoreTier.ConfigCopy,
                "mergeafterinstall" or "merge-after-install" => RestoreTier.MergeAfterInstall,
                _ => null,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
