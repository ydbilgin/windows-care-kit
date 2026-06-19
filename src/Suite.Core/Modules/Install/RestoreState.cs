namespace WindowsCareKit.Core.Modules.Install;

/// <summary>The persisted status of a single restore entry, for checkpoint/resume across reboots.</summary>
public enum RestoreEntryStatus
{
    /// <summary>Not yet attempted.</summary>
    Pending = 0,
    /// <summary>Completed successfully — skipped on resume.</summary>
    Done = 1,
    /// <summary>Attempted and failed — the user may retry; resume re-attempts it.</summary>
    Failed = 2,
    /// <summary>Deliberately skipped (gate-blocked, manual-after, or driver not Net) — not re-attempted.</summary>
    Skipped = 3,
}

/// <summary>One checkpoint record: an install entry id and its last-known status.</summary>
public sealed record RestoreEntryState(string EntryId, RestoreEntryStatus Status);

/// <summary>
/// The checkpoint/resume model written to <c>.kurulum_state.json</c> next to the payload dir (outside the
/// repo). A reboot during a heavy winget/driver install can resume the plan from the first non-<see
/// cref="RestoreEntryStatus.Done"/> id (spec §1.4). This is plain data — reading/writing it is IO done by
/// <see cref="IRestoreStateStore"/>; the planner consults it to skip already-completed entries.
/// </summary>
public sealed record RestoreState(
    string PlanHash,
    DateTime StartedUtc,
    DateTime UpdatedUtc,
    IReadOnlyList<RestoreEntryState> Entries)
{
    /// <summary>A fresh, empty state for a plan that has not started.</summary>
    public static RestoreState Empty { get; } =
        new(string.Empty, default, default, Array.Empty<RestoreEntryState>());

    /// <summary>True when the entry id has been recorded as <see cref="RestoreEntryStatus.Done"/>.</summary>
    public bool IsDone(string entryId)
        => Entries.Any(e => string.Equals(e.EntryId, entryId, StringComparison.OrdinalIgnoreCase)
                            && e.Status == RestoreEntryStatus.Done);

    /// <summary>The recorded status for an entry, or <see cref="RestoreEntryStatus.Pending"/> when unseen.</summary>
    public RestoreEntryStatus StatusOf(string entryId)
        => Entries.FirstOrDefault(e => string.Equals(e.EntryId, entryId, StringComparison.OrdinalIgnoreCase))
               ?.Status ?? RestoreEntryStatus.Pending;

    /// <summary>The first entry id that is not yet <see cref="RestoreEntryStatus.Done"/> (resume point), or null.</summary>
    public string? FirstUnfinished()
        => Entries.FirstOrDefault(e => e.Status != RestoreEntryStatus.Done)?.EntryId;

    /// <summary>
    /// Returns a new state with <paramref name="entryId"/> set to <paramref name="status"/> and a fresh
    /// <see cref="UpdatedUtc"/>. Existing entries are preserved; an unseen id is appended.
    /// </summary>
    public RestoreState With(string entryId, RestoreEntryStatus status, DateTime utc)
    {
        var list = new List<RestoreEntryState>(Entries.Count + 1);
        bool replaced = false;
        foreach (RestoreEntryState e in Entries)
        {
            if (string.Equals(e.EntryId, entryId, StringComparison.OrdinalIgnoreCase))
            {
                list.Add(e with { Status = status });
                replaced = true;
            }
            else
            {
                list.Add(e);
            }
        }
        if (!replaced)
            list.Add(new RestoreEntryState(entryId, status));

        return this with { Entries = list, UpdatedUtc = utc };
    }
}
