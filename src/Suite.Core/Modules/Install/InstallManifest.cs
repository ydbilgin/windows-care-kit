using System.Text.Json.Serialization;

namespace WindowsCareKit.Core.Modules.Install;

/// <summary>
/// Method values understood by the Kur (Install/Restore) planner. <c>install-winget</c> and
/// <c>install-npm</c> become <see cref="Planning.CommandAction"/>s; everything else (e.g.
/// <c>install-url-manual</c>) is listed for the user as a manual step and never executed automatically.
/// </summary>
public static class InstallMethod
{
    public const string Winget = "install-winget";
    public const string Npm = "install-npm";
    public const string UrlManual = "install-url-manual";
    public const string ConfigRestore = "config-restore";
}

/// <summary>
/// Install tiers from the source manifest: <c>auto</c> entries can run unattended; <c>manual-after</c>
/// entries (login walls, big downloads, reboots, UAC) are surfaced as a checklist, never auto-run.
/// </summary>
public static class InstallTier
{
    public const string Auto = "auto";
    public const string ManualAfter = "manual-after";
}

/// <summary>
/// One entry of the reinstall manifest (<c>90-kurulum.json</c>). Read-only data — the planner turns the
/// auto/winget/npm entries into typed <see cref="Planning.CommandAction"/>s and config entries into
/// <see cref="Planning.RestoreMergeAction"/>s, ordered by the restore sequence (spec §1.4).
/// </summary>
public sealed record InstallEntry(
    string Id,
    string Phase,
    string Category,
    string Method,
    string? WingetId,
    string? NpmPackage,
    bool RequiresAdmin,
    bool RebootExpected,
    int RestoreOrder,
    string Description)
{
    /// <summary>The install tier (<c>auto</c> / <c>manual-after</c>); defaults to auto when absent.</summary>
    public string InstallTier { get; init; } = Modules.Install.InstallTier.Auto;

    /// <summary>True when this entry installs an npm global package that needs Node first (PATH refresh).</summary>
    public bool RequiresNode { get; init; }

    /// <summary>For <c>install-url-manual</c> entries: the official download / store URL (listed, not opened).</summary>
    public string? ManualUrl { get; init; }

    /// <summary>A path probed with <c>Test-Path</c> semantics to detect an existing sign-in (NEVER read — spec §1.4).</summary>
    public string? AuthProbe { get; init; }

    /// <summary>Short key for the sign-in (e.g. <c>claude</c>), used in the UI summary.</summary>
    public string? AuthKey { get; init; }

    /// <summary>The command the user runs to sign in after install (shown as text, never executed).</summary>
    public string? AuthCommand { get; init; }

    /// <summary>The config <c>Source</c> for a <see cref="InstallMethod.ConfigRestore"/> entry (merged with .bak).</summary>
    public string? ConfigSource { get; init; }

    /// <summary>The config <c>Destination</c> for a <see cref="InstallMethod.ConfigRestore"/> entry.</summary>
    public string? ConfigDestination { get; init; }

    /// <summary>True when this entry can be installed unattended (tier <c>auto</c> and a runnable method).</summary>
    [JsonIgnore]
    public bool IsAutomatable =>
        string.Equals(InstallTier, Modules.Install.InstallTier.Auto, StringComparison.OrdinalIgnoreCase)
        && (Method is InstallMethod.Winget or InstallMethod.Npm or InstallMethod.ConfigRestore);
}

/// <summary>The whole reinstall manifest: an ordered set of <see cref="InstallEntry"/> records.</summary>
public sealed record InstallManifest(IReadOnlyList<InstallEntry> Entries)
{
    /// <summary>An empty manifest (no entries).</summary>
    public static InstallManifest Empty { get; } = new(Array.Empty<InstallEntry>());
}
