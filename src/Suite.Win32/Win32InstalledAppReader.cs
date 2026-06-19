using Microsoft.Win32;
using WindowsCareKit.Core.Modules.Uninstall;
using CoreView = WindowsCareKit.Core.Planning.RegistryView;

namespace WindowsCareKit.Win32;

/// <summary>
/// Reads classic installed-program inventory from the three uninstall locations: HKLM 64-bit,
/// HKLM 32-bit (WOW6432Node view), and HKCU. Read-only — it only enumerates and reads values.
/// </summary>
public sealed class Win32InstalledAppReader : IInstalledAppReader
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<InstalledApp> ReadAll()
    {
        var apps = new List<InstalledApp>();
        ReadFrom(RegistryHive.LocalMachine, RegistryView.Registry64, InstalledAppSource.MachineWide64, apps);
        ReadFrom(RegistryHive.LocalMachine, RegistryView.Registry32, InstalledAppSource.MachineWide32, apps);
        ReadFrom(RegistryHive.CurrentUser, RegistryView.Registry64, InstalledAppSource.CurrentUser, apps);
        return apps;
    }

    private static void ReadFrom(RegistryHive hive, RegistryView view, InstalledAppSource source, List<InstalledApp> sink)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, view);
            using var uninstall = baseKey.OpenSubKey(UninstallPath, writable: false);
            if (uninstall is null)
                return;

            foreach (string subName in uninstall.GetSubKeyNames())
            {
                InstalledApp? app = TryReadEntry(uninstall, subName, source);
                if (app is not null)
                    sink.Add(app);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A view/hive we cannot read is simply skipped; the inventory is best-effort.
        }
    }

    private static InstalledApp? TryReadEntry(RegistryKey uninstall, string subName, InstalledAppSource source)
    {
        try
        {
            using var key = uninstall.OpenSubKey(subName, writable: false);
            if (key is null)
                return null;

            string? displayName = key.GetValue("DisplayName") as string;
            if (string.IsNullOrWhiteSpace(displayName))
                return null; // entries without a display name are not user-facing programs

            return new InstalledApp
            {
                DisplayName = displayName.Trim(),
                Publisher = (key.GetValue("Publisher") as string)?.Trim(),
                DisplayVersion = (key.GetValue("DisplayVersion") as string)?.Trim(),
                InstallLocation = NormalizeNullable(key.GetValue("InstallLocation") as string),
                UninstallString = (key.GetValue("UninstallString") as string)?.Trim(),
                QuietUninstallString = (key.GetValue("QuietUninstallString") as string)?.Trim(),
                RegistryKeyName = subName,
                Source = source,
                IsSystemComponent = IsTruthy(key.GetValue("SystemComponent")),
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string? NormalizeNullable(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim().TrimEnd('\\');

    private static bool IsTruthy(object? value)
        => value is int i && i != 0;

    /// <summary>The Core <see cref="CoreView"/> equivalent of the given app, for callers that need it.</summary>
    public static CoreView ViewOf(InstalledApp app) => app.View;
}
