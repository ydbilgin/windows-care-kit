using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Win32;
using Xunit;
using WindowsCareKit.Tests.TestInfra;

namespace WindowsCareKit.Tests.MigrationRestore;

public sealed class MigrationPackagePublicationTests
{
    private static readonly DateTime T0 = new(2026, 7, 22, 10, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Second_manifest_write_failure_yields_no_loadable_config_only_package()
    {
        string root = CreateRoot("second-write", out ProfileRoots roots);
        try
        {
            string packageDir = Path.Combine(root, "package");
            var writer = new FaultingFileWriter(faultWriteNumber: 2);

            MigrationBackupRunResult result = Run(roots, packageDir, Recipe(withInstall: false), writer);

            Assert.Equal(MigrationRestoreManifest.FileName, writer.FaultedFileName);
            Assert.Contains(result.FinalizationSkips,
                skip => skip.Reason.Contains("package not finalized", StringComparison.OrdinalIgnoreCase));
            Assert.False(new MigrationPackageMarkerStore(writer).Exists(packageDir));
            Assert.Throws<MigrationManifestException>(
                () => new MigrationRestoreManifestStore(writer).Load(packageDir));
        }
        finally
        {
            TestFs.DeleteResilient(root);
        }
    }

    [Fact]
    public void First_manifest_write_failure_leaves_package_unfinalized()
    {
        string root = CreateRoot("first-write", out ProfileRoots roots);
        try
        {
            string packageDir = Path.Combine(root, "package");
            var writer = new FaultingFileWriter(faultWriteNumber: 1);

            MigrationBackupRunResult result = Run(roots, packageDir, Recipe(withInstall: true), writer);

            Assert.Equal(MigrationInstallManifestStore.FileName, writer.FaultedFileName);
            Assert.NotEmpty(result.FinalizationSkips);
            Assert.False(new MigrationPackageMarkerStore(writer).Exists(packageDir));
            Assert.Throws<MigrationManifestException>(
                () => new MigrationRestoreManifestStore(writer).Load(packageDir));
        }
        finally
        {
            TestFs.DeleteResilient(root);
        }
    }

    [Fact]
    public void Successful_publish_writes_both_manifests_and_a_marker()
    {
        string root = CreateRoot("success", out ProfileRoots roots);
        try
        {
            string packageDir = Path.Combine(root, "package");
            var writer = new FaultingFileWriter();

            MigrationBackupRunResult result = Run(roots, packageDir, Recipe(withInstall: true), writer);

            Assert.Empty(result.FinalizationSkips);
            Assert.Single(new MigrationRestoreManifestStore(writer).Load(packageDir).Targets);
            Assert.Single(new MigrationInstallManifestStore(writer).Load(packageDir).Entries);
            MigrationPackageMarker marker = Assert.IsType<MigrationPackageMarker>(
                new MigrationPackageMarkerStore(writer).TryLoad(packageDir));
            Assert.Equal(T0, marker.CompletedUtc);
            Assert.Equal(1, marker.RestoreTargetCount);
            Assert.True(marker.HasInstallManifest);
        }
        finally
        {
            TestFs.DeleteResilient(root);
        }
    }

    [Fact]
    public void Config_only_package_is_distinguishable_from_a_failed_capture()
    {
        string root = CreateRoot("config-only", out ProfileRoots roots);
        try
        {
            string completeDir = Path.Combine(root, "complete");
            var writer = new FaultingFileWriter();
            MigrationBackupRunResult complete = Run(roots, completeDir, Recipe(withInstall: false), writer);

            Assert.Empty(complete.FinalizationSkips);
            MigrationPackageMarker marker = Assert.IsType<MigrationPackageMarker>(
                new MigrationPackageMarkerStore(writer).TryLoad(completeDir));
            Assert.Equal(1, marker.RestoreTargetCount);
            Assert.False(marker.HasInstallManifest);

            string failedDir = Path.Combine(root, "failed");
            var faultingWriter = new FaultingFileWriter(faultWriteNumber: 2);
            MigrationBackupRunResult failed = Run(roots, failedDir, Recipe(withInstall: false), faultingWriter);

            Assert.NotEmpty(failed.FinalizationSkips);
            Assert.False(new MigrationPackageMarkerStore(faultingWriter).Exists(failedDir));
        }
        finally
        {
            TestFs.DeleteResilient(root);
        }
    }

    [Fact]
    public void Retry_after_a_finalization_failure_converges_to_one_complete_package()
    {
        string root = CreateRoot("retry", out ProfileRoots roots);
        try
        {
            string packageDir = Path.Combine(root, "package");
            var faultingWriter = new FaultingFileWriter(faultWriteNumber: 2);
            MigrationBackupRunResult first = Run(roots, packageDir, Recipe(withInstall: false), faultingWriter);
            Assert.NotEmpty(first.FinalizationSkips);

            var writer = new FaultingFileWriter();
            MigrationBackupRunResult retry = Run(roots, packageDir, Recipe(withInstall: false), writer);

            Assert.Empty(retry.FinalizationSkips);
            Assert.Single(new MigrationRestoreManifestStore(writer).Load(packageDir).Targets);
            Assert.Empty(new MigrationInstallManifestStore(writer).Load(packageDir).Entries);
            Assert.True(new MigrationPackageMarkerStore(writer).Exists(packageDir));
            Assert.Single(Directory.EnumerateFiles(packageDir, ".gitconfig", SearchOption.AllDirectories));
        }
        finally
        {
            TestFs.DeleteResilient(root);
        }
    }

    private static MigrationBackupRunResult Run(
        ProfileRoots roots,
        string packageDir,
        MigrationRecipe recipe,
        IFileWriter writer)
    {
        SafetyGate gate = MigrationRestoreTestData.GateAllowingPackage(packageDir);
        var runner = new MigrationBackupRunner(
            new RecipeResolver(new RecipePathResolver(roots), new Win32RecipeFileSystem()),
            new BackupExecutorAdapter(MigrationRestoreTestData.Executor(gate)),
            new Sha256Hasher(),
            new PhysicalFileSystem(),
            new MigrationRestoreManifestStore(writer),
            gate,
            new MigrationInstallManifestStore(writer),
            new MigrationPackageMarkerStore(writer));
        MigrationBackupPlanResult plan = runner.BuildPlan([recipe], packageDir, T0);
        return runner.Run(plan, plan.Plan.ComputeHash(), packageDir);
    }

    private static string CreateRoot(string tag, out ProfileRoots roots)
    {
        string root = MigrationRestoreTestData.TempDir(tag);
        string profile = Path.Combine(root, "Users", "alice");
        Directory.CreateDirectory(profile);
        File.WriteAllText(Path.Combine(profile, ".gitconfig"), "[user]\n name = alice");
        roots = new ProfileRoots(
            profile,
            Path.Combine(profile, "AppData", "Roaming"),
            Path.Combine(profile, "AppData", "Local"));
        return root;
    }

    private static MigrationRecipe Recipe(bool withInstall)
    {
        var recipe = new MigrationRecipe(
            SchemaVersion: withInstall ? 2 : 1,
            Id: "git.config",
            DisplayName: "Git",
            Category: "dev-tools",
            Detect: new RecipeDetect(KnownFolder.UserProfile, ".gitconfig", Exists: true),
            Items: [new RecipeItem(".gitconfig", Array.Empty<string>(), Array.Empty<string>())],
            Exclude: Array.Empty<string>(),
            SecretRule: "global",
            PortabilityClass: PortabilityClass.ProfileRelative,
            Restore: new RecipeRestore(
                RestoreStrategy.MergeAfterInstall, RestorePhase.ConfigWrite, Array.Empty<string>()));

        return withInstall
            ? recipe with
            {
                Install = new RecipeInstall(
                    RecipeInstallMethod.Winget, "Git.Git", null, null, RequiresAdmin: false, RebootExpected: false),
            }
            : recipe;
    }

    private sealed class FaultingFileWriter(int? faultWriteNumber = null) : IFileWriter
    {
        private readonly SanctionedFileWriter _inner = new();
        private int _writeCount;

        public string? FaultedFileName { get; private set; }

        public void WriteAllText(string path, string contents)
        {
            _writeCount++;
            if (_writeCount == faultWriteNumber)
            {
                FaultedFileName = Path.GetFileName(path);
                throw new IOException($"synthetic write failure for {FaultedFileName}");
            }

            _inner.WriteAllText(path, contents);
        }

        public void AtomicReplace(string path, string contents) => _inner.AtomicReplace(path, contents);
    }
}
