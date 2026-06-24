using Microsoft.Win32;
using WindowsCareKit.Core.Modules.Uninstall;
using CoreView = WindowsCareKit.Core.Planning.RegistryView;
using CoreHive = WindowsCareKit.Core.Planning.RegistryHive;

namespace WindowsCareKit.Win32;

/// <summary>
/// Reads classic installed-program inventory from the three uninstall locations: HKLM 64-bit,
/// HKLM 32-bit (WOW6432Node view), and HKCU. Read-only — it only enumerates and reads values.
/// </summary>
public sealed class Win32InstalledAppReader : IInstalledAppReader
{
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
    private readonly IRegistryProbe _registry;

    public Win32InstalledAppReader()
        : this(new Win32RegistryProbe())
    {
    }

    public Win32InstalledAppReader(IRegistryProbe registry)
        => _registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public IReadOnlyList<InstalledApp> ReadAll()
    {
        var apps = new List<InstalledApp>();
        ReadFrom(CoreHive.LocalMachine, CoreView.Registry64, InstalledAppSource.MachineWide64, apps);
        ReadFrom(CoreHive.LocalMachine, CoreView.Registry32, InstalledAppSource.MachineWide32, apps);
        ReadFrom(CoreHive.CurrentUser, CoreView.Registry64, InstalledAppSource.CurrentUser, apps);
        return apps;
    }

    private void ReadFrom(CoreHive hive, CoreView view, InstalledAppSource source, List<InstalledApp> sink)
    {
        try
        {
            foreach (string subName in _registry.GetSubKeyNames(hive, view, UninstallPath))
            {
                InstalledApp? app = TryReadEntry(hive, view, subName, source);
                if (app is not null)
                    sink.Add(app);
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // A view/hive we cannot read is simply skipped; the inventory is best-effort.
        }
    }

    private InstalledApp? TryReadEntry(CoreHive hive, CoreView view, string subName, InstalledAppSource source)
    {
        try
        {
            RegistryKeySnapshot? key = _registry.ReadKey(hive, view, $@"{UninstallPath}\{subName}");
            if (key is null)
                return null;

            string? displayName = key.GetString("DisplayName");
            if (string.IsNullOrWhiteSpace(displayName))
                return null; // entries without a display name are not user-facing programs

            return new InstalledApp
            {
                DisplayName = displayName.Trim(),
                Publisher = key.GetString("Publisher"),
                DisplayVersion = key.GetString("DisplayVersion"),
                InstallLocation = NormalizeNullable(key.GetString("InstallLocation")),
                UninstallString = key.GetString("UninstallString"),
                QuietUninstallString = key.GetString("QuietUninstallString"),
                RegistryKeyName = subName,
                Source = source,
                IsSystemComponent = key.IsTruthy("SystemComponent"),
                // Cheap registry values — vendor-reported figures, never a disk scan (spec "Sahip kararları").
                EstimatedSizeKb = key.GetDword("EstimatedSize"),
                InstallDate = InstalledApp.ParseInstallDate(key.GetString("InstallDate")),
            };
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }

    private static string? NormalizeNullable(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim().TrimEnd('\\');

    /// <summary>The Core <see cref="CoreView"/> equivalent of the given app, for callers that need it.</summary>
    public static CoreView ViewOf(InstalledApp app) => app.View;
}
