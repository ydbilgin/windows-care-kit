using System.IO;
using System.Text.Json;
using WindowsCareKit.Core.Modules.Clean;

namespace WindowsCareKit.Win32;

/// <summary>
/// Read-only inventory of Chromium-family browser extensions. It walks
/// <c>%LocalAppData%\&lt;vendor&gt;\User Data\&lt;profile&gt;\Extensions\&lt;id&gt;\&lt;version&gt;</c> and reads each
/// extension's <c>manifest.json</c> name for display. It never removes anything — extension removal is
/// out of scope (profile/sync risk, spec §1.2).
/// </summary>
public sealed class Win32BrowserExtensionInventory : IBrowserExtensionInventory
{
    public IReadOnlyList<BrowserExtension> ReadAll()
    {
        var found = new List<BrowserExtension>();
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach ((string vendor, string label) in Browsers())
        {
            string userData = Path.Combine(localAppData, vendor, "User Data");
            if (!Directory.Exists(userData))
                continue;

            foreach (string profileDir in EnumerateProfileDirs(userData))
            {
                string extensionsRoot = Path.Combine(profileDir, "Extensions");
                if (!Directory.Exists(extensionsRoot))
                    continue;

                string profileName = Path.GetFileName(profileDir);
                ReadExtensions(label, profileName, extensionsRoot, found);
            }
        }

        return found;
    }

    private static IEnumerable<(string Vendor, string Label)> Browsers()
    {
        yield return (@"Google\Chrome", "Chrome");
        yield return (@"Microsoft\Edge", "Edge");
        yield return (@"BraveSoftware\Brave-Browser", "Brave");
        yield return (@"Vivaldi", "Vivaldi");
        yield return (@"Opera Software\Opera Stable", "Opera");
    }

    private static IEnumerable<string> EnumerateProfileDirs(string userData)
    {
        string[] dirs;
        try { dirs = Directory.GetDirectories(userData); }
        catch { yield break; }

        foreach (string d in dirs)
        {
            string name = Path.GetFileName(d);
            if (name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                yield return d;
        }
    }

    private static void ReadExtensions(string browser, string profile, string extensionsRoot, List<BrowserExtension> sink)
    {
        string[] idDirs;
        try { idDirs = Directory.GetDirectories(extensionsRoot); }
        catch { return; }

        foreach (string idDir in idDirs)
        {
            string id = Path.GetFileName(idDir);
            if (id.Equals("Temp", StringComparison.OrdinalIgnoreCase))
                continue;

            string? name = TryReadExtensionName(idDir);
            sink.Add(new BrowserExtension(browser, profile, id, name, idDir));
        }
    }

    /// <summary>Resolve the display name from the newest version's <c>manifest.json</c>; null when unresolved.</summary>
    private static string? TryReadExtensionName(string idDir)
    {
        string[] versionDirs;
        try { versionDirs = Directory.GetDirectories(idDir); }
        catch { return null; }

        // Newest version folder last (string sort is good enough for display).
        Array.Sort(versionDirs, StringComparer.OrdinalIgnoreCase);
        for (int i = versionDirs.Length - 1; i >= 0; i--)
        {
            string manifest = Path.Combine(versionDirs[i], "manifest.json");
            if (!File.Exists(manifest))
                continue;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(manifest));
                if (doc.RootElement.TryGetProperty("name", out JsonElement nameEl)
                    && nameEl.ValueKind == JsonValueKind.String)
                {
                    string? value = nameEl.GetString();
                    // Localized names look like "__MSG_appName__"; surface them as null (we don't read _locales).
                    if (!string.IsNullOrWhiteSpace(value) && !value.StartsWith("__MSG_", StringComparison.Ordinal))
                        return value;
                }
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                // unreadable/invalid manifest — fall through to the next version
            }
        }

        return null;
    }
}
