using Microsoft.Win32;
using WindowsCareKit.Core.Modules.Install;

namespace WindowsCareKit.Win32;

/// <summary>
/// <see cref="IDriverGuard"/> that confirms a driver belongs to the Windows network device class
/// (<c>Class=Net</c>) before the planner is allowed to emit any driver-restore action (spec §1.4: NO
/// all-driver restore). It reads the installed network class registry node
/// (<c>HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}</c>) — the
/// canonical "Net" class GUID — and matches the supplied identifier against the registered net drivers
/// (by <c>DriverDesc</c>, <c>MatchingDeviceId</c>, <c>ProviderName</c>, or the class subkey). Read-only;
/// it fails closed — anything it cannot positively confirm as a net driver returns false.
/// </summary>
public sealed class Win32DriverGuard : IDriverGuard
{
    /// <summary>The Windows device setup class GUID for "Net" (network adapters).</summary>
    public const string NetClassGuid = "{4d36e972-e325-11ce-bfc1-08002be10318}";

    private const string ClassRoot = @"SYSTEM\CurrentControlSet\Control\Class\" + NetClassGuid;

    /// <inheritdoc />
    public bool IsNetClass(string driverIdentifier)
    {
        if (string.IsNullOrWhiteSpace(driverIdentifier))
            return false;

        string needle = driverIdentifier.Trim();

        // A caller may pass the class GUID itself — that is unambiguously the Net class.
        if (needle.Equals(NetClassGuid, StringComparison.OrdinalIgnoreCase))
            return true;

        try
        {
            using RegistryKey baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using RegistryKey? classKey = baseKey.OpenSubKey(ClassRoot, writable: false);
            if (classKey is null)
                return false;

            foreach (string instanceName in classKey.GetSubKeyNames())
            {
                // Instance subkeys are four-digit indexes like "0000", "0001"; Properties/Configuration are not.
                if (instanceName.Length != 4 || !instanceName.All(char.IsDigit))
                    continue;

                try
                {
                    using RegistryKey? instance = classKey.OpenSubKey(instanceName, writable: false);
                    if (instance is null)
                        continue;

                    if (MatchesNetInstance(instance, needle))
                        return true;
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // skip an instance we cannot read
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false; // cannot read the class node → fail closed
        }

        return false;
    }

    private static bool MatchesNetInstance(RegistryKey instance, string needle)
    {
        foreach (string valueName in new[] { "DriverDesc", "MatchingDeviceId", "ProviderName", "InfSection", "DriverVersion" })
        {
            if (instance.GetValue(valueName) is string s
                && !string.IsNullOrWhiteSpace(s)
                && s.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }
}
