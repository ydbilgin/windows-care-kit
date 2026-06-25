using WindowsCareKit.Core.Modules.Install;
using Xunit;

namespace WindowsCareKit.Tests;

public class RestoreStateStoreTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly string _dir;
    private readonly RestoreStateStore _store = new();

    public RestoreStateStoreTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "wck-state-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    [Fact]
    public void Load_returns_empty_when_no_checkpoint_exists()
        => Assert.Empty(_store.Load(_dir).Entries);

    [Fact]
    public void PathFor_uses_the_fixed_checkpoint_filename()
        => Assert.Equal(Path.Combine(_dir, RestoreStateStore.FileName), _store.PathFor(_dir));

    [Fact]
    public void Save_then_load_roundtrips_the_state()
    {
        var state = RestoreState.Empty with { PlanHash = "abc", StartedUtc = T0, UpdatedUtc = T0 };
        state = state
            .With("install-git", RestoreEntryStatus.Done, T0)
            .With("install-vscode", RestoreEntryStatus.Failed, T0);

        _store.Save(_dir, state);
        var loaded = _store.Load(_dir);

        Assert.Equal("abc", loaded.PlanHash);
        Assert.Equal(2, loaded.Entries.Count);
        Assert.True(loaded.IsDone("install-git"));
        Assert.Equal(RestoreEntryStatus.Failed, loaded.StatusOf("install-vscode"));
    }

    [Fact]
    public void Save_then_load_roundtrips_package_sha_and_restore_journal()
    {
        var applied = T0.AddMinutes(5);
        var state = (RestoreState.Empty with { PlanHash = "abc", StartedUtc = T0, UpdatedUtc = T0 })
            .WithPackageSha("package-sha", T0)
            .WithJournalEntry(new RestoreJournalEntry(
                EntryId: "git.config#0",
                TargetPath: @"C:\Users\bob\.gitconfig",
                BakPath: @"C:\Users\bob\.gitconfig.bak.1",
                ShaBefore: "before",
                ShaAfter: "after",
                AppliedUtc: applied));

        _store.Save(_dir, state);
        RestoreState loaded = _store.Load(_dir);

        Assert.Equal("package-sha", loaded.PackageSha);
        RestoreJournalEntry entry = Assert.Single(loaded.Journal);
        Assert.Equal("git.config#0", entry.EntryId);
        Assert.Equal(@"C:\Users\bob\.gitconfig.bak.1", entry.BakPath);
        Assert.Equal(applied, entry.AppliedUtc);
        Assert.False(File.Exists(_store.PathFor(_dir) + ".wcktmp"));
    }

    [Fact]
    public void RestoreJournal_builds_reverse_order_undo_plan_for_entries_with_bak()
    {
        var state = RestoreState.Empty
            .WithJournalEntry(new RestoreJournalEntry("a", @"C:\target\a", @"C:\target\a.bak", "old-a", "new-a", T0))
            .WithJournalEntry(new RestoreJournalEntry("b", @"C:\target\b", null, null, "new-b", T0.AddMinutes(1)))
            .WithJournalEntry(new RestoreJournalEntry("c", @"C:\target\c", @"C:\target\c.bak", "old-c", "new-c", T0.AddMinutes(2)));

        RestoreUndoPlan plan = RestoreJournal.BuildUndoPlan(state);

        Assert.Equal(new[] { "c", "a" }, plan.Steps.Select(s => s.EntryId).ToArray());
        Assert.All(plan.Steps, s => Assert.EndsWith(".bak", s.BakPath, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void FirstUnfinished_points_at_the_resume_entry()
    {
        var state = RestoreState.Empty
            .With("a", RestoreEntryStatus.Done, T0)
            .With("b", RestoreEntryStatus.Failed, T0)
            .With("c", RestoreEntryStatus.Pending, T0);

        Assert.Equal("b", state.FirstUnfinished());
    }

    [Fact]
    public void With_replaces_an_existing_entry_rather_than_duplicating()
    {
        var state = RestoreState.Empty
            .With("a", RestoreEntryStatus.Pending, T0)
            .With("a", RestoreEntryStatus.Done, T0);

        var entry = Assert.Single(state.Entries);
        Assert.Equal(RestoreEntryStatus.Done, entry.Status);
    }

    [Fact]
    public void Corrupt_checkpoint_fails_safe_to_empty()
    {
        File.WriteAllText(_store.PathFor(_dir), "{ not valid json");
        Assert.Empty(_store.Load(_dir).Entries);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* best-effort cleanup */ }
    }
}
