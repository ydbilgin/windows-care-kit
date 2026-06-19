using Microsoft.Win32;
using CoreHive = WindowsCareKit.Core.Planning.RegistryHive;
using CoreView = WindowsCareKit.Core.Planning.RegistryView;

namespace WindowsCareKit.Win32;

/// <summary>Maps the Core registry enums to the BCL ones and opens base keys per view (no hardcoded
/// Wow6432Node; both views are opened explicitly — spec §1.1, §4).</summary>
internal static class RegistryInterop
{
    public static RegistryHive ToBcl(CoreHive hive) => hive switch
    {
        CoreHive.ClassesRoot => RegistryHive.ClassesRoot,
        CoreHive.CurrentUser => RegistryHive.CurrentUser,
        CoreHive.LocalMachine => RegistryHive.LocalMachine,
        CoreHive.Users => RegistryHive.Users,
        CoreHive.CurrentConfig => RegistryHive.CurrentConfig,
        _ => RegistryHive.LocalMachine,
    };

    public static RegistryView ToBcl(CoreView view)
        => view == CoreView.Registry32 ? RegistryView.Registry32 : RegistryView.Registry64;

    public static RegistryKey OpenBase(CoreHive hive, CoreView view)
        => RegistryKey.OpenBaseKey(ToBcl(hive), ToBcl(view));

    /// <summary>True when the subkey exists (read-only existence probe).</summary>
    public static bool SubKeyExists(CoreHive hive, CoreView view, string subKey)
    {
        try
        {
            using var baseKey = OpenBase(hive, view);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            return key is not null;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }
}
