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
