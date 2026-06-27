using System.Reflection;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Execution;
using Xunit;

namespace WindowsCareKit.Tests.MigrationRestore;

public sealed class RestoreViewModelTests
{
    [Fact]
    public async Task Preview_builds_plan_and_surfaces_restored_and_manual_dispositions()
    {
        using var fx = Fixture.Create("vm-preview");
        fx.WritePayload("migration/x/settings.json", "NEW");
        fx.SaveManifest(
            Target("git.config#0", ".gitconfig"),
            Target("locked#0", "locked.db", recipeId: "locked.app", portability: PortabilityClass.MachineLocked));

        RestoreViewModel vm = fx.CreateViewModel();

        await vm.LoadAndPreviewAsync();

        Assert.True(vm.HasPreviewPlan);
        Assert.Single(vm.PlanRows);
        Assert.Single(vm.SkippedRows);
        Assert.Single(vm.RestoredRows);
        Assert.Empty(vm.ReinstallEnqueuedRows);
        Assert.NotEmpty(vm.ManualRows);
        Assert.Contains(vm.ManualRows, row => row.Detail?.Contains("MachineLocked", StringComparison.Ordinal) == true);
        Assert.False(vm.CanRunRestore);
        Assert.False(vm.IsPreviewApproved);
        Assert.Empty(vm.PackageWarning);
    }

    [Fact]
    public async Task Run_requires_approval_then_restores_and_enables_undo()
    {
        using var fx = Fixture.Create("vm-run");
        fx.WritePayload("migration/x/settings.json", "NEW");
        fx.SaveManifest(Target("git.config#0", ".gitconfig"));
        File.WriteAllText(Path.Combine(fx.Profile, ".gitconfig"), "OLD");
        RestoreViewModel vm = fx.CreateViewModel();

        await vm.LoadAndPreviewAsync();
        await vm.RunRestoreAsync();

        Assert.Equal("OLD", File.ReadAllText(Path.Combine(fx.Profile, ".gitconfig")));
        Assert.False(vm.CanRunRestore);

        vm.IsPreviewApproved = true;
        await vm.RunRestoreAsync();

        Assert.Equal("NEW", File.ReadAllText(Path.Combine(fx.Profile, ".gitconfig")));
        Assert.True(vm.HasResultRows);
        Assert.True(vm.CanPreviewUndo);
        Assert.False(vm.CanUndo);
        Assert.True(File.Exists(new RestoreStateStore().PathFor(fx.StateDir)));
    }

    [Fact]
    public async Task Tampered_approved_hash_refuses_with_zero_mutation()
    {
        using var fx = Fixture.Create("vm-refused");
        fx.WritePayload("migration/x/settings.json", "NEW");
        fx.SaveManifest(Target("git.config#0", ".gitconfig"));
        string destination = Path.Combine(fx.Profile, ".gitconfig");
        File.WriteAllText(destination, "OLD");
        RestoreViewModel vm = fx.CreateViewModel();

        await vm.LoadAndPreviewAsync();
        vm.IsPreviewApproved = true;
        SetApprovedHash(vm, "tampered");

        await vm.RunRestoreAsync();

        Assert.Equal("OLD", File.ReadAllText(destination));
        Assert.Empty(vm.ResultRows);
        Assert.False(vm.CanUndo);
        Assert.Equal("migration.restore.refused", vm.RestoreSummary);
        Assert.False(File.Exists(new RestoreStateStore().PathFor(fx.StateDir)));
        Assert.Empty(Directory.EnumerateFiles(fx.Root, "*.bak.*", SearchOption.AllDirectories));
    }

    [Fact]
    public async Task Machine_locked_target_yields_no_writes_and_visible_manual_row()
    {
        using var fx = Fixture.Create("vm-locked");
        fx.SaveManifest(Target(
            "locked#0",
            "locked.db",
            recipeId: "locked.app",
            portability: PortabilityClass.MachineLocked));
        RestoreViewModel vm = fx.CreateViewModel();

        await vm.LoadAndPreviewAsync();
        vm.IsPreviewApproved = true;
        await vm.RunRestoreAsync();

        Assert.Empty(vm.PlanRows);
        Assert.Single(vm.SkippedRows);
        Assert.NotEmpty(vm.ManualRows);
        Assert.False(vm.CanRunRestore);
        Assert.False(File.Exists(Path.Combine(fx.Profile, "locked.db")));
        Assert.False(File.Exists(new RestoreStateStore().PathFor(fx.StateDir)));
    }

    [Fact]
    public async Task Undo_reverts_old_bytes_through_journaled_backup()
    {
        using var fx = Fixture.Create("vm-undo");
        fx.WritePayload("migration/x/settings.json", "NEW");
        fx.SaveManifest(Target("git.config#0", ".gitconfig"));
        string destination = Path.Combine(fx.Profile, ".gitconfig");
        File.WriteAllText(destination, "OLD");
        RestoreViewModel vm = fx.CreateViewModel();

        await vm.LoadAndPreviewAsync();
        vm.IsPreviewApproved = true;
        await vm.RunRestoreAsync();
        await vm.PreviewUndoAsync();
        vm.IsUndoPreviewApproved = true;
        await vm.UndoAsync();

        Assert.Equal("OLD", File.ReadAllText(destination));
        Assert.Contains(vm.UndoRows, row => row.RiskText == "migration.restore.status.Done");
    }

    [Fact]
    public async Task Execute_undo_is_no_op_until_undo_preview_is_approved()
    {
        using var fx = Fixture.Create("vm-undo-no-approval");
        fx.WritePayload("migration/x/settings.json", "NEW");
        fx.SaveManifest(Target("git.config#0", ".gitconfig"));
        string destination = Path.Combine(fx.Profile, ".gitconfig");
        File.WriteAllText(destination, "OLD");
        RestoreViewModel vm = fx.CreateViewModel();

        await vm.LoadAndPreviewAsync();
        vm.IsPreviewApproved = true;
        await vm.RunRestoreAsync();
        await vm.PreviewUndoAsync();

        Assert.False(vm.CanUndo);
        await vm.UndoAsync();

        Assert.Equal("NEW", File.ReadAllText(destination));
        Assert.DoesNotContain(vm.UndoRows, row => row.RiskText == "migration.restore.status.Done");
    }

    [Fact]
    public async Task Undo_with_missing_backup_fails_visibly()
    {
        using var fx = Fixture.Create("vm-undo-missing");
        fx.WritePayload("migration/x/settings.json", "NEW");
        fx.SaveManifest(Target("git.config#0", ".gitconfig"));
        string destination = Path.Combine(fx.Profile, ".gitconfig");
        File.WriteAllText(destination, "OLD");
        RestoreViewModel vm = fx.CreateViewModel();

        await vm.LoadAndPreviewAsync();
        vm.IsPreviewApproved = true;
        await vm.RunRestoreAsync();
        File.Delete(Directory.GetFiles(fx.Profile, ".gitconfig.bak.*").Single());
        await vm.PreviewUndoAsync();
        vm.IsUndoPreviewApproved = true;
        await vm.UndoAsync();

        Assert.Equal("NEW", File.ReadAllText(destination));
        Assert.Contains(vm.UndoRows, row => row.RiskText == "migration.restore.status.Failed");
    }

    [Fact]
    public async Task Package_without_manifest_sets_warning_and_does_not_crash()
    {
        using var fx = Fixture.Create("vm-no-manifest");
        RestoreViewModel vm = fx.CreateViewModel();

        await vm.LoadAndPreviewAsync();

        Assert.False(vm.HasPreviewPlan);
        Assert.Empty(vm.PlanRows);
        Assert.StartsWith("migration.restore.noManifestWarning", vm.PackageWarning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Changing_package_or_state_directory_clears_preview_and_approval()
    {
        using var fx = Fixture.Create("vm-reset");
        fx.WritePayload("migration/x/settings.json", "NEW");
        fx.SaveManifest(Target("git.config#0", ".gitconfig"));
        RestoreViewModel vm = fx.CreateViewModel();

        await vm.LoadAndPreviewAsync();
        vm.IsPreviewApproved = true;

        vm.StateDir = Path.Combine(fx.Root, "other-state");

        Assert.False(vm.HasPreviewPlan);
        Assert.False(vm.IsPreviewApproved);
        Assert.False(vm.CanRunRestore);
        Assert.Empty(vm.PlanRows);
    }

    private static MigrationRestoreTarget Target(
        string entryId,
        string relativePath,
        string recipeId = "git.config",
        string packageRelativeSource = "migration/x/settings.json",
        PortabilityClass portability = PortabilityClass.ProfileRelative)
        => new(
            recipeId,
            entryId,
            KnownFolder.UserProfile,
            relativePath,
            packageRelativeSource,
            RestoreStrategy.ConfigWrite,
            RestorePhase.ConfigWrite,
            Array.Empty<string>(),
            portability,
            "sha")
        {
            RestoreTier = RestoreTier.ConfigCopy,
        };

    private static void SetApprovedHash(RestoreViewModel vm, string value)
    {
        FieldInfo field = typeof(RestoreViewModel).GetField(
            "_approvedHash",
            BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(vm, value);
    }

    private sealed class Fixture : IDisposable
    {
        private readonly MigrationRestoreManifestStore _manifestStore = new();
        private readonly RestoreStateStore _stateStore = new();
        private readonly MigrationRestoreService _service;

        private Fixture(string root, string package, string profile, string stateDir, MigrationRestoreService service)
        {
            Root = root;
            Package = package;
            Profile = profile;
            StateDir = stateDir;
            _service = service;
        }

        public string Root { get; }
        public string Package { get; }
        public string Profile { get; }
        public string StateDir { get; }

        public static Fixture Create(string tag)
        {
            string root = MigrationRestoreTestData.TempDir(tag);
            string package = Path.Combine(root, "pkg");
            Directory.CreateDirectory(package);
            string profile = Path.Combine(root, "Users", "bob");
            Directory.CreateDirectory(profile);
            string stateDir = Path.Combine(root, "state");
            Directory.CreateDirectory(stateDir);

            var gate = MigrationRestoreTestData.GateForProfile(profile, Path.Combine(root, "Users"));
            var runner = new MigrationRestoreRunner(
                new RecipePathResolver(new ProfileRoots(
                    profile,
                    Path.Combine(profile, "AppData", "Roaming"),
                    Path.Combine(profile, "AppData", "Local"))),
                gate);
            var service = new MigrationRestoreService(
                runner,
                MigrationRestoreTestData.Executor(gate),
                new RestoreStateStore());

            return new Fixture(root, package, profile, stateDir, service);
        }

        public RestoreViewModel CreateViewModel()
            => new(new I18n(), _service, _manifestStore, _stateStore)
            {
                PackageDir = Package,
                StateDir = StateDir,
            };

        public void WritePayload(string packageRelativePath, string contents)
        {
            string path = Path.Combine(Package, packageRelativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, contents);
        }

        public void SaveManifest(params MigrationRestoreTarget[] targets)
            => _manifestStore.Save(
                Package,
                new MigrationRestoreManifest(MigrationRestoreManifest.CurrentSchemaVersion, targets));

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
    }
}
