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

/// <summary>One applied restore write, persisted in the checkpoint so per-item undo can be planned later.</summary>
public sealed record RestoreJournalEntry(
    string EntryId,
    string TargetPath,
    string? BakPath,
    string? ShaBefore,
    string? ShaAfter,
    DateTime AppliedUtc);

/// <summary>One pure undo step derived from a restore journal entry.</summary>
public sealed record RestoreUndoStep(
    string EntryId,
    string TargetPath,
    string? BakPath,
    string Note);

public sealed record RestoreUndoPlan(IReadOnlyList<RestoreUndoStep> Steps);

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

    /// <summary>Package-level integrity value checked before resume. Empty means no package hash was recorded.</summary>
    public string PackageSha { get; init; } = string.Empty;

    /// <summary>Per-item file restore journal. This is additive to checkpoint entries and does not affect resume status.</summary>
    public IReadOnlyList<RestoreJournalEntry> Journal { get; init; } = Array.Empty<RestoreJournalEntry>();

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

    /// <summary>Return a copy with the package integrity hash recorded for resume checks.</summary>
    public RestoreState WithPackageSha(string packageSha, DateTime utc)
        => this with { PackageSha = packageSha ?? string.Empty, UpdatedUtc = utc };

    /// <summary>Append one applied-write journal entry without changing the entry checkpoint status.</summary>
    public RestoreState WithJournalEntry(RestoreJournalEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        var list = new List<RestoreJournalEntry>(Journal.Count + 1);
        list.AddRange(Journal);
        list.Add(entry);
        return this with { Journal = list, UpdatedUtc = entry.AppliedUtc };
    }
}

/// <summary>Pure undo-plan builder. Actual undo execution remains a separately gated VM-proven path.</summary>
public static class RestoreJournal
{
    public static RestoreUndoPlan BuildUndoPlan(RestoreState state)
    {
        ArgumentNullException.ThrowIfNull(state);

        var steps = state.Journal
            .Reverse()
            .Select(e => new RestoreUndoStep(
                e.EntryId,
                e.TargetPath,
                e.BakPath,
                string.IsNullOrWhiteSpace(e.BakPath)
                    ? "Restore created this file; undo restores overwritten files only, so this file will remain."
                    : "Restore the saved .bak over the target through the gated file-restore path."))
            .ToArray();

        return new RestoreUndoPlan(steps);
    }
}
