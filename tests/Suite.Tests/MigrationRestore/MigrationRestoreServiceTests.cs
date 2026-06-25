using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Execution;
using Xunit;

namespace WindowsCareKit.Tests.MigrationRestore;

public class MigrationRestoreServiceTests
{
    private static readonly DateTime T0 = new(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Restore_over_preexisting_file_records_non_null_BakPath()
    {
        var fx = Setup("service-restore");
        try
        {
            File.WriteAllText(Path.Combine(fx.Profile, ".gitconfig"), "OLD");
            MigrationRestoreExecutionResult result = fx.Service.Restore(
                Manifest("git.config#0", ".gitconfig"),
                fx.Package,
                fx.StateDir,
                T0,
                "run1");

            Assert.True(result.Execution.Authorized);
            Assert.All(result.Execution.Results, r => Assert.Equal(ActionStatus.Done, r.Status));
            RestoreJournalEntry entry = Assert.Single(result.State.Journal);
            Assert.NotNull(entry.BakPath);
            Assert.True(File.Exists(entry.BakPath));
            Assert.Equal("OLD", File.ReadAllText(entry.BakPath!));
        }
        finally { Directory.Delete(fx.Root, recursive: true); }
    }

    [Fact]
    public void Undo_rejects_backup_when_disk_sha_does_not_match_journal()
    {
        var fx = Setup("service-undo");
        try
        {
            File.WriteAllText(Path.Combine(fx.Profile, ".gitconfig"), "OLD");
            MigrationRestoreExecutionResult restored = fx.Service.Restore(
                Manifest("git.config#0", ".gitconfig"),
                fx.Package,
                fx.StateDir,
                T0,
                "run1");
            string bak = Assert.Single(restored.State.Journal).BakPath!;
            File.WriteAllText(bak, "TAMPERED");

            MigrationRestoreUndoResult undo = fx.Service.Undo(restored.State, T0.AddMinutes(1));

            Assert.Empty(undo.BuildResult.Plan.Actions);
            RejectedRestoreUndoStep rejected = Assert.Single(undo.RejectedSteps);
            Assert.Equal("git.config#0", rejected.Step.EntryId);
            Assert.Contains("sha", rejected.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Empty(undo.Execution.Results);
        }
        finally { Directory.Delete(fx.Root, recursive: true); }
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

    private sealed record Fixture(string Root, string Package, string Profile, string StateDir, MigrationRestoreService Service);
}
