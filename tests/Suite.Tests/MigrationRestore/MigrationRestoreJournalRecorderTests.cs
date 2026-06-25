using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Planning;
using Xunit;

namespace WindowsCareKit.Tests.MigrationRestore;

public class MigrationRestoreJournalRecorderTests
{
    private static readonly DateTime T0 = new(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Record_maps_done_restore_merges_to_accumulating_journal()
    {
        var existing = new RestoreJournalEntry("old", @"C:\p\old", @"C:\p\old.bak.1", "o", "n", T0.AddMinutes(-1));
        RestoreState state = RestoreState.Empty.WithJournalEntry(existing);
        var merge = Merge("a", @"C:\p\a.cfg", @"C:\p\a.cfg.bak.entry.run");
        var plan = new OperationPlan("restore", "migration-restore", new PlannedAction[] { merge }, T0);

        RestoreState updated = MigrationRestoreJournalRecorder.Record(
            plan,
            new[] { new MigrationRestoreActionResult(merge.Id, MigrationRestoreActionStatus.Done) },
            new Dictionary<string, string> { [merge.Id] = "entry-a" },
            new Dictionary<string, MigrationRestoreActionSnapshot>
            {
                [merge.Id] = new(merge.Id, true, "before", merge.BakPath, "after"),
            },
            T0,
            state);

        Assert.Equal(new[] { "old", "entry-a" }, updated.Journal.Select(j => j.EntryId).ToArray());
        RestoreJournalEntry entry = updated.Journal[1];
        Assert.Equal(merge.Destination, entry.TargetPath);
        Assert.Equal(merge.BakPath, entry.BakPath);
        Assert.Equal("before", entry.ShaBefore);
        Assert.Equal("after", entry.ShaAfter);
    }

    [Fact]
    public void Record_filters_non_done_and_non_restore_actions()
    {
        var doneMerge = Merge("done", @"C:\p\done.cfg", @"C:\p\done.cfg.bak.entry.run");
        var failedMerge = Merge("failed", @"C:\p\failed.cfg", @"C:\p\failed.cfg.bak.entry.run");
        var command = new CommandAction
        {
            FileName = @"C:\Windows\System32\winget.exe",
            Arguments = Array.Empty<string>(),
            Description = "install",
            Reason = "test",
        };
        var plan = new OperationPlan("restore", "migration-restore", new PlannedAction[] { doneMerge, failedMerge, command }, T0);

        RestoreState updated = MigrationRestoreJournalRecorder.Record(
            plan,
            new[]
            {
                new MigrationRestoreActionResult(doneMerge.Id, MigrationRestoreActionStatus.Done),
                new MigrationRestoreActionResult(failedMerge.Id, MigrationRestoreActionStatus.Failed),
                new MigrationRestoreActionResult(command.Id, MigrationRestoreActionStatus.Done),
            },
            new Dictionary<string, string>
            {
                [doneMerge.Id] = "entry-done",
                [failedMerge.Id] = "entry-failed",
                [command.Id] = "entry-command",
            },
            new Dictionary<string, MigrationRestoreActionSnapshot>
            {
                [doneMerge.Id] = new(doneMerge.Id, true, "before", doneMerge.BakPath, "after"),
                [failedMerge.Id] = new(failedMerge.Id, true, "before2", failedMerge.BakPath, "after2"),
            },
            T0,
            RestoreState.Empty);

        RestoreJournalEntry entry = Assert.Single(updated.Journal);
        Assert.Equal("entry-done", entry.EntryId);
    }

    [Fact]
    public void Record_sets_null_BakPath_when_destination_did_not_exist()
    {
        var merge = Merge("new", @"C:\p\new.cfg", @"C:\p\new.cfg.bak.entry.run");
        var plan = new OperationPlan("restore", "migration-restore", new PlannedAction[] { merge }, T0);

        RestoreState updated = MigrationRestoreJournalRecorder.Record(
            plan,
            new[] { new MigrationRestoreActionResult(merge.Id, MigrationRestoreActionStatus.Done) },
            new Dictionary<string, string> { [merge.Id] = "entry-new" },
            new Dictionary<string, MigrationRestoreActionSnapshot>
            {
                [merge.Id] = new(merge.Id, false, null, merge.BakPath, "after"),
            },
            T0,
            RestoreState.Empty);

        RestoreJournalEntry entry = Assert.Single(updated.Journal);
        Assert.Null(entry.BakPath);
        Assert.Null(entry.ShaBefore);
    }

    private static RestoreMergeAction Merge(string id, string dest, string bak)
        => new()
        {
            Id = id,
            Source = @"C:\pkg\source.cfg",
            Destination = dest,
            BakPath = bak,
            Description = "merge",
            Reason = "test",
        };
}
