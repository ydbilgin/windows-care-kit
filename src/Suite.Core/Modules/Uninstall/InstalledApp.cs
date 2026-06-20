using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Modules.Uninstall;

/// <summary>Where an app's uninstall entry was found.</summary>
public enum InstalledAppSource
{
    /// <summary>HKLM\…\Uninstall, 64-bit view.</summary>
    MachineWide64,
    /// <summary>HKLM\…\Uninstall, 32-bit (WOW6432Node) view.</summary>
    MachineWide32,
    /// <summary>HKCU\…\Uninstall, per-user.</summary>
    CurrentUser,
}

/// <summary>
/// A classic (MSI / Win32) installed program, read from an uninstall registry key. Read-only data:
/// nothing here removes anything (spec §1.1 PR1 = read-only inventory).
/// </summary>
public sealed record InstalledApp
{
    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? DisplayVersion { get; init; }
    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }

    /// <summary>The registry subkey path under the uninstall root (e.g. a product GUID or app name).</summary>
    public required string RegistryKeyName { get; init; }
    public required InstalledAppSource Source { get; init; }

    /// <summary>System components are hidden from the default list (SystemComponent=1).</summary>
    public bool IsSystemComponent { get; init; }

    /// <summary>
    /// The reported install size in KB, from the uninstall key's <c>EstimatedSize</c> (REG_DWORD). Null when
    /// the value is absent. This is the vendor-reported figure — a CHEAP registry read, never a disk scan.
    /// </summary>
    public int? EstimatedSizeKb { get; init; }

    /// <summary>
    /// The install date from the uninstall key's <c>InstallDate</c> (a <c>yyyyMMdd</c> string). Null when the
    /// value is absent or unparseable. A cheap registry read, no filesystem inspection.
    /// </summary>
    public DateOnly? InstallDate { get; init; }

    /// <summary>
    /// True when the app has neither an UninstallString nor a QuietUninstallString — its vendor uninstaller
    /// entry is missing/broken, so the official-uninstaller path cannot run from this entry.
    /// </summary>
    public bool HasUninstaller =>
        !string.IsNullOrWhiteSpace(UninstallString) || !string.IsNullOrWhiteSpace(QuietUninstallString);

    /// <summary>
    /// The <c>EstimatedSize</c> rendered human-readable (KB → MB/GB), or "—" when absent. Pure formatting so
    /// the same string is used by the grid and any test (spec "Sahip kararları": registry size, no disk scan).
    /// </summary>
    public string SizeDisplay => FormatSize(EstimatedSizeKb);

    /// <summary>The <see cref="InstallDate"/> as <c>yyyy-MM-dd</c>, or "—" when absent.</summary>
    public string InstallDateDisplay =>
        InstallDate is { } d ? d.ToString("yyyy-MM-dd") : Em;

    /// <summary>The em-dash placeholder shown when a field is absent (spec §2 "—").</summary>
    public const string Em = "—";

    /// <summary>
    /// Formats an <c>EstimatedSize</c> (KB) as a compact human string. &lt;1024 KB stays in KB; otherwise MB,
    /// then GB once it reaches 1024 MB. Returns "—" for null/negative.
    /// </summary>
    public static string FormatSize(int? sizeKb)
    {
        if (sizeKb is not { } kb || kb < 0)
            return Em;
        if (kb < 1024)
            return $"{kb} KB";
        double mb = kb / 1024.0;
        if (mb < 1024)
            return $"{mb:0.#} MB";
        double gb = mb / 1024.0;
        return $"{gb:0.##} GB";
    }

    /// <summary>
    /// Parses the registry <c>InstallDate</c> string (<c>yyyyMMdd</c>) into a <see cref="DateOnly"/>; null when
    /// absent or not in that exact form. Kept here so the reader and tests share one parse rule.
    /// </summary>
    public static DateOnly? ParseInstallDate(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return null;
        string s = raw.Trim();
        return DateOnly.TryParseExact(s, "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out DateOnly d)
            ? d
            : null;
    }

    /// <summary>The registry view to use when later operating on this app's keys.</summary>
    public RegistryView View => Source == InstalledAppSource.MachineWide32
        ? RegistryView.Registry32
        : RegistryView.Registry64;

    /// <summary>True when run for all users (HKLM) and therefore needs elevation to remove.</summary>
    public bool IsMachineWide => Source != InstalledAppSource.CurrentUser;
}
