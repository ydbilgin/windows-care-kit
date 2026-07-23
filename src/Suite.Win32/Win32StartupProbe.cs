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

    private readonly Func<string, string[]> _enumerateLnkFiles;

    public Win32StartupProbe() : this(folder => Directory.GetFiles(folder, "*.lnk")) { }

    /// <summary>Test-only seam (NEW-07 MAJOR-01 fix): lets unit tests inject a synthetic
    /// <see cref="DirectoryNotFoundException"/>/<see cref="UnauthorizedAccessException"/>/<see cref="IOException"/>
    /// for a Startup folder without needing a real inaccessible directory. Production always uses the real-FS
    /// default above.</summary>
    internal Win32StartupProbe(Func<string, string[]> enumerateLnkFiles)
        => _enumerateLnkFiles = enumerateLnkFiles;

    public StartupInventory ReadAll()
    {
        var entries = new List<StartupEntry>();
        var faults = new List<InventorySourceFault>();
        int attempted = 0, failed = 0;

        void Account((ReadOutcome Outcome, string? Category) r, string label)
        {
            if (r.Outcome == ReadOutcome.Absent)
                return; // an absent key/folder is neither attempted nor failed — an honest empty.
            attempted++;
            if (r.Outcome == ReadOutcome.Failed)
            {
                failed++;
                faults.Add(new InventorySourceFault(label, r.Category ?? "Unknown"));
            }
        }

        Account(ReadRunKey(RegistryHive.CurrentUser, RunPath, StartupSource.HkcuRun, entries), "HKCU Run");
        Account(ReadRunKey(RegistryHive.LocalMachine, RunPath, StartupSource.HklmRun, entries), "HKLM Run");
        Account(ReadRunKey(RegistryHive.CurrentUser, RunOncePath, StartupSource.HkcuRunOnce, entries), "HKCU RunOnce");
        Account(ReadRunKey(RegistryHive.LocalMachine, RunOncePath, StartupSource.HklmRunOnce, entries), "HKLM RunOnce");
        Account(ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.Startup), entries, _enumerateLnkFiles), "Startup folder (user)");
        Account(ReadStartupFolder(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup), entries, _enumerateLnkFiles), "Startup folder (common)");

        return new StartupInventory(entries, InventoryHealth.Aggregate(attempted, failed), faults);
    }

    private enum ReadOutcome { Read, Absent, Failed }

    private static (ReadOutcome Outcome, string? Category) ReadRunKey(
        RegistryHive hive, string subKey, StartupSource source, List<StartupEntry> sink)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
            using var run = baseKey.OpenSubKey(subKey, writable: false);
            if (run is null)
                return (ReadOutcome.Absent, null); // key does not exist — legitimate empty, not a failure.

            foreach (string name in run.GetValueNames())
            {
                if (string.IsNullOrEmpty(name))
                    continue; // skip the (Default) value
                string command = run.GetValue(name)?.ToString() ?? string.Empty;
                sink.Add(new StartupEntry(name, command, source, FolderPath: null));
            }

            return (ReadOutcome.Read, null);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // a key/view we cannot read is now SURFACED as a source fault (NEW-07), not silently skipped.
            return (ReadOutcome.Failed, ex.GetType().Name);
        }
    }

    private static (ReadOutcome Outcome, string? Category) ReadStartupFolder(
        string folder, List<StartupEntry> sink, Func<string, string[]> enumerateLnkFiles)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return (ReadOutcome.Absent, null); // no special-folder path at all — legitimate empty.

        // NEW-07 MAJOR-01 fix: a preflight Directory.Exists check is NOT used here — per Microsoft's own
        // documented behavior, Directory.Exists returns false both for genuine absence AND when determining
        // existence fails (insufficient permissions, I/O errors), which would silently collapse an
        // access-denied Startup folder into "absent" (honest-empty), hiding the exact NEW-07 defect this
        // round exists to close. Instead we attempt the enumeration directly and classify the outcome.
        string[] shortcuts;
        try
        {
            shortcuts = enumerateLnkFiles(folder);
        }
        catch (DirectoryNotFoundException)
        {
            return (ReadOutcome.Absent, null); // folder genuinely does not exist — legitimate empty.
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return (ReadOutcome.Failed, ex.GetType().Name);
        }

        foreach (string lnk in shortcuts)
        {
            string name = Path.GetFileNameWithoutExtension(lnk);
            sink.Add(new StartupEntry(name, lnk, StartupSource.StartupFolder, FolderPath: lnk));
        }

        return (ReadOutcome.Read, null);
    }
}
