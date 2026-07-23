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
    private readonly Func<string, string[]> _getDirectories;

    public Win32BrowserExtensionInventory() : this(Directory.GetDirectories) { }

    /// <summary>Test-only seam (NEW-07 MAJOR-01 fix): lets unit tests inject a synthetic
    /// <see cref="DirectoryNotFoundException"/>/<see cref="UnauthorizedAccessException"/>/<see cref="IOException"/>
    /// for a browser's <c>User Data</c> root or a profile's <c>Extensions</c> folder without needing a real
    /// inaccessible directory. Production always uses the real-FS default above.</summary>
    internal Win32BrowserExtensionInventory(Func<string, string[]> getDirectories)
        => _getDirectories = getDirectories;

    public BrowserExtensionListing ReadAll()
    {
        var found = new List<BrowserExtension>();
        var faults = new List<InventorySourceFault>();
        int attempted = 0, failed = 0;
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

        foreach ((string vendor, string label) in Browsers())
        {
            string userData = Path.Combine(localAppData, vendor, "User Data");

            // NEW-07 MAJOR-01 fix: no Directory.Exists preflight — per Microsoft's own documented behavior it
            // returns false both for genuine absence AND when determining existence fails (insufficient
            // permissions, I/O errors), which would silently collapse an access-denied "User Data" root into
            // "browser not installed" (honest-empty). Attempt the enumeration directly and classify the outcome.
            string[] profileDirs;
            try
            {
                profileDirs = EnumerateProfileDirs(userData, _getDirectories);
            }
            catch (DirectoryNotFoundException)
            {
                continue; // browser not installed — legitimate empty, not a fault.
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                attempted++;
                failed++;
                faults.Add(new InventorySourceFault(label, ex.GetType().Name));
                continue;
            }

            attempted++; // listing this browser's profiles is one sub-source.

            foreach (string profileDir in profileDirs)
            {
                string extensionsRoot = Path.Combine(profileDir, "Extensions");
                string profileName = Path.GetFileName(profileDir);
                // MINOR-01 fix: the fault Source is a fixed, non-path-derived descriptor (never the actual
                // captured profile directory name, which — per the accept filter below — is not restricted to
                // a closed enum and could carry an attacker/user-influenced string).
                string profileLabel = profileName.Equals("Default", StringComparison.OrdinalIgnoreCase)
                    ? "Default"
                    : "numbered-profile";

                try
                {
                    ReadExtensions(label, profileName, extensionsRoot, _getDirectories, found);
                }
                catch (DirectoryNotFoundException)
                {
                    continue; // profile has no Extensions folder — legitimate empty.
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    attempted++;
                    failed++;
                    faults.Add(new InventorySourceFault($"{label}/{profileLabel}", ex.GetType().Name));
                    continue;
                }

                attempted++; // enumerating this profile's extensions is one sub-source.
            }
        }

        return new BrowserExtensionListing(found, InventoryHealth.Aggregate(attempted, failed), faults);
    }

    private static IEnumerable<(string Vendor, string Label)> Browsers()
    {
        yield return (@"Google\Chrome", "Chrome");
        yield return (@"Microsoft\Edge", "Edge");
        yield return (@"BraveSoftware\Brave-Browser", "Brave");
        yield return (@"Vivaldi", "Vivaldi");
        yield return (@"Opera Software\Opera Stable", "Opera");
    }

    /// <summary>The Default + numbered profile directories under a browser's User Data. Throws
    /// <see cref="DirectoryNotFoundException"/> when <paramref name="userData"/> genuinely does not exist, or an
    /// access/I/O exception when it exists but cannot be read — both are classified by the caller.</summary>
    private static string[] EnumerateProfileDirs(string userData, Func<string, string[]> getDirectories)
    {
        var profiles = new List<string>();
        foreach (string d in getDirectories(userData))
        {
            string name = Path.GetFileName(d);
            if (name.Equals("Default", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Profile ", StringComparison.OrdinalIgnoreCase))
                profiles.Add(d);
        }
        return profiles.ToArray();
    }

    /// <summary>Add every extension under a profile's Extensions root. Throws
    /// <see cref="DirectoryNotFoundException"/> when the Extensions folder genuinely does not exist, or an
    /// access/I/O exception when it exists but cannot be enumerated — both are classified by the caller.
    /// A single extension whose manifest name is unresolvable still yields a row with a null Name
    /// (audit Nuance) — that is NOT a source fault.</summary>
    private static void ReadExtensions(
        string browser, string profile, string extensionsRoot, Func<string, string[]> getDirectories, List<BrowserExtension> sink)
    {
        foreach (string idDir in getDirectories(extensionsRoot))
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
