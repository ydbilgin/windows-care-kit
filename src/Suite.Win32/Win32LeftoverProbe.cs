using Microsoft.Win32;
using WindowsCareKit.Core.Modules.Uninstall;
using CoreHive = WindowsCareKit.Core.Planning.RegistryHive;
using CoreView = WindowsCareKit.Core.Planning.RegistryView;

namespace WindowsCareKit.Win32;

/// <summary>
/// Read-only leftover candidate finder. It looks at the app's install location, data folders named
/// after the app, the app's own registry keys, and services whose image path lives inside the app's
/// install location. It never deletes — classification and gating happen in <c>LeftoverScanner</c>.
/// Scheduled-task correlation is intentionally deferred to a later PR (returns empty here).
/// </summary>
public sealed class Win32LeftoverProbe : ILeftoverProbe
{
    private const string ServicesKey = @"SYSTEM\CurrentControlSet\Services";
    private const string UninstallPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";

    public IReadOnlyList<LeftoverDirectory> FindLeftoverDirectories(InstalledApp app)
    {
        var found = new List<LeftoverDirectory>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Consider(string? path, string note)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            string full;
            try { full = Path.GetFullPath(path); }
            catch { return; }
            if (!seen.Add(full)) return;
            if (Directory.Exists(full))
                found.Add(new LeftoverDirectory(full, note));
        }

        Consider(app.InstallLocation, "Reported install location");

        string name = app.DisplayName;
        Consider(Path.Combine(Folder(Environment.SpecialFolder.ProgramFiles), name), "Folder named after the app under Program Files");
        Consider(Path.Combine(Folder(Environment.SpecialFolder.ProgramFilesX86), name), "Folder named after the app under Program Files (x86)");
        Consider(Path.Combine(Folder(Environment.SpecialFolder.LocalApplicationData), name), "Per-user data folder named after the app");
        Consider(Path.Combine(Folder(Environment.SpecialFolder.ApplicationData), name), "Roaming data folder named after the app");
        Consider(Path.Combine(Folder(Environment.SpecialFolder.CommonApplicationData), name), "ProgramData folder named after the app");

        return found;
    }

    public IReadOnlyList<LeftoverRegistryKey> FindLeftoverRegistryKeys(InstalledApp app)
    {
        var found = new List<LeftoverRegistryKey>();

        // The app's own uninstall entry.
        CoreHive uninstallHive = app.Source == InstalledAppSource.CurrentUser ? CoreHive.CurrentUser : CoreHive.LocalMachine;
        string uninstallSub = UninstallPath + "\\" + app.RegistryKeyName;
        if (RegistryInterop.SubKeyExists(uninstallHive, app.View, uninstallSub))
            found.Add(new LeftoverRegistryKey(uninstallHive, uninstallSub, app.View, "The app's own uninstall registry entry"));

        // Vendor settings key Software\<Publisher>\<DisplayName>, in both HKCU and HKLM.
        if (!string.IsNullOrWhiteSpace(app.Publisher))
        {
            string vendorSub = $"SOFTWARE\\{app.Publisher}\\{app.DisplayName}";
            foreach (var hive in new[] { CoreHive.CurrentUser, CoreHive.LocalMachine })
            {
                if (RegistryInterop.SubKeyExists(hive, app.View, vendorSub))
                    found.Add(new LeftoverRegistryKey(hive, vendorSub, app.View, "Vendor settings key for the app"));
            }
        }

        return found;
    }

    public IReadOnlyList<LeftoverService> FindRelatedServices(InstalledApp app)
    {
        var found = new List<LeftoverService>();
        if (string.IsNullOrWhiteSpace(app.InstallLocation))
            return found; // only correlate services by image path inside a known install location

        string? location = TryFullPath(app.InstallLocation);
        if (location is null)
            return found;
        location = location.TrimEnd('\\');

        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var services = baseKey.OpenSubKey(ServicesKey, writable: false);
            if (services is null)
                return found;

            foreach (string svcName in services.GetSubKeyNames())
            {
                try
                {
                    using var svc = services.OpenSubKey(svcName, writable: false);
                    string? imagePath = svc?.GetValue("ImagePath") as string;
                    string? exe = ExtractExecutablePath(imagePath);
                    if (exe is not null && IsUnder(exe, location))
                        found.Add(new LeftoverService(svcName, "Service whose executable lives in the app's install folder", exe));
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
                {
                    // skip a service key we cannot read
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // services hive unreadable — return what we have
        }

        return found;
    }

    public IReadOnlyList<LeftoverTask> FindRelatedTasks(InstalledApp app)
        => Array.Empty<LeftoverTask>(); // scheduled-task correlation lands in a later PR (spec §8 PR sequence)

    private static string Folder(Environment.SpecialFolder f)
        => Environment.GetFolderPath(f);

    private static string? TryFullPath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return null; }
    }

    /// <summary>Extract the executable path from a service ImagePath (strips <c>\??\</c>, quotes, args).</summary>
    internal static string? ExtractExecutablePath(string? imagePath)
    {
        if (string.IsNullOrWhiteSpace(imagePath))
            return null;

        string s = imagePath.Trim();
        if (s.StartsWith(@"\??\", StringComparison.Ordinal))
            s = s.Substring(4);

        if (s.StartsWith('"'))
        {
            int end = s.IndexOf('"', 1);
            return end > 1 ? s.Substring(1, end - 1) : null;
        }

        // Unquoted: take up to the first ".exe" boundary, else up to the first space.
        int idx = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
            return s.Substring(0, idx + 4);
        int sp = s.IndexOf(' ');
        return sp > 0 ? s.Substring(0, sp) : s;
    }

    /// <summary>True when <paramref name="candidate"/> equals or sits under <paramref name="root"/>
    /// on a path-segment boundary (so <c>...\AB</c> does not match <c>...\ABC</c>).</summary>
    internal static bool IsUnder(string candidate, string root)
    {
        string c = TryFullPath(candidate)?.TrimEnd('\\') ?? string.Empty;
        if (c.Length == 0)
            return false;
        return c.Equals(root, StringComparison.OrdinalIgnoreCase)
            || c.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
    }
}
