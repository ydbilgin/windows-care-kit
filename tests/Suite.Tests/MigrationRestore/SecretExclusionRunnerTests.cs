using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Win32;
using Xunit;
using WindowsCareKit.Tests.TestInfra;

namespace WindowsCareKit.Tests.MigrationRestore;

/// <summary>
/// Item 7(a) — Secret exclusion proven THROUGH the production <see cref="MigrationBackupRunner"/>:
/// binds the secret-glob overlay → runner wiring. Today secret exclusion is only proven by calling
/// <see cref="WindowsCareKit.Execution.Adapters.CopyAdapter"/> directly; this test shows it also holds
/// when the full BuildPlan→Run pipeline is executed.
///
/// <para>Non-vacuity invariant: if the secret-glob overlay globs are stripped from the ExcludeLeaves
/// forwarded by <see cref="RecipeToBackupEntry"/> (the bridging step), the <c>id_rsa</c> file would land
/// in the package and the test would fail.</para>
/// </summary>
public class SecretExclusionRunnerTests
{
    private static readonly DateTime T0 = new(2026, 6, 21, 10, 0, 0, DateTimeKind.Utc);

    private static MigrationBackupRunner Runner(ProfileRoots roots, SafetyGate gate)
        => new(
            new RecipeResolver(new RecipePathResolver(roots), new Win32RecipeFileSystem()),
            new BackupExecutorAdapter(MigrationRestoreTestData.Executor(gate)),
            new Sha256Hasher(),
            new PhysicalFileSystem(),
            new MigrationRestoreManifestStore(),
            gate);

    private static MigrationBackupRunResult RunBackup(
        ProfileRoots roots, string packageDir, params MigrationRecipe[] recipes)
    {
        SafetyGate gate = MigrationRestoreTestData.GateAllowingPackage(packageDir);
        MigrationBackupRunner runner = Runner(roots, gate);
        MigrationBackupPlanResult plan = runner.BuildPlan(recipes, packageDir, T0);
        return runner.Run(plan, plan.Plan.ComputeHash(), packageDir);
    }

    /// <summary>
    /// A recipe whose source DIRECTORY contains an <c>id_rsa</c> secret AND a benign <c>settings.json</c> is
    /// run through the full production runner. The runner must:
    /// <list type="bullet">
    /// <item>NOT copy <c>id_rsa</c> into the package on disk;</item>
    /// <item>have NO restore manifest target whose relative path maps to the secret;</item>
    /// <item>DO copy <c>settings.json</c> into the package.</item>
    /// </list>
    /// Binds <see cref="RecipeToBackupEntry"/> secret-glob overlay → <see cref="MigrationBackupRunner"/> wiring.
    /// </summary>
    [Fact]
    public void Runner_secret_glob_excludes_id_rsa_from_package_and_manifest_but_copies_benign_file()
    {
        string root = MigrationRestoreTestData.TempDir("secret-runner");
        try
        {
            // Build a synthetic profile with a directory item that contains both a secret and a benign file.
            string profile = System.IO.Path.Combine(root, "Users", "alice");
            string appDir = System.IO.Path.Combine(profile, ".testapp");
            System.IO.Directory.CreateDirectory(appDir);
            System.IO.File.WriteAllText(System.IO.Path.Combine(appDir, "settings.json"), "{\"theme\":\"dark\"}");
            // Exclusion triggers on the FILENAME (the `id_rsa*` secret glob), so the content is irrelevant —
            // keep it innocuous so a fake PEM header does not trip secret scanners (this file is not allowlisted).
            System.IO.File.WriteAllText(System.IO.Path.Combine(appDir, "id_rsa"), "fixture content - excluded by filename, never by content");

            var roots = new ProfileRoots(
                profile,
                System.IO.Path.Combine(profile, "AppData", "Roaming"),
                System.IO.Path.Combine(profile, "AppData", "Local"));

            // A recipe that targets the .testapp DIRECTORY (exists check on the dir itself).
            var recipe = new MigrationRecipe(
                SchemaVersion: 1, Id: "test.app", DisplayName: "TestApp", Category: "dev-tools",
                Detect: new RecipeDetect(KnownFolder.UserProfile, ".testapp", Exists: true),
                Items: new[] { new RecipeItem(".testapp", System.Array.Empty<string>(), System.Array.Empty<string>()) },
                Exclude: System.Array.Empty<string>(), SecretRule: "global",
                PortabilityClass: PortabilityClass.ProfileRelative,
                Restore: new RecipeRestore(RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, System.Array.Empty<string>()));

            string pkg = System.IO.Path.Combine(root, "package");
            MigrationBackupRunResult result = RunBackup(roots, pkg, recipe);

            Assert.True(result.Authorized, "backup must be authorized");

            // 1) id_rsa MUST NOT be present anywhere in the package directory on disk.
            string[] allInPackage = System.IO.Directory.Exists(pkg)
                ? System.IO.Directory.GetFiles(pkg, "*", System.IO.SearchOption.AllDirectories)
                : System.Array.Empty<string>();
            Assert.False(
                allInPackage.Any(f => System.IO.Path.GetFileName(f).Equals("id_rsa", StringComparison.OrdinalIgnoreCase)),
                "id_rsa private key must not be copied into the package by the production runner");

            // 2) The restore manifest must have NO target whose relative path contains id_rsa.
            Assert.False(
                result.Manifest.Targets.Any(t => t.RelativePath.Contains("id_rsa", StringComparison.OrdinalIgnoreCase)),
                "id_rsa must not appear as a restore target in the manifest");

            // 3) The benign file MUST be present in the package.
            Assert.True(
                allInPackage.Any(f => System.IO.Path.GetFileName(f).Equals("settings.json", StringComparison.OrdinalIgnoreCase)),
                "settings.json (benign) must be present in the package");
        }
        finally { TestFs.DeleteResilient(root); }
    }
}
