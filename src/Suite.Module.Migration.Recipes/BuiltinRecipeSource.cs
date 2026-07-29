using System.Reflection;

// Namespace intentionally stays WindowsCareKit.Core.Modules.Migration even though this type lives in the
// Suite.Module.Migration.Recipes assembly: these seeds are Core-domain recipe vocabulary (MigrationRecipe,
// MigrationRecipeLoader) that merely ships from a module-owned assembly. This is a DELIBERATE, documented
// exception to the NAME-01 rule that a module assembly declares its own WindowsCareKit.Module.<Feature> root
// (see .planning/STAGING/ARCH-FIX-SPEC-NAME01_module-namespaces_2026-07-29.md §2.3); it is deliberately NOT
// covered by the ArchitectureTests namespace-ownership guard, whose module list is explicit for that reason.
namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>
/// The L1 built-in recipe source (decision §"4 katman + merge": L1 gömülü-builtin). It reads the seed
/// recipes shipped as embedded JSON resources and validates each through the strict loader
/// (<see cref="MigrationRecipeLoader"/>). Because the seeds are compiled into the assembly they cannot be
/// tampered with on disk, and a malformed seed fails the build's tests rather than shipping silently.
/// Built-in recipes declare <c>upstreamDataLicense: none</c> because they are WCK-authored app-data definitions,
/// not bundled third-party corpus data.
///
/// Slice 1 seeds: Claude Code, Discord, VS Code, .gitconfig. <c>.ssh</c> is intentionally deferred
/// (critic fix F3 — key-leak risk until the secret-glob overlay matures).
///
/// Module-owned since modular M2: these seed recipes ship in <c>Suite.Module.Migration.Recipes.dll</c>, not
/// <c>Suite.Core.dll</c>. An install without the Migration module does not carry the seed recipes on disk.
/// </summary>
public static class BuiltinRecipeSource
{
    // Logical resource names are pinned explicitly via csproj LogicalName (prefix below); RootNamespace is
    // intentionally NOT load-bearing for resource lookup (a folder/RootNamespace rename cannot silently empty
    // the recipe set — see Recipes_are_module_owned_and_core_is_recipe_free).
    private const string ResourcePrefix = "WindowsCareKit.Module.Migration.Recipes.";

    /// <summary>Load + validate every embedded seed recipe, ordered by id for determinism.</summary>
    public static IReadOnlyList<MigrationRecipe> LoadAll()
    {
        Assembly asm = typeof(BuiltinRecipeSource).Assembly;
        var recipes = new List<MigrationRecipe>();

        foreach (string name in asm.GetManifestResourceNames())
        {
            if (!name.StartsWith(ResourcePrefix, StringComparison.Ordinal) || !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                continue;

            using Stream? stream = asm.GetManifestResourceStream(name);
            if (stream is null)
                continue;
            using var reader = new StreamReader(stream);
            string json = reader.ReadToEnd();

            recipes.Add(MigrationRecipeLoader.Load(json)); // strict: a bad seed throws → caught by tests
        }

        recipes.Sort((a, b) => string.CompareOrdinal(a.Id, b.Id));
        return recipes;
    }
}
