using WindowsCareKit.Core.Modules.Install;

namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>
/// The result of projecting recipes into install entries: the entries that became part of the package's
/// self-describing install manifest, plus the recipes/ids that were skipped (surfaced, never silently dropped —
/// mirrors the backup runner's honest-skip discipline).
/// </summary>
/// <param name="Entries">One <see cref="InstallEntry"/> per recipe that carried an install block (in recipe order).</param>
/// <param name="Skipped">Duplicate recipe ids / duplicate install ids that were rejected (skip + surface).</param>
public sealed record MigrationInstallProjection(
    IReadOnlyList<InstallEntry> Entries,
    IReadOnlyList<RecipeItemSkip> Skipped);

/// <summary>
/// PURE projector (no IO, no gate, no execution): maps the recipes that carry a v2 <see cref="RecipeInstall"/>
/// block into the Kur module's <see cref="InstallEntry"/> shape, so the migration BACKUP can emit a parallel,
/// self-describing <c>migration-install.json</c> and restore can feed the EXISTING gated
/// <see cref="InstallPlanner"/> — there is no second command-builder anywhere (decision §"reuse the whole
/// InstallPlanner"). It emits ONE entry per recipe (decision §review finding 2 — per-recipe, NOT per restore target),
/// keyed <c>migration:{recipe.Id}:install</c>, with a deterministic restore order derived from the recipe order
/// so the package's reinstall sequence is stable.
///
/// <para><b>Fail-closed:</b> a duplicate recipe id (the loader's id grammar permits two distinct recipes to share
/// an id only across separately-loaded sources) or a duplicate install entry id is REJECTED (skip + surface),
/// never overwritten — the same collision discipline the backup runner uses for copy targets. The winget id / npm
/// name carried here were already validated by the strict loader through <see cref="InstallPlanner.IsValidWingetId"/>
/// / <see cref="InstallPlanner.IsValidNpmPackage"/>, and they are re-validated again at strict package load and a
/// third time inside the planner — a tampered value can at worst become a gate-reviewed, never-auto-run entry.</para>
/// </summary>
public static class MigrationInstallProjector
{
    /// <summary>The <c>InstallEntry.Phase</c> stamped on every projected migration install entry.</summary>
    private const string InstallPhase = "install";

    /// <summary>Project every recipe that HAS an install block into one <see cref="InstallEntry"/>.</summary>
    public static MigrationInstallProjection Project(IEnumerable<MigrationRecipe> recipes)
    {
        ArgumentNullException.ThrowIfNull(recipes);

        var entries = new List<InstallEntry>();
        var skipped = new List<RecipeItemSkip>();

        // Reject duplicate recipe ids and duplicate projected install ids across the WHOLE supplied set, so two
        // recipes can never collide on one install checkpoint id (decision §"Duplicate detection").
        var seenRecipeIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenInstallIds = new HashSet<string>(StringComparer.Ordinal);

        // The recipe ENUMERATION order is the deterministic restore order: a recipe seen earlier installs earlier.
        // (The backup runner enumerates the same sequence; this keeps the package's install order stable.)
        int order = 0;

        foreach (MigrationRecipe recipe in recipes)
        {
            if (!seenRecipeIds.Add(recipe.Id))
            {
                skipped.Add(new RecipeItemSkip($"recipe:{recipe.Id}", "duplicate recipe id in the supplied set"));
                continue;
            }

            // No install block ⇒ this recipe carries config only (the common v1 case) — not an error, just nothing
            // to project here. It still gets its config restore target via the restore manifest.
            if (recipe.Install is null)
                continue;

            string installId = $"migration:{recipe.Id}:install";
            if (!seenInstallIds.Add(installId))
            {
                skipped.Add(new RecipeItemSkip(installId, "duplicate install entry id in the supplied set"));
                continue;
            }

            entries.Add(ToEntry(recipe, recipe.Install, installId, order));
            order++;
        }

        return new MigrationInstallProjection(entries, skipped);
    }

    private static InstallEntry ToEntry(MigrationRecipe recipe, RecipeInstall install, string installId, int restoreOrder)
    {
        string method = install.Method switch
        {
            RecipeInstallMethod.Winget => InstallMethod.Winget,
            RecipeInstallMethod.Npm => InstallMethod.Npm,
            RecipeInstallMethod.UrlManual => InstallMethod.UrlManual,
            _ => throw new ArgumentOutOfRangeException(nameof(install)),
        };

        // url-manual is surfaced as a manual-after checklist item (never auto-run); winget/npm are auto-tier.
        bool isManual = install.Method == RecipeInstallMethod.UrlManual;

        return new InstallEntry(
            Id: installId,
            Phase: InstallPhase,
            Category: recipe.Category,
            Method: method,
            WingetId: install.Method == RecipeInstallMethod.Winget ? install.WingetId : null,
            NpmPackage: install.Method == RecipeInstallMethod.Npm ? install.NpmPackage : null,
            RequiresAdmin: install.RequiresAdmin,
            RebootExpected: install.RebootExpected,
            RestoreOrder: restoreOrder,
            Description: recipe.DisplayName)
        {
            InstallTier = isManual ? Install.InstallTier.ManualAfter : Install.InstallTier.Auto,
            ManualUrl = isManual ? install.ManualUrl : null,
        };
    }
}
