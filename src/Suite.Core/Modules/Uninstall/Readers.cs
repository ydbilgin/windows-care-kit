namespace WindowsCareKit.Core.Modules.Uninstall;

/// <summary>Reads classic installed-program inventory from the uninstall registry keys (read-only).</summary>
public interface IInstalledAppReader
{
    /// <summary>All entries across HKLM 64/32 and HKCU. System components are included but flagged.</summary>
    IReadOnlyList<InstalledApp> ReadAll();
}

/// <summary>A per-user UWP/AppX package (read-only listing for the inventory).</summary>
public sealed record InstalledAppx
{
    public required string PackageFullName { get; init; }
    public required string DisplayName { get; init; }
    public string? PublisherDisplayName { get; init; }
    public string? Version { get; init; }
    public string? InstallLocation { get; init; }
    /// <summary>Framework/resource/system packages are not user-facing apps; flagged so the UI can hide them.</summary>
    public bool IsFrameworkOrSystem { get; init; }
}

/// <summary>
/// Lists per-user AppX packages. v1 is per-user only — provisioned / all-users / framework removal is
/// out of scope (spec §1.1). This is read-only.
/// </summary>
public interface IAppxReader
{
    IReadOnlyList<InstalledAppx> ReadCurrentUserPackages();
}
