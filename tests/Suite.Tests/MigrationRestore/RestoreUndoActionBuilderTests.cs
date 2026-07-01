using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests.MigrationRestore;

public class RestoreUndoActionBuilderTests
{
    private static readonly DateTime T0 = new(2026, 6, 25, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Build_creates_reverse_restore_merge_actions_with_medium_risk_and_no_undo()
    {
        string root = Path.Combine(Path.GetTempPath(), "wck-undo-builder-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string a = Path.Combine(root, "a.cfg");
            string c = Path.Combine(root, "c.cfg");
            RestoreState state = RestoreState.Empty
                .WithJournalEntry(new RestoreJournalEntry("a", a, a + ".bak.entry.run", "old-a", "new-a", T0))
                .WithJournalEntry(new RestoreJournalEntry("b", Path.Combine(root, "b.cfg"), null, null, "new-b", T0.AddMinutes(1)))
                .WithJournalEntry(new RestoreJournalEntry("c", c, c + ".bak.entry.run", "old-c", "new-c", T0.AddMinutes(2)));

            RestoreUndoActionBuildResult result = RestoreUndoActionBuilder.Build(RestoreJournal.BuildUndoPlan(state), T0);

            RejectedRestoreUndoStep rejected = Assert.Single(result.RejectedSteps);
            Assert.Equal("b", rejected.Step.EntryId);
            Assert.Contains("created", rejected.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("remain", rejected.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(new[] { c, a }, result.Plan.Actions.Cast<RestoreMergeAction>().Select(x => x.Destination).ToArray());
            Assert.All(result.Plan.Actions.Cast<RestoreMergeAction>(), action =>
            {
                Assert.False(action.CreateBak);
                Assert.Equal(RiskLevel.Medium, action.Risk);
                Assert.Equal(UndoCapability.None, action.Undo);
                Assert.StartsWith("Undo restore of ", action.Description, StringComparison.Ordinal);
            });
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void Build_rejects_non_sibling_backup_paths_visibly()
    {
        string root = Path.Combine(Path.GetTempPath(), "wck-undo-builder-" + Guid.NewGuid().ToString("N"));
        string other = Path.Combine(root, "other");
        Directory.CreateDirectory(other);
        try
        {
            string target = Path.Combine(root, "a.cfg");
            var state = RestoreState.Empty.WithJournalEntry(new RestoreJournalEntry(
                "a", target, Path.Combine(other, "a.cfg.bak.entry.run"), "old", "new", T0));

            RestoreUndoActionBuildResult result = RestoreUndoActionBuilder.Build(RestoreJournal.BuildUndoPlan(state), T0);

            Assert.Empty(result.Plan.Actions);
            RejectedRestoreUndoStep rejected = Assert.Single(result.RejectedSteps);
            Assert.Equal("a", rejected.Step.EntryId);
            Assert.Contains("sibling", rejected.Reason, StringComparison.OrdinalIgnoreCase);
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
