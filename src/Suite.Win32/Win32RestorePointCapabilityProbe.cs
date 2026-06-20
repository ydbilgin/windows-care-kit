using System.Security.Principal;
using Microsoft.Win32;
using WindowsCareKit.Core.Modules.Uninstall;

namespace WindowsCareKit.Win32;

/// <summary>
/// The real "is System Restore enabled on the system drive?" signal. It reads the SR configuration registry
/// node, NOT the service presence — <c>SRSetRestorePointW</c> can report success when SR is OFF, so service
/// presence would be a false positive (UI decision §5). The canonical "SR is active" signal is
/// <c>HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore</c> · <c>RPSessionInterval</c> &gt; 0
/// (it is 0 when System Protection is turned off); the <c>DisableSR</c> flag (when present and non-zero) is
/// treated as a hard off. Read-only; fails closed (anything it cannot positively confirm → false).
/// </summary>
public sealed class Win32SystemRestoreConfigProbe : ISystemRestoreConfigProbe
{
    private const string SrConfigKey = @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\SystemRestore";

    /// <inheritdoc />
    public bool IsSystemRestoreEnabled()
    {
        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? sr = baseKey.OpenSubKey(SrConfigKey, writable: false);
            if (sr is null)
                return false; // no SR config node → cannot confirm enabled → fail closed

            // An explicit DisableSR=1 is a hard "off".
            if (sr.GetValue("DisableSR") is int disabled && disabled != 0)
                return false;

            // RPSessionInterval > 0 is the active-monitoring signal; 0 (or missing) means SR is not running.
            return sr.GetValue("RPSessionInterval") is int interval && interval > 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false; // cannot read the SR config → fail closed
        }
    }
}

/// <summary>The real elevation signal: true when the current process runs as a member of the Administrators role.</summary>
public sealed class Win32ElevationProbe : IElevationProbe
{
    /// <inheritdoc />
    public bool IsElevated()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            var principal = new WindowsPrincipal(identity);
            return principal.IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch (Exception)
        {
            return false; // cannot determine → fail closed
        }
    }
}
