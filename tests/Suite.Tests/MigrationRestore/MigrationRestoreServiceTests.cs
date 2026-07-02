using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Execution;
using Xunit;
using WindowsCareKit.Tests.TestInfra;

namespace WindowsCareKit.Tests.MigrationRestore;

public class MigrationRestoreServiceTests
{
    private static readonly DateTime T0 = new(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Preview_is_side_effect_free_and_hash_authorizes_restore()
    {
        var fx = Setup("service-preview");
        try
        {
            string destination = Path.Combine(fx.Profile, ".gitconfig");
            File.WriteAllText(destination, "OLD");
            IReadOnlyDictionary<string, string> before = SnapshotFiles(fx.Root);

            MigrationRestorePreviewResult preview = fx.Service.Preview(
                Manifest("git.config#0", ".gitconfig"),
                fx.Package,
                fx.StateDir,
                T0);

            Assert.Equal(before, SnapshotFiles(fx.Root));
            Assert.Equal(preview.PlanResult.Plan.ComputeHash(), preview.PlanHash);
            Assert.False(File.Exists(new RestoreStateStore().PathFor(fx.StateDir)));
            Assert.Empty(Directory.EnumerateFiles(fx.Root, "*.bak.*", SearchOption.AllDirectories));
            Assert.Single(preview.RestoreReport.Restored);

            MigrationRestoreExecutionResult restored = fx.Service.Restore(
                Manifest("git.config#0", ".gitconfig"),
                fx.Package,
                fx.StateDir,
                T0,
                approvedHash: preview.PlanHash);

            Assert.True(restored.Authorized);
            Assert.True(restored.Execution.Authorized);
            Assert.Equal(preview.PlanHash, restored.Execution.PlanHash);
            Assert.Equal("NEW", File.ReadAllText(destination));
        }
        finally { TestFs.DeleteResilient(fx.Root); }
    }

    [Fact]
    public void Restore_with_tampered_approved_hash_refuses_before_mutation()
    {
        var fx = Setup("service-refused");
        try
        {
            string destination = Path.Combine(fx.Profile, ".gitconfig");
            File.WriteAllText(destination, "OLD");

            MigrationRestoreExecutionResult result = fx.Service.Restore(
                Manifest("git.config#0", ".gitconfig"),
                fx.Package,
                fx.StateDir,
                T0,
                approvedHash: "tampered");

            Assert.False(result.Authorized);
            Assert.False(result.Execution.Authorized);
            Assert.Empty(result.Execution.Results);
            Assert.Equal("OLD", File.ReadAllText(destination));
            Assert.False(File.Exists(new RestoreStateStore().PathFor(fx.StateDir)));
            Assert.Empty(Directory.EnumerateFiles(fx.Root, "*.bak.*", SearchOption.AllDirectories));
            Assert.Empty(result.State.Journal);
            Assert.Empty(result.RestoreReport.Restored);
            Assert.Empty(result.RestoreReport.ReinstallEnqueued);
            Assert.Empty(result.RestoreReport.Manual);
        }
        finally { TestFs.DeleteResilient(fx.Root); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Restore_without_approved_hash_refuses_before_mutation(string? approvedHash)
    {
        var fx = Setup("service-no-approval");
        try
        {
            string destination = Path.Combine(fx.Profile, ".gitconfig");

            MigrationRestoreExecutionResult result = fx.Service.Restore(
                Manifest("git.config#0", ".gitconfig"),
                fx.Package,
                fx.StateDir,
                T0,
                approvedHash: approvedHash);

            Assert.False(result.Authorized);
            Assert.False(result.Execution.Authorized);
            Assert.Empty(result.Execution.Results);
            Assert.False(File.Exists(destination));
            Assert.False(File.Exists(new RestoreStateStore().PathFor(fx.StateDir)));
            Assert.Empty(Directory.EnumerateFiles(fx.Root, "*.bak.*", SearchOption.AllDirectories));
            Assert.Empty(result.State.Journal);
            Assert.Empty(result.RestoreReport.Restored);
        }
        finally { TestFs.DeleteResilient(fx.Root); }
    }

    [Fact]
    public void Restore_over_preexisting_file_records_non_null_BakPath()
    {
        var fx = Setup("service-restore");
        try
        {
            File.WriteAllText(Path.Combine(fx.Profile, ".gitconfig"), "OLD");
            MigrationRestorePreviewResult preview = fx.Service.Preview(
                Manifest("git.config#0", ".gitconfig"),
                fx.Package,
                fx.StateDir,
                T0);
            MigrationRestoreExecutionResult result = fx.Service.Restore(
                Manifest("git.config#0", ".gitconfig"),
                fx.Package,
                fx.StateDir,
                T0,
                "run1",
                approvedHash: preview.PlanHash);

            Assert.True(result.Execution.Authorized);
            Assert.All(result.Execution.Results, r => Assert.Equal(ActionStatus.Done, r.Status));
            RestoreJournalEntry entry = Assert.Single(result.State.Journal);
            Assert.NotNull(entry.BakPath);
            Assert.True(File.Exists(entry.BakPath));
            Assert.Equal("OLD", File.ReadAllText(entry.BakPath!));
        }
        finally { TestFs.DeleteResilient(fx.Root); }
    }

    [Fact]
    public void PreviewUndo_surfaces_created_files_as_rejected_rows()
    {
        var fx = Setup("service-created-undo");
        try
        {
            string destination = Path.Combine(fx.Profile, ".gitconfig");
            MigrationRestorePreviewResult preview = fx.Service.Preview(
                Manifest("git.config#0", ".gitconfig"),
                fx.Package,
                fx.StateDir,
                T0);
            MigrationRestoreExecutionResult restored = fx.Service.Restore(
                Manifest("git.config#0", ".gitconfig"),
                fx.Package,
                fx.StateDir,
                T0,
                "run1",
                approvedHash: preview.PlanHash);

            Assert.Equal("NEW", File.ReadAllText(destination));
            RestoreJournalEntry journal = Assert.Single(restored.State.Journal);
            Assert.Null(journal.BakPath);

            MigrationRestoreUndoPreviewResult undoPreview = fx.Service.PreviewUndo(restored.State, T0.AddMinutes(1));

            Assert.Empty(undoPreview.BuildResult.Plan.Actions);
            RejectedRestoreUndoStep rejected = Assert.Single(undoPreview.RejectedSteps);
            Assert.Equal("git.config#0", rejected.Step.EntryId);
            Assert.Equal(destination, rejected.Step.TargetPath);
            Assert.Contains("created", rejected.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("remain", rejected.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { TestFs.DeleteResilient(fx.Root); }
    }

    [Fact]
    public void Undo_rejects_backup_when_disk_sha_does_not_match_journal()
    {
        var fx = Setup("service-undo");
        try
        {
            File.WriteAllText(Path.Combine(fx.Profile, ".gitconfig"), "OLD");
            MigrationRestorePreviewResult restorePreview = fx.Service.Preview(
                Manifest("git.config#0", ".gitconfig"),
                fx.Package,
                fx.StateDir,
                T0);
            MigrationRestoreExecutionResult restored = fx.Service.Restore(
                Manifest("git.config#0", ".gitconfig"),
                fx.Package,
                fx.StateDir,
                T0,
                "run1",
                approvedHash: restorePreview.PlanHash);
            string bak = Assert.Single(restored.State.Journal).BakPath!;
            File.WriteAllText(bak, "TAMPERED");

            MigrationRestoreUndoPreviewResult preview = fx.Service.PreviewUndo(restored.State, T0.AddMinutes(1));
            MigrationRestoreUndoResult undo = fx.Service.Undo(
                restored.State,
                fx.StateDir,
                T0.AddMinutes(1),
                preview.PlanHash);

            Assert.Empty(undo.BuildResult.Plan.Actions);
            RejectedRestoreUndoStep rejected = Assert.Single(undo.RejectedSteps);
            Assert.Equal("git.config#0", rejected.Step.EntryId);
            Assert.Contains("sha", rejected.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(undo.Execution.Results);
        }
        finally { TestFs.DeleteResilient(fx.Root); }
    }

    [Fact]
    public void Undo_without_approved_hash_refuses_before_mutation()
    {
        var fx = Setup("service-undo-no-approval");
        try
        {
            string destination = Path.Combine(fx.Profile, ".gitconfig");
            File.WriteAllText(destination, "OLD");
            MigrationRestorePreviewResult restorePreview = fx.Service.Preview(
                Manifest("git.config#0", ".gitconfig"),
                fx.Package,
                fx.StateDir,
                T0);
            MigrationRestoreExecutionResult restored = fx.Service.Restore(
                Manifest("git.config#0", ".gitconfig"),
                fx.Package,
                fx.StateDir,
                T0,
                "run1",
                approvedHash: restorePreview.PlanHash);

            Assert.Equal("NEW", File.ReadAllText(destination));

            MigrationRestoreUndoResult undo = fx.Service.Undo(restored.State, fx.StateDir, T0.AddMinutes(1));

            Assert.False(undo.Authorized);
            Assert.False(undo.Execution.Authorized);
            Assert.Empty(undo.Execution.Results);
            Assert.Equal("NEW", File.ReadAllText(destination));
        }
        finally { TestFs.DeleteResilient(fx.Root); }
    }

    [Fact]
    public void Undo_with_tampered_approved_hash_refuses_before_mutation()
    {
        var fx = Setup("service-undo-tampered");
        try
        {
            string destination = Path.Combine(fx.Profile, ".gitconfig");
            File.WriteAllText(destination, "OLD");
            MigrationRestorePreviewResult restorePreview = fx.Service.Preview(
                Manifest("git.config#0", ".gitconfig"),
                fx.Package,
                fx.StateDir,
                T0);
            MigrationRestoreExecutionResult restored = fx.Service.Restore(
                Manifest("git.config#0", ".gitconfig"),
                fx.Package,
                fx.StateDir,
                T0,
                "run1",
                approvedHash: restorePreview.PlanHash);

            Assert.Equal("NEW", File.ReadAllText(destination));

            MigrationRestoreUndoPreviewResult preview = fx.Service.PreviewUndo(restored.State, T0.AddMinutes(1));
            Assert.Equal(preview.BuildResult.Plan.ComputeHash(), preview.PlanHash);
            MigrationRestoreUndoResult undo = fx.Service.Undo(
                restored.State,
                fx.StateDir,
                T0.AddMinutes(1),
                "tampered");

            Assert.False(undo.Authorized);
            Assert.False(undo.Execution.Authorized);
            Assert.Empty(undo.Execution.Results);
            Assert.Equal("NEW", File.ReadAllText(destination));
        }
        finally { TestFs.DeleteResilient(fx.Root); }
    }

    [Fact]
    public void Undo_clears_checkpoint_so_restore_can_rerun()
    {
        var fx = Setup("service-undo-checkpoint");
        try
        {
            string destination = Path.Combine(fx.Profile, ".gitconfig");
            File.WriteAllText(destination, "OLD");
            var manifest = Manifest("git.config#0", ".gitconfig");
            MigrationRestorePreviewResult preview = fx.Service.Preview(manifest, fx.Package, fx.StateDir, T0);
            MigrationRestoreExecutionResult restored = fx.Service.Restore(
                manifest,
                fx.Package,
                fx.StateDir,
                T0,
                "run1",
                approvedHash: preview.PlanHash);
            Assert.True(restored.State.IsDone("git.config#0"));

            MigrationRestoreUndoPreviewResult undoPreview = fx.Service.PreviewUndo(restored.State, T0.AddMinutes(1));
            MigrationRestoreUndoResult undo = fx.Service.Undo(
                restored.State,
                fx.StateDir,
                T0.AddMinutes(1),
                undoPreview.PlanHash);

            Assert.True(undo.Authorized);
            Assert.Equal("OLD", File.ReadAllText(destination));
            RestoreState afterUndo = new RestoreStateStore().Load(fx.StateDir);
            Assert.False(afterUndo.IsDone("git.config#0"));
            Assert.Equal(RestoreEntryStatus.Pending, afterUndo.StatusOf("git.config#0"));

            MigrationRestorePreviewResult rerunPreview = fx.Service.Preview(
                manifest,
                fx.Package,
                fx.StateDir,
                T0.AddMinutes(2));
            Assert.Single(rerunPreview.PlanResult.Plan.Actions);
            Assert.DoesNotContain(rerunPreview.PlanResult.Skipped, s => s.Reason == RestoreSkipReason.AlreadyDone);

            MigrationRestoreExecutionResult rerun = fx.Service.Restore(
                manifest,
                fx.Package,
                fx.StateDir,
                T0.AddMinutes(2),
                "rerun",
                approvedHash: rerunPreview.PlanHash);
            Assert.True(rerun.Authorized);
            Assert.Equal("NEW", File.ReadAllText(destination));
        }
        finally { TestFs.DeleteResilient(fx.Root); }
    }

    private static Fixture Setup(string tag)
    {
        string root = MigrationRestoreTestData.TempDir(tag);
        string pkg = Path.Combine(root, "pkg");
        Directory.CreateDirectory(Path.Combine(pkg, "migration", "x"));
        File.WriteAllText(Path.Combine(pkg, "migration", "x", "settings.json"), "NEW");

        string profile = Path.Combine(root, "Users", "bob");
        Directory.CreateDirectory(profile);
        string stateDir = Path.Combine(root, "state");
        Directory.CreateDirectory(stateDir);

        var gate = MigrationRestoreTestData.GateForProfile(profile, Path.Combine(root, "Users"));
        var runner = new MigrationRestoreRunner(
            new RecipePathResolver(new ProfileRoots(profile, Path.Combine(profile, "AppData", "Roaming"), Path.Combine(profile, "AppData", "Local"))),
            gate);
        var service = new MigrationRestoreService(runner, MigrationRestoreTestData.Executor(gate), new RestoreStateStore());
        return new Fixture(root, pkg, profile, stateDir, service);
    }

    private static MigrationRestoreManifest Manifest(string entryId, string relativePath)
        => new(1, new[]
        {
            new MigrationRestoreTarget("git.config", entryId, KnownFolder.UserProfile, relativePath,
                "migration/x/settings.json", RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite,
                Array.Empty<string>(), PortabilityClass.ProfileRelative, "sha")
            {
                RestoreTier = RestoreTier.ConfigCopy,
            },
        });

    private static IReadOnlyDictionary<string, string> SnapshotFiles(string root)
        => Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                path => Path.GetRelativePath(root, path),
                File.ReadAllText,
                StringComparer.OrdinalIgnoreCase);

    private sealed record Fixture(string Root, string Package, string Profile, string StateDir, MigrationRestoreService Service);
}
