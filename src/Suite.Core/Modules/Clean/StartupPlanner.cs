using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Clean;

/// <summary>
/// Builds the typed dry-run plan that disables a single startup entry. A Run/RunOnce entry becomes a
/// value-delete <see cref="RegistryDeleteAction"/> on the Run key (the gate's <c>ValueDeleteAllowedKeys</c>
/// permits a value-delete on Run/RunOnce; a key-delete there would be refused). A Startup-folder entry
/// becomes a recycle-bin <see cref="FileDeleteAction"/> on the <c>.lnk</c>. It NEVER emits a key-delete
/// on a protected key. Pure: it produces a plan; it executes nothing (spec §1.2, §3).
/// </summary>
public static class StartupPlanner
{
    /// <summary>
    /// Build the one-action plan that disables <paramref name="entry"/>. The action is gate-checked at
    /// authorize/execute time like every other plan; this method only shapes the typed action.
    /// </summary>
    public static OperationPlan BuildDisablePlan(StartupEntry entry, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(entry);

        PlannedAction action = entry.Source == StartupSource.StartupFolder
            ? BuildFolderDelete(entry)
            : BuildRegistryValueDelete(entry);

        return new OperationPlan($"Disable startup entry: {entry.Name}", "clean", new[] { action }, utc);
    }

    private static FileDeleteAction BuildFolderDelete(StartupEntry entry)
    {
        string path = entry.FolderPath
            ?? throw new ArgumentException("Startup-folder entry has no .lnk path.", nameof(entry));

        return new FileDeleteAction
        {
            Path = path,
            ToRecycleBin = true,
            Description = $"Remove startup shortcut: {entry.Name}",
            Reason = "Disable startup entry (shortcut moved to the Recycle Bin)",
            Risk = RiskLevel.Low,
            Undo = UndoCapability.Full, // recycle bin
        };
    }

    private static RegistryDeleteAction BuildRegistryValueDelete(StartupEntry entry)
    {
        return new RegistryDeleteAction
        {
            Hive = entry.Hive,
            SubKeyPath = entry.SubKeyPath,
            ValueName = entry.Name,
            View = RegistryView.Registry64,
            Description = $"Disable startup entry: {entry.Name}",
            Reason = "Disable startup entry",
            Risk = RiskLevel.Medium,
            Undo = UndoCapability.Partial, // .reg export before delete
        };
    }
}
