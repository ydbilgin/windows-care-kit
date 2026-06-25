using System.Reflection;

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
/// </summary>
public static class BuiltinRecipeSource
{
    // Logical resource names are <RootNamespace>.<dir-with-dots>.<file>; RootNamespace is WindowsCareKit.Core.
    private const string ResourcePrefix = "WindowsCareKit.Core.Modules.Migration.Recipes.";

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
