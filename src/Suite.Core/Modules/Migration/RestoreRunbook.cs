namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>
/// Pure M4 restore ordering: the runbook is an orchestration layer on top of the existing three
/// <see cref="RestorePhase"/> values, not a new enum. Reinstall actions are added by the runner before these
/// target phases; config targets then run install-phase seed data, first-run seed data, and plain config writes.
/// </summary>
public static class RestoreRunbook
{
    /// <summary>Deterministically order restore targets by their recipe phase, preserving input order inside a phase.</summary>
    public static IReadOnlyList<MigrationRestoreTarget> OrderTargets(IEnumerable<MigrationRestoreTarget> targets)
    {
        ArgumentNullException.ThrowIfNull(targets);
        return targets
            .Select((target, index) => (target, index))
            .OrderBy(x => PhaseRank(x.target.RestorePhase))
            .ThenBy(x => x.index)
            .Select(x => x.target)
            .ToArray();
    }

    public static int PhaseRank(RestorePhase phase) => phase switch
    {
        RestorePhase.Install => 0,
        RestorePhase.FirstRunSeed => 1,
        RestorePhase.ConfigWrite => 2,
        _ => 99,
    };
}
