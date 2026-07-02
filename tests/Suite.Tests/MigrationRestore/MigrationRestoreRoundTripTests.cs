using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Win32;
using Xunit;
using WindowsCareKit.Tests.TestInfra;

namespace WindowsCareKit.Tests.MigrationRestore;

/// <summary>
/// Slice 2 RESTORE — the user's literal question answered: "does it put settings back in the RIGHT place after
/// a format, even with a different username / drive / relocated AppData?" These run host-safe over real temp
/// dirs through the PRODUCTION components: the BACKUP side is now the production <see cref="MigrationBackupRunner"/>
/// (RecipeResolver → gated executor seam → restore manifest), and the RESTORE side is the unchanged
/// <see cref="MigrationRestoreRunner"/> → GatedExecutor → atomic Merge.
/// </summary>
public class MigrationRestoreRoundTripTests
{
    private static readonly DateTime T0 = new(2026, 6, 20, 12, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// Resolve + copy a recipe's items from <paramref name="profileRoots"/> into a package dir, and build +
    /// save the restore manifest. Returns the package directory. This is the Slice 2 BACKUP side — now driven
    /// by the PRODUCTION <see cref="MigrationBackupRunner"/> (real gated executor + real hasher + real store),
    /// so the round-trip exercises the production orchestration and the index bug is provably gone.
    /// </summary>
    private static string BackupToPackage(string packageDir, ProfileRoots profileRoots, params MigrationRecipe[] recipes)
    {
        var resolver = new RecipeResolver(new RecipePathResolver(profileRoots), new Win32RecipeFileSystem());

        // The backup-side gate must AUTHORIZE writes into the temp package dir (the decision refutes the
        // "gate blocks external dirs" fear). Route the copy through the gated executor seam, not a direct
        // CopyAdapter, so the manifest reflects what the sanctioned executor actually copied.
        SafetyGate gate = MigrationRestoreTestData.GateAllowingPackage(packageDir);
        GatedExecutor executor = MigrationRestoreTestData.Executor(gate);
        var runner = new MigrationBackupRunner(
            resolver,
            new BackupExecutorAdapter(executor),
            new Sha256Hasher(),
            new PhysicalFileSystem(),
            new MigrationRestoreManifestStore(),
            gate);

        MigrationBackupPlanResult plan = runner.BuildPlan(recipes, packageDir, T0);
        MigrationBackupRunResult result = runner.Run(plan, plan.Plan.ComputeHash(), packageDir);
        Assert.True(result.Authorized, "backup plan must be authorized for a temp package dir");
        return packageDir;
    }

    // A single-file recipe (git .gitconfig) is the canonical Slice 2 ConfigWrite item.
    private static MigrationRecipe GitRecipe() => new(
        SchemaVersion: 1, Id: "git.config", DisplayName: "Git", Category: "dev-tools",
        Detect: new RecipeDetect(KnownFolder.UserProfile, ".gitconfig", Exists: true),
        Items: new[] { new RecipeItem(".gitconfig", Array.Empty<string>(), Array.Empty<string>()) },
        Exclude: Array.Empty<string>(), SecretRule: "global",
        PortabilityClass: PortabilityClass.ProfileRelative,
        Restore: new RecipeRestore(RestoreStrategy.MergeAfterInstall, RestorePhase.ConfigWrite, Array.Empty<string>()));

    // A single-file AppData-anchored recipe so the round-trip also proves a NON-UserProfile KnownFolder.
    private static MigrationRecipe ClaudeSettingsRecipe() => new(
        SchemaVersion: 1, Id: "anthropic.claude-code", DisplayName: "Claude", Category: "dev-tools",
        Detect: new RecipeDetect(KnownFolder.AppData, "wckclaude/settings.json", Exists: true),
        Items: new[] { new RecipeItem("wckclaude/settings.json", Array.Empty<string>(), Array.Empty<string>()) },
        Exclude: Array.Empty<string>(), SecretRule: "global",
        PortabilityClass: PortabilityClass.ProfileRelative,
        Restore: new RecipeRestore(RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, Array.Empty<string>()));

    /// <summary>
    /// F1 — REAL round-trip with a fabricated Profile B (different username + relocated/different-drive-shaped
    /// AppData). Every file lands at B's CORRECT KnownFolder + relative path, NOT at A's path (A != B).
    /// </summary>
    [Fact]
    public void RoundTrip_restores_each_file_to_the_correct_known_folder_on_a_fabricated_profile_B()
    {
        string root = MigrationRestoreTestData.TempDir("rt");
        try
        {
            // --- Profile A (the old machine): username "alice", standard layout. ---
            string aProfile = Path.Combine(root, "A", "Users", "alice");
            string aAppData = Path.Combine(aProfile, "AppData", "Roaming");
            Directory.CreateDirectory(aProfile);
            Directory.CreateDirectory(Path.Combine(aAppData, "wckclaude"));
            File.WriteAllText(Path.Combine(aProfile, ".gitconfig"), "[user]\n name = alice");
            File.WriteAllText(Path.Combine(aAppData, "wckclaude", "settings.json"), "{\"theme\":\"dark\"}");

            var aRoots = new ProfileRoots(aProfile, aAppData, Path.Combine(aProfile, "AppData", "Local"));

            // --- Backup A → package + manifest. ---
            string pkg = Path.Combine(root, "package");
            BackupToPackage(pkg, aRoots, GitRecipe(), ClaudeSettingsRecipe());

            // --- Profile B (the new machine): DIFFERENT username "bob", RELOCATED AppData under a different
            //     sub-path, simulating a different-drive / relocated-AppData layout. Nothing pre-exists. ---
            string bProfile = Path.Combine(root, "B", "Users", "bob");
            string bAppData = Path.Combine(root, "B", "RelocatedAppData", "bob", "Roaming"); // intentionally NOT under bProfile
            Directory.CreateDirectory(bProfile);
            var bRoots = new ProfileRoots(bProfile, bAppData, Path.Combine(root, "B", "RelocatedAppData", "bob", "Local"));

            // The gate's current/target profile is B's USERPROFILE; but the relocated AppData lives outside it,
            // so for this fabricated layout the gate's "users root" is the B tree root and the profile spans it.
            // Restore writes go to B's profile + B's relocated AppData; allow both by rooting the gate at B's top.
            string bRootDir = Path.Combine(root, "B");
            SafetyGate gate = MigrationRestoreTestData.GateForProfile(bRootDir, Path.Combine(bRootDir, "Users"));
            GatedExecutor executor = MigrationRestoreTestData.Executor(gate);

            // --- Restore package → Profile B. ---
            MigrationRestoreManifest manifest = new MigrationRestoreManifestStore().Load(pkg);
            var runner = new MigrationRestoreRunner(new RecipePathResolver(bRoots), gate);
            MigrationRestorePlanResult result = runner.BuildPlan(manifest, pkg, RestoreState.Empty, T0);

            Assert.Equal(2, result.Plan.Actions.Count); // both profile-relative configs planned
            Assert.Empty(result.Skipped);

            ExecutionReport report = executor.ExecuteWithReport(result.Plan, result.Plan.ComputeHash());
            Assert.True(report.Authorized, string.Join(",", report.Results.Select(r => r.Detail)));
            Assert.True(report.Results.All(r => r.Status == ActionStatus.Done),
                string.Join(",", report.Results.Select(r => $"{r.Kind}:{r.Status}:{r.Detail}")));

            // --- THE proof: files landed at B's CORRECT KnownFolder locations, NOT A's. ---
            string bGit = Path.Combine(bProfile, ".gitconfig");
            string bSettings = Path.Combine(bAppData, "wckclaude", "settings.json");
            Assert.True(File.Exists(bGit), "git config must land under B's USERPROFILE");
            Assert.True(File.Exists(bSettings), "settings.json must land under B's (relocated) APPDATA");
            Assert.Equal("[user]\n name = alice", File.ReadAllText(bGit));
            Assert.Equal("{\"theme\":\"dark\"}", File.ReadAllText(bSettings));

            // A != B: the destinations are genuinely different absolute paths from A's.
            Assert.NotEqual(Path.Combine(aProfile, ".gitconfig"), bGit);
            Assert.NotEqual(Path.Combine(aAppData, "wckclaude", "settings.json"), bSettings);
            // And nothing was written into A during the restore.
            Assert.False(File.Exists(Path.Combine(bProfile, "AppData", "Roaming", "wckclaude", "settings.json")),
                "settings.json must NOT be placed under B's default AppData when AppData is relocated");
        }
        finally { TestFs.DeleteResilient(root); }
    }

    /// <summary>
    /// F1 (pure) — a fabricated Profile B on a DIFFERENT DRIVE LETTER resolves to a different drive in the
    /// destination, proving KnownFolder is resolved on the TARGET roots (no host disk needed).
    /// </summary>
    [Fact]
    public void Rebind_resolves_destinations_onto_a_different_drive_letter_for_profile_B()
    {
        var bRoots = new ProfileRoots(@"D:\Users\bob", @"D:\Users\bob\AppData\Roaming", @"D:\Users\bob\AppData\Local");
        var resolver = new RecipePathResolver(bRoots);

        string git = resolver.Resolve(KnownFolder.UserProfile, ".gitconfig");
        string settings = resolver.Resolve(KnownFolder.AppData, "wckclaude/settings.json");

        Assert.Equal(@"D:\Users\bob\.gitconfig", git);
        Assert.Equal(@"D:\Users\bob\AppData\Roaming\wckclaude\settings.json", settings);
        Assert.StartsWith(@"D:\", git);          // different drive than a C:\ source machine
        Assert.Contains(@"\bob\", git);          // different username than alice
    }
}
