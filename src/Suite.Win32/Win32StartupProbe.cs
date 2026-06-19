using System.IO;
using Microsoft.Win32;
using WindowsCareKit.Core.Modules.Clean;

namespace WindowsCareKit.Win32;

/// <summary>
/// Read-only listing of startup entries: HKCU/HKLM <c>Run</c> + <c>RunOnce</c> values and the per-user
/// and common Startup-folder shortcuts. It only reads value names/data and lists <c>.lnk</c> files —
/// it never disables anything (disabling is a gated action built by <see cref="StartupPlanner"/>).
/// </summary>
public sealed class Win32StartupProbe : IStartupProbe
{
    private const string RunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunOncePath = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";

    public IReadOnlyList<StartupEntry> ReadAll()
    {
        var entries = new List<StartupEntry>();

        ReadRunKey(RegistryHive.CurrentUser, RunPath, StartupSource.HkcuRun, entries);
        ReadRunKey(RegistryHive.LocalMachine, RunPath, StartupSource.HklmRun, entries);
        ReadRunKey(RegistryHive.CurrentUser, RunOncePath, StartupSource.HkcuRunOnce, entries);
        ReadRunKey(RegistryHive.LocalMachine, RunOncePath, StartupSource.HklmRunOnce, entries);

        ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), entries);
        ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), entries);

        return entries;
    }

    private static void ReadRunKey(RegistryHive hive, string subKey, StartupSource source, List<StartupEntry> sink)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var run = baseKey.OpenSubKey(subKey, writable: false);
            if (run is null)
                return;

            foreach (string name in run.GetValueNames())
            {
                if (string.IsNullOrEmpty(name))
                    continue; // skip the (Default) value
                string command = run.GetValue(name)?.ToString() ?? string.Empty;
                sink.Add(new StartupEntry(name, command, source, FolderPath: null));
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // a key/view we cannot read is simply skipped; the listing is best-effort
        }
    }

    private static void ReadStartupFolder(string folder, List<StartupEntry> sink)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder))
            return;

        string[] shortcuts;
        try { shortcuts = Directory.GetFiles(folder, "*.lnk"); }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return;
        }

        foreach (string lnk in shortcuts)
        {
            string name = Path.GetFileNameWithoutExtension(lnk);
            sink.Add(new StartupEntry(name, lnk, StartupSource.StartupFolder, FolderPath: lnk));
        }
    }
}
