using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Modules.Migration;

public enum MigrationRestoreActionStatus
{
    NotRun = 0,
    Done = 1,
    Failed = 2,
    Blocked = 3,
}

public sealed record MigrationRestoreActionResult(string ActionId, MigrationRestoreActionStatus Status);

public sealed record MigrationRestoreActionSnapshot(
    string ActionId,
    bool DestExisted,
    string? ShaBefore,
    string? BakPath,
    string? ShaAfter);

/// <summary>Pure journal mapper for executed migration restore plans. It performs no filesystem IO.</summary>
public static class MigrationRestoreJournalRecorder
{
    public static RestoreState Record(
        OperationPlan executedPlan,
        IReadOnlyList<MigrationRestoreActionResult> results,
        IReadOnlyDictionary<string, string> actionEntryIds,
        IReadOnlyDictionary<string, MigrationRestoreActionSnapshot> snapshots,
        DateTime appliedUtc,
        RestoreState previousState)
    {
        ArgumentNullException.ThrowIfNull(executedPlan);
        ArgumentNullException.ThrowIfNull(results);
        ArgumentNullException.ThrowIfNull(actionEntryIds);
        ArgumentNullException.ThrowIfNull(snapshots);
        ArgumentNullException.ThrowIfNull(previousState);

        var byActionId = results.ToDictionary(r => r.ActionId, StringComparer.Ordinal);
        RestoreState state = previousState;

        foreach (RestoreMergeAction action in executedPlan.Actions.OfType<RestoreMergeAction>())
        {
            if (!byActionId.TryGetValue(action.Id, out MigrationRestoreActionResult? result)
                || result.Status != MigrationRestoreActionStatus.Done
                || !actionEntryIds.TryGetValue(action.Id, out string? entryId)
                || !snapshots.TryGetValue(action.Id, out MigrationRestoreActionSnapshot? snapshot))
            {
                continue;
            }

            state = state.WithJournalEntry(new RestoreJournalEntry(
                entryId,
                action.Destination,
                snapshot.DestExisted ? snapshot.BakPath : null,
                snapshot.DestExisted ? snapshot.ShaBefore : null,
                snapshot.ShaAfter,
                appliedUtc));
        }

        return state;
    }
}
