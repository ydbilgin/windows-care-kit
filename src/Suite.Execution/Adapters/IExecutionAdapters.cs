using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// Deletes a file or directory — to the Recycle Bin by default (spec §3). Implemented by
/// <see cref="RecycleBinFileDeleteAdapter"/>. The executor guarantees the gate already approved the
/// action; the adapter still validates the target exists and throws on real failure.
/// </summary>
public interface IFileDeleteAdapter
{
    /// <summary>Delete the action's path. Throws if neither a file nor a directory exists there.</summary>
    void Delete(FileDeleteAction action);
}

/// <summary>
/// Deletes a registry key or value — but always exports a <c>.reg</c> backup FIRST (fail closed:
/// no backup → no delete). Implemented by <see cref="RegistryDeleteAdapter"/>.
/// </summary>
public interface IRegistryAdapter
{
    /// <summary>Export a <c>.reg</c> backup, then delete the value/key honoring the 64/32 view.</summary>
    void Delete(RegistryDeleteAction action);
}

/// <summary>
/// Stops / disables / deletes a Windows service through the Service Control Manager (no <c>sc.exe</c>).
/// Implemented by <see cref="ServiceControlAdapter"/>.
/// </summary>
public interface IServiceAdapter
{
    /// <summary>Apply the action's <see cref="ServiceOperation"/> to the service.</summary>
    void Apply(ServiceDeleteAction action);
}

/// <summary>
/// Disables / deletes a scheduled task through the Task Scheduler COM API (no <c>schtasks.exe</c>).
/// Implemented by <see cref="ScheduledTaskAdapter"/>.
/// </summary>
public interface ITaskAdapter
{
    /// <summary>Apply the action's <see cref="TaskOperation"/> to the task.</summary>
    void Apply(TaskDeleteAction action);
}

/// <summary>
/// Runs an executable with a structured argument list (never a shell string), elevating only when the
/// action requires it. Implemented by <see cref="ProcessAdapter"/>.
/// </summary>
public interface IProcessAdapter
{
    /// <summary>Start the process, wait for exit, and surface a non-zero exit code as a failure.</summary>
    void Run(CommandAction action);
}

/// <summary>
/// Copies files/trees for Backup, and merges a restored config onto its destination with a timestamped
/// <c>.bak</c> (never a blind overwrite). Implemented by <see cref="CopyAdapter"/>.
/// </summary>
public interface ICopyAdapter
{
    /// <summary>Copy <see cref="CopyAction.Source"/> to <see cref="CopyAction.Destination"/>.</summary>
    void Copy(CopyAction action);

    /// <summary>Back up the destination (if present and requested) to <c>.bak.&lt;timestamp&gt;</c>, then write the source onto it.</summary>
    void Merge(RestoreMergeAction action);
}
