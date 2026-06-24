using System.IO;
using WindowsCareKit.Core.Modules.Backup;

namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>
/// The BACKUP-side restore-manifest emitter (decision §"Backup-tarafı ek"). After the bridge has produced the
/// <see cref="BridgedMigrationItem"/> entries the backup engine copies, this builds the parallel
/// <see cref="MigrationRestoreTarget"/> for each — recording WHICH closed <see cref="KnownFolder"/> the item
/// anchors to and its normalized relative path — so the restore side can later place every file back in the
/// CORRECT location on a different machine.
///
/// <para><b>F5:</b> the relative path is normalized through <see cref="RecipePathResolver"/>'s own rules
/// (reject <c>%ENV%</c> / rooted / UNC / <c>..</c>, lexical containment) by resolving it against a fixed dummy
/// root and re-deriving the relative segment — never a raw string-join. An item whose recipe path cannot be
/// safely resolved produces NO target (it could not have been backed up safely either).</para>
/// </summary>
public static class MigrationRestoreManifestBuilder
{
    // A fixed, synthetic root used ONLY to run the path through RecipePathResolver's normalization/containment
    // rules so the stored RelativePath is provably traversal/absolute/escape-free (F5). It never touches disk.
    private const string NormalizationRoot = @"C:\__wck_norm__";

    /// <summary>
    /// Build a restore target for one recipe item. <paramref name="itemRecipePath"/> is the item's path as
    /// declared in the recipe (relative to the recipe's <see cref="RecipeDetect.KnownFolder"/>). Returns null
    /// when the path cannot be safely normalized.
    /// </summary>
    public static MigrationRestoreTarget? BuildTarget(
        MigrationRecipe recipe,
        MigrationItemMeta meta,
        KnownFolder knownFolder,
        string itemRecipePath,
        string packageRelativeSource,
        string sha256)
    {
        ArgumentNullException.ThrowIfNull(recipe);
        ArgumentNullException.ThrowIfNull(meta);

        string? relative = TryNormalizeRelative(knownFolder, itemRecipePath);
        if (relative is null)
            return null;

        return new MigrationRestoreTarget(
            RecipeId: recipe.Id,
            EntryId: meta.EntryId,
            KnownFolder: knownFolder,
            RelativePath: relative,
            PackageRelativeSource: packageRelativeSource.Replace('\\', '/').Trim('/'),
            RestoreStrategy: meta.RestoreStrategy,
            RestorePhase: meta.RestorePhase,
            Preconditions: meta.Preconditions,
            PortabilityClass: meta.PortabilityClass,
            Sha256: (sha256 ?? string.Empty).ToLowerInvariant())
        {
            RestoreTier = recipe.RestoreTier,
            MigrationMeta = recipe.MigrationMeta,
        };
    }

    /// <summary>
    /// Run <paramref name="itemRecipePath"/> through the recipe path rules against a synthetic root and return
    /// the normalized profile-relative segment (forward slashes), or null if it would escape / is unsafe (F5).
    /// </summary>
    internal static string? TryNormalizeRelative(KnownFolder knownFolder, string itemRecipePath)
    {
        if (string.IsNullOrWhiteSpace(itemRecipePath))
            return null;

        var roots = new ProfileRoots(NormalizationRoot, NormalizationRoot, NormalizationRoot);
        var resolver = new RecipePathResolver(roots);
        try
        {
            string absolute = resolver.Resolve(knownFolder, itemRecipePath);
            string root = resolver.RootFor(knownFolder);
            string rel = Path.GetRelativePath(root, absolute);
            return rel.Replace('\\', '/').Trim('/');
        }
        catch (RecipePathException)
        {
            return null;
        }
    }
}
