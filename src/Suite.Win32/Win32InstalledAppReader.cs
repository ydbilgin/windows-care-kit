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
        => ReadAllWithStatus().Apps;

    public InstalledAppReadResult ReadAllWithStatus()
    {
        var apps = new List<InstalledApp>();
        var failedSources = new List<InstalledAppSource>();
        int readableSources = 0;

        ReadSource(CoreHive.LocalMachine, CoreView.Registry64, InstalledAppSource.MachineWide64);
        ReadSource(CoreHive.LocalMachine, CoreView.Registry32, InstalledAppSource.MachineWide32);
        ReadSource(CoreHive.CurrentUser, CoreView.Registry64, InstalledAppSource.CurrentUser);

        InstalledAppReadStatus status = readableSources == 0
            ? InstalledAppReadStatus.Unavailable
            : failedSources.Count > 0
                ? InstalledAppReadStatus.Partial
                : InstalledAppReadStatus.Complete;
        return new InstalledAppReadResult(apps, status, failedSources);

        void ReadSource(CoreHive hive, CoreView view, InstalledAppSource source)
        {
            SourceReadStatus sourceStatus = ReadFrom(hive, view, source, apps);
            if (sourceStatus != SourceReadStatus.Failed)
                readableSources++;
            if (sourceStatus != SourceReadStatus.Complete)
                failedSources.Add(source);
        }
    }

    private SourceReadStatus ReadFrom(CoreHive hive, CoreView view, InstalledAppSource source, List<InstalledApp> sink)
    {
        try
        {
            bool entryFailed = false;
            foreach (string subName in _registry.GetSubKeyNames(hive, view, UninstallPath))
            {
                InstalledApp? app = TryReadEntry(hive, view, subName, source, out bool entryRead);
                entryFailed |= !entryRead;
                if (app is not null)
                    sink.Add(app);
            }
            return entryFailed ? SourceReadStatus.Partial : SourceReadStatus.Complete;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return SourceReadStatus.Failed;
        }
    }

    private InstalledApp? TryReadEntry(
        CoreHive hive,
        CoreView view,
        string subName,
        InstalledAppSource source,
        out bool succeeded)
    {
        try
        {
            RegistryKeySnapshot? key = _registry.ReadKey(hive, view, $@"{UninstallPath}\{subName}");
            succeeded = true;
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
            succeeded = false;
            return null;
        }
    }

    private enum SourceReadStatus
    {
        Complete,
        Partial,
        Failed,
    }

    private static string? NormalizeNullable(string? s)
        => string.IsNullOrWhiteSpace(s) ? null : s.Trim().TrimEnd('\\');
}
