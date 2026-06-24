using WindowsCareKit.Core.Modules.Backup;

namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>A bridged item: the execution-projection <see cref="BackupEntry"/> plus its side-carrier meta.</summary>
/// <param name="Entry">The <c>BackupEntry</c> the existing <c>BackupPlanner</c>/<c>CopyAdapter</c> consume unchanged.</param>
/// <param name="Meta">The recipe-only metadata (portability/restore) that does NOT live on <c>BackupEntry</c> (F4).</param>
public sealed record BridgedMigrationItem(BackupEntry Entry, MigrationItemMeta Meta);

/// <summary>
/// The bridge (decision §"RecipeToBackupEntry köprü"): turns the items that PASSED <see cref="RecipeResolver"/>'s
/// sandbox into existing <see cref="BackupEntry"/> values, so the unchanged <c>BackupPlanner</c> projects them
/// into a gate-approved dry-run <c>BackupManifest</c>/plan. Two invariants:
/// <list type="bullet">
/// <item><b>F2:</b> ONLY sandbox-passing items become entries — a skipped (escaped/reparse/missing) item never
/// produces a <c>BackupEntry</c>.</item>
/// <item><b>F3/F4:</b> the global secret-glob overlay is merged into each entry's <c>Exclude</c> (forbidden-first
/// is preserved by the engine + <see cref="MigrationSecretFilter"/>), and the recipe-only portability/restore
/// META rides on a parallel <see cref="MigrationItemMeta"/> rather than on the frozen <c>BackupEntry</c> schema.</item>
/// </list>
/// </summary>
public static class RecipeToBackupEntry
{
    /// <summary>Bridge every sandbox-passing item of <paramref name="resolved"/> into entry + meta pairs.</summary>
    public static IReadOnlyList<BridgedMigrationItem> Bridge(ResolvedRecipe resolved)
    {
        ArgumentNullException.ThrowIfNull(resolved);
        if (!resolved.DetectMatched || resolved.Items.Count == 0)
            return Array.Empty<BridgedMigrationItem>();

        MigrationRecipe recipe = resolved.Recipe;
        var result = new List<BridgedMigrationItem>(resolved.Items.Count);
        int index = 0;

        foreach (ResolvedRecipeItem item in resolved.Items)
        {
            string entryId = $"{recipe.Id}#{index++}";

            // F3: the secret-glob overlay is layered ON TOP of every recipe's own excludes, into ExcludeLeaves.
            // CopyAdapter.Exclusions treats any '*'-bearing ExcludeLeaves entry as a leaf glob and evaluates
            // exclusion BEFORE the include allow-list, so a recipe's include can never pull back a secret leaf.
            var excludes = new List<string>(item.Exclude.Count + SecretGlobOverlay.Globs.Count);
            excludes.AddRange(item.Exclude);
            excludes.AddRange(SecretGlobOverlay.Globs);

            // B-1 (decision §3A; review cx#1/#2): aggregate the FULL name policy the copy engine enforces
            // (fixed credential leaves + secret globs, via MigrationSecretFilter) over this item's DECLARED
            // secret surface — the item's own path leaf AND each include pattern's leaf (path-shaped patterns
            // such as `**/id_rsa` or `keys/*.pem` are reduced to their leaf first). A declared secret the engine
            // prunes must NOT let the item claim a clean "works". Honesty residual (M2.5 content probe):
            // name-based only — an unknown-named DPAPI blob under a broad/empty include is NOT caught here.
            bool hasExcludedSecret = MigrationSecretFilter.IsSecretLeafName(LeafOf(item.RecipePath));
            if (!hasExcludedSecret)
            {
                foreach (string inc in item.Include)
                {
                    if (MigrationSecretFilter.IsSecretLeafName(LeafOf(inc)))
                    {
                        hasExcludedSecret = true;
                        break;
                    }
                }
            }

            var entry = new BackupEntry(
                Id: entryId,
                Enabled: true,
                Method: BackupMethod.Copy,
                Category: recipe.Category,
                Source: item.AbsoluteSource,
                Target: item.TargetRelative,
                Exclude: excludes,
                SecretHandling: SecretHandling.Normal,
                RestoreOrder: 50,
                RestoreMode: RestoreModeText(recipe.Restore.Strategy),
                Description: recipe.DisplayName,
                UiWarning: WarningFor(recipe.PortabilityClass))
            {
                Include = item.Include,
            };

            IReadOnlyList<string> preconditions = MergePreconditions(recipe.Restore.Preconditions, item.RequiresClosedProcesses);
            var meta = new MigrationItemMeta(
                RecipeId: recipe.Id,
                EntryId: entryId,
                PortabilityClass: recipe.PortabilityClass,
                RestoreStrategy: recipe.Restore.Strategy,
                RestorePhase: recipe.Restore.Phase,
                Preconditions: preconditions)
            {
                HasExcludedSecret = hasExcludedSecret,
            };

            result.Add(new BridgedMigrationItem(entry, meta));
        }

        return result;
    }

    /// <summary>The leaf (final segment) of a declared item/include path, separator-agnostic — so a path-shaped
    /// secret declaration (<c>**/id_rsa</c>, <c>.ssh/id_rsa</c>, <c>keys/*.pem</c>) is matched by its leaf name (B-1).</summary>
    private static string LeafOf(string path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;
        string s = path.Replace('\\', '/').TrimEnd('/');
        int slash = s.LastIndexOf('/');
        return slash >= 0 ? s[(slash + 1)..] : s;
    }

    private static string RestoreModeText(RestoreStrategy strategy) => strategy switch
    {
        RestoreStrategy.MergeAfterInstall => "merge-after-install",
        RestoreStrategy.Replace => "replace",
        _ => "config-write",
    };

    private static IReadOnlyList<string> MergePreconditions(
        IReadOnlyList<string> recipePreconditions,
        IReadOnlyList<string> closedProcesses)
    {
        if (closedProcesses.Count == 0)
            return recipePreconditions;

        var merged = new List<string>(recipePreconditions.Count + closedProcesses.Count);
        merged.AddRange(recipePreconditions);
        foreach (string process in closedProcesses)
            if (!string.IsNullOrWhiteSpace(process))
                merged.Add($"process-closed:{process.Trim()}");
        return merged;
    }

    // A machine-locked recipe must never be presented as "it will work" — surface a warning, never a green claim.
    private static string? WarningFor(PortabilityClass cls) => cls switch
    {
        PortabilityClass.MachineLocked => "machine-locked: tekrar kurulum/giriş gerekebilir",
        PortabilityClass.Partial => "kısmen taşınabilir: bazı ayarlar yeni makinede çalışmayabilir",
        _ => null,
    };
}
