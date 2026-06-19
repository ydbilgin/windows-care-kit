using System.Runtime.InteropServices;
using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// Disables or deletes a scheduled task through the Task Scheduler 2.0 COM API (<c>Schedule.Service</c>),
/// late-bound via <see cref="IDispatch"/> so no third-party interop package is needed. Never shells out
/// to <c>schtasks.exe</c> (spec §3). The gate has already blocked <c>\Microsoft\Windows\**</c>; this
/// adapter re-confirms the task exists and throws on a COM failure so the executor records it.
/// </summary>
public sealed class ScheduledTaskAdapter : ITaskAdapter
{
    /// <inheritdoc />
    public void Apply(TaskDeleteAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        Type? schedulerType = Type.GetTypeFromProgID("Schedule.Service");
        if (schedulerType is null)
            throw new InvalidOperationException("Task Scheduler service (Schedule.Service) is not available.");

        object? service = null;
        object? rootFolder = null;
        object? folder = null;
        try
        {
            service = Activator.CreateInstance(schedulerType)
                      ?? throw new InvalidOperationException("Could not create the Task Scheduler service object.");

            // Connect(serverName, user, domain, password) — all null = local machine, current user.
            Invoke(service, "Connect", null, null, null, null);

            SplitTaskPath(action.TaskPath, out string folderPath, out string taskName);

            rootFolder = Invoke(service, "GetFolder", "\\")
                         ?? throw new InvalidOperationException("Could not open the Task Scheduler root folder.");

            folder = folderPath == "\\"
                ? rootFolder
                : Invoke(rootFolder, "GetFolder", folderPath)
                  ?? throw new DirectoryNotFoundException($"Task folder not found: {folderPath}");

            // Re-confirm the task exists (GetTask throws if it does not).
            object? task = Invoke(folder, "GetTask", taskName)
                           ?? throw new FileNotFoundException($"Scheduled task not found: {action.TaskPath}");

            switch (action.Operation)
            {
                case TaskOperation.Disable:
                    SetProperty(task, "Enabled", false);
                    ReleaseCom(task);
                    break;

                case TaskOperation.Delete:
                    ReleaseCom(task);
                    // DeleteTask(name, flags) on the containing folder.
                    Invoke(folder, "DeleteTask", taskName, 0);
                    break;
            }
        }
        finally
        {
            if (folder is not null && !ReferenceEquals(folder, rootFolder)) ReleaseCom(folder);
            ReleaseCom(rootFolder);
            ReleaseCom(service);
        }
    }

    /// <summary>Split a full task path like <c>\Vendor\Updater</c> into folder (<c>\Vendor</c>) and name (<c>Updater</c>).</summary>
    internal static void SplitTaskPath(string taskPath, out string folderPath, out string taskName)
    {
        string p = taskPath.StartsWith('\\') ? taskPath : "\\" + taskPath;
        int idx = p.LastIndexOf('\\');
        taskName = p[(idx + 1)..];
        folderPath = idx <= 0 ? "\\" : p[..idx];
        if (taskName.Length == 0)
            throw new ArgumentException($"Invalid task path (no task name): {taskPath}", nameof(taskPath));
    }

    private static object? Invoke(object target, string member, params object?[] args)
        => target.GetType().InvokeMember(
            member,
            System.Reflection.BindingFlags.InvokeMethod,
            binder: null,
            target: target,
            args: args);

    private static void SetProperty(object target, string member, object? value)
        => target.GetType().InvokeMember(
            member,
            System.Reflection.BindingFlags.SetProperty,
            binder: null,
            target: target,
            args: new[] { value });

    private static void ReleaseCom(object? comObject)
    {
        if (comObject is not null && Marshal.IsComObject(comObject))
            Marshal.ReleaseComObject(comObject);
    }

    /// <summary>Marker for the late-bound <c>Schedule.Service</c> IDispatch surface (documentation only).</summary>
    private interface IDispatch { }
}
