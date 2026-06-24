using System.IO;

namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>One resolved recipe item that passed the full sandbox — ready to become a <c>BackupEntry</c>.</summary>
/// <param name="AbsoluteSource">The canonicalized, profile-contained absolute source path.</param>
/// <param name="TargetRelative">The payload-relative target path (forward slashes).</param>
/// <param name="Include">Include allow-list globs (from the recipe item).</param>
/// <param name="Exclude">Exclude globs (recipe-wide + item, secret overlay is added by the bridge).</param>
/// <param name="RecipePath">
/// The item's path as declared in the recipe, RELATIVE to its <see cref="RecipeDetect.KnownFolder"/>, normalized
/// to forward slashes. It travels with exactly the sandbox-passing items, so the backup runner can correlate a
/// bridged entry to its declared item path WITHOUT indexing back into <c>recipe.Items</c> — which is a superset
/// (skips drop items) and would misalign by one for every skipped item (the latent index bug, killed here by
/// construction).
/// </param>
public sealed record ResolvedRecipeItem(
    string AbsoluteSource,
    string TargetRelative,
    IReadOnlyList<string> Include,
    IReadOnlyList<string> Exclude,
    string RecipePath,
    IReadOnlyList<string> RequiresClosedProcesses);

/// <summary>One item the sandbox refused, plus the human reason (for the report / tests).</summary>
public sealed record RecipeItemSkip(string ItemPath, string Reason);

/// <summary>The output of resolving one recipe: the items that passed the sandbox and the ones that did not.</summary>
/// <param name="Recipe">The source recipe.</param>
/// <param name="DetectMatched">False when <c>detect.exists</c> required the detect path and it was absent.</param>
/// <param name="Items">Items that passed the full sandbox (empty when detect did not match).</param>
/// <param name="Skipped">Items the sandbox refused (escape, reparse, missing, …).</param>
public sealed record ResolvedRecipe(
    MigrationRecipe Recipe,
    bool DetectMatched,
    IReadOnlyList<ResolvedRecipeItem> Items,
    IReadOnlyList<RecipeItemSkip> Skipped);

/// <summary>
/// The REAL sandbox (critic fix F2). For each recipe item it runs, in order:
/// <list type="number">
/// <item><b>detect</b> — if <c>detect.exists</c> is set and the detect path is absent, the whole recipe is skipped;</item>
/// <item><b>token-expand</b> — the closed <see cref="KnownFolder"/> root + relative path via <see cref="RecipePathResolver"/>
/// (lexical containment, no <c>%ENV%</c>, no rooted/UNC/<c>..</c>);</item>
/// <item><b>canonicalize</b> — resolve junctions/symlinks to the true target via <see cref="IRecipeFileSystem.Canonicalize"/>;
/// an UNRESOLVED reparse point fails closed;</item>
/// <item><b>containment</b> — the canonical target must STILL be inside the selected profile root (a junction that
/// jumps out of the profile is refused even though the lexical path looked fine).</item>
/// </list>
/// Only items that pass every step are emitted; the bridge turns ONLY those into <c>BackupEntry</c> values.
/// </summary>
public sealed class RecipeResolver
{
    private readonly RecipePathResolver _paths;
    private readonly IRecipeFileSystem _fs;

    public RecipeResolver(RecipePathResolver paths, IRecipeFileSystem fs)
    {
        _paths = paths ?? throw new ArgumentNullException(nameof(paths));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
    }

    public ResolvedRecipe Resolve(MigrationRecipe recipe)
    {
        ArgumentNullException.ThrowIfNull(recipe);

        string profileRoot = _paths.RootFor(recipe.Detect.KnownFolder);

        // (1) detect
        bool detectMatched = true;
        if (recipe.Detect.Exists)
        {
            string detectAbs;
            try
            {
                detectAbs = _paths.Resolve(recipe.Detect.KnownFolder, recipe.Detect.Path);
            }
            catch (RecipePathException)
            {
                // A detect path that cannot even be lexically resolved means we can never trust this recipe.
                return new ResolvedRecipe(recipe, false, Array.Empty<ResolvedRecipeItem>(), Array.Empty<RecipeItemSkip>());
            }
            detectMatched = _fs.DirectoryExists(detectAbs) || _fs.FileExists(detectAbs);
        }

        if (!detectMatched)
            return new ResolvedRecipe(recipe, false, Array.Empty<ResolvedRecipeItem>(), Array.Empty<RecipeItemSkip>());

        var items = new List<ResolvedRecipeItem>();
        var skipped = new List<RecipeItemSkip>();

        foreach (RecipeItem item in recipe.Items)
        {
            if (item.Kind != RecipeItemKind.ProfilePath || !IsProfileFolder(recipe.Detect.KnownFolder))
            {
                skipped.Add(new RecipeItemSkip(item.Path, "inventory-only item kind is not copied by the profile resolver"));
                continue;
            }

            // (2) token-expand + lexical containment
            string lexical;
            try
            {
                lexical = _paths.Resolve(recipe.Detect.KnownFolder, item.Path);
            }
            catch (RecipePathException ex)
            {
                skipped.Add(new RecipeItemSkip(item.Path, ex.Message));
                continue;
            }

            // (3) the source must exist to be backed up
            if (!_fs.DirectoryExists(lexical) && !_fs.FileExists(lexical))
            {
                skipped.Add(new RecipeItemSkip(item.Path, "source does not exist"));
                continue;
            }

            // (3a) an unresolved reparse point at the source is untrustworthy → fail closed
            string? canonical = _fs.Canonicalize(lexical);
            if (canonical is null)
            {
                skipped.Add(new RecipeItemSkip(item.Path, "unresolved reparse point (junction/symlink)"));
                continue;
            }

            // (4) canonical containment: a junction that redirects OUT of the profile root is refused even
            //     though the lexical path was inside it (this is what defeats reparse-traversal escape).
            if (!RecipePathResolver.IsContained(profileRoot, canonical))
            {
                skipped.Add(new RecipeItemSkip(item.Path, "canonical source escapes the profile root"));
                continue;
            }

            string targetRelative = BuildTargetRelative(recipe.Id, item.Path);
            var exclude = MergeExcludes(recipe.Exclude, item.Exclude);
            // Carry the declared item path (normalized forward-slash, trimmed) so the backup runner never has to
            // index back into the recipe's (superset) item list to recover it — skip-proof by construction.
            string recipePath = item.Path.Replace('\\', '/').Trim('/');
            items.Add(new ResolvedRecipeItem(canonical, targetRelative, item.Include, exclude, recipePath, item.RequiresClosedProcesses));
        }

        return new ResolvedRecipe(recipe, true, items, skipped);
    }

    /// <summary>Payload-relative target: <c>migration/&lt;recipeId&gt;/&lt;itemPath&gt;</c> (forward slashes, normalized).</summary>
    private static string BuildTargetRelative(string recipeId, string itemPath)
    {
        string item = itemPath.Replace('\\', '/').Trim('/');
        return $"migration/{recipeId}/{item}";
    }

    private static IReadOnlyList<string> MergeExcludes(IReadOnlyList<string> recipeWide, IReadOnlyList<string> item)
    {
        if (recipeWide.Count == 0 && item.Count == 0)
            return Array.Empty<string>();
        var merged = new List<string>(recipeWide.Count + item.Count);
        merged.AddRange(recipeWide);
        merged.AddRange(item);
        return merged;
    }

    private static bool IsProfileFolder(KnownFolder folder)
        => folder is KnownFolder.UserProfile or KnownFolder.AppData or KnownFolder.LocalAppData;
}
