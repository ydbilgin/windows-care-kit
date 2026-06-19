using System.ComponentModel;
using System.Runtime.InteropServices;
using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// Stops, disables, or deletes a Windows service through the Service Control Manager (advapi32 P/Invoke).
/// Never shells out to <c>sc.exe</c> (a banned string-execution path, spec §3). The gate has already
/// blocked critical services; this adapter re-confirms the service exists and throws on any SCM failure
/// so the executor records it.
/// </summary>
public sealed class ServiceControlAdapter : IServiceAdapter
{
    /// <inheritdoc />
    public void Apply(ServiceDeleteAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        using var scm = ScmHandle.OpenManager();
        uint access = action.Operation switch
        {
            ServiceOperation.Stop => SERVICE_STOP | SERVICE_QUERY_STATUS,
            ServiceOperation.Disable => SERVICE_CHANGE_CONFIG,
            ServiceOperation.Delete => SERVICE_STOP | SERVICE_QUERY_STATUS | DELETE,
            _ => SERVICE_QUERY_STATUS,
        };

        using var svc = scm.OpenService(action.ServiceName, access);

        switch (action.Operation)
        {
            case ServiceOperation.Stop:
                StopIfRunning(svc);
                break;

            case ServiceOperation.Disable:
                if (!ChangeServiceConfig(
                        svc.DangerousHandle, SERVICE_NO_CHANGE, SERVICE_DISABLED, SERVICE_NO_CHANGE,
                        null, null, IntPtr.Zero, null, null, null, null))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"ChangeServiceConfig (disable) failed for '{action.ServiceName}'.");
                break;

            case ServiceOperation.Delete:
                // Best practice: stop first (reduces "marked for deletion until next reboot"), then delete.
                StopIfRunning(svc);
                if (!DeleteService(svc.DangerousHandle))
                    throw new Win32Exception(Marshal.GetLastWin32Error(), $"DeleteService failed for '{action.ServiceName}'.");
                break;
        }
    }

    private static void StopIfRunning(ScmHandle svc)
    {
        var status = default(SERVICE_STATUS);
        if (!QueryServiceStatus(svc.DangerousHandle, ref status))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "QueryServiceStatus failed.");

        if (status.dwCurrentState == SERVICE_STOPPED)
            return;

        if (!ControlService(svc.DangerousHandle, SERVICE_CONTROL_STOP, ref status))
        {
            int err = Marshal.GetLastWin32Error();
            // ERROR_SERVICE_NOT_ACTIVE (1062): already stopped — tolerate.
            if (err != ERROR_SERVICE_NOT_ACTIVE)
                throw new Win32Exception(err, "ControlService(STOP) failed.");
        }
    }

    // ---- SCM handle wrapper ----------------------------------------------------------------

    private sealed class ScmHandle : IDisposable
    {
        public IntPtr DangerousHandle { get; }

        private ScmHandle(IntPtr handle) => DangerousHandle = handle;

        public static ScmHandle OpenManager()
        {
            IntPtr h = OpenSCManager(null, null, SC_MANAGER_CONNECT);
            if (h == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "OpenSCManager failed (need elevation?).");
            return new ScmHandle(h);
        }

        public ScmHandle OpenService(string name, uint access)
        {
            IntPtr h = OpenServiceW(DangerousHandle, name, access);
            if (h == IntPtr.Zero)
            {
                int err = Marshal.GetLastWin32Error();
                throw new Win32Exception(err,
                    err == ERROR_SERVICE_DOES_NOT_EXIST
                        ? $"Service '{name}' does not exist."
                        : $"OpenService failed for '{name}'.");
            }
            return new ScmHandle(h);
        }

        public void Dispose()
        {
            if (DangerousHandle != IntPtr.Zero)
                CloseServiceHandle(DangerousHandle);
        }
    }

    // ---- constants -------------------------------------------------------------------------

    private const uint SC_MANAGER_CONNECT = 0x0001;
    private const uint SERVICE_QUERY_STATUS = 0x0004;
    private const uint SERVICE_STOP = 0x0020;
    private const uint SERVICE_CHANGE_CONFIG = 0x0002;
    private const uint DELETE = 0x00010000;

    private const uint SERVICE_CONTROL_STOP = 0x00000001;
    private const uint SERVICE_STOPPED = 0x00000001;

    private const uint SERVICE_NO_CHANGE = 0xFFFFFFFF;
    private const uint SERVICE_DISABLED = 0x00000004;

    private const int ERROR_SERVICE_DOES_NOT_EXIST = 1060;
    private const int ERROR_SERVICE_NOT_ACTIVE = 1062;

    // ---- P/Invoke --------------------------------------------------------------------------

    [StructLayout(LayoutKind.Sequential)]
    private struct SERVICE_STATUS
    {
        public uint dwServiceType;
        public uint dwCurrentState;
        public uint dwControlsAccepted;
        public uint dwWin32ExitCode;
        public uint dwServiceSpecificExitCode;
        public uint dwCheckPoint;
        public uint dwWaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManager(string? lpMachineName, string? lpDatabaseName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr hSCManager, string lpServiceName, uint dwDesiredAccess);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr hSCObject);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr hService, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr hService, uint dwControl, ref SERVICE_STATUS lpServiceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteService(IntPtr hService);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ChangeServiceConfig(
        IntPtr hService,
        uint dwServiceType,
        uint dwStartType,
        uint dwErrorControl,
        string? lpBinaryPathName,
        string? lpLoadOrderGroup,
        IntPtr lpdwTagId,
        string? lpDependencies,
        string? lpServiceStartName,
        string? lpPassword,
        string? lpDisplayName);
}
