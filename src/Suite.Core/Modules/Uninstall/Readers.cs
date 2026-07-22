namespace WindowsCareKit.Core.Modules.Uninstall;

using WindowsCareKit.Core.Planning;

/// <summary>Reads classic installed-program inventory from the uninstall registry keys (read-only).</summary>
public interface IInstalledAppReader
{
    /// <summary>All entries across HKLM 64/32 and HKCU. System components are included but flagged.</summary>
    IReadOnlyList<InstalledApp> ReadAll();

    /// <summary>Inventory plus source-read health. Simple fakes and legacy readers are complete by default.</summary>
    InstalledAppReadResult ReadAllWithStatus() => InstalledAppReadResult.Complete(ReadAll());
}

/// <summary>Whether all, some, or none of the classic-app inventory sources could be read.</summary>
public enum InstalledAppReadStatus
{
    Complete,
    Partial,
    Unavailable,
}

/// <summary>A typed classic-app inventory outcome, including every source that could not be read completely.</summary>
public sealed record InstalledAppReadResult(
    IReadOnlyList<InstalledApp> Apps,
    InstalledAppReadStatus Status,
    IReadOnlyList<InstalledAppSource> FailedSources)
{
    public static InstalledAppReadResult Complete(IReadOnlyList<InstalledApp> apps)
        => new(apps, InstalledAppReadStatus.Complete, Array.Empty<InstalledAppSource>());
}

/// <summary>A read-only snapshot of one registry key's values.</summary>
public sealed record RegistryKeySnapshot(IReadOnlyDictionary<string, object?> Values)
{
    public string? GetString(string name)
        => Values.TryGetValue(name, out object? value) ? (value as string)?.Trim() : null;

    public int? GetDword(string name)
        => Values.TryGetValue(name, out object? value) && value is int i && i >= 0 ? i : null;

    public bool IsTruthy(string name)
        => Values.TryGetValue(name, out object? value) && value is int i && i != 0;
}

/// <summary>
/// Fine-grained, read-only registry probe for inventory code. Implementations must not create, write,
/// delete, or mutate registry state.
/// </summary>
public interface IRegistryProbe
{
    IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, RegistryView view, string subKey);

    RegistryKeySnapshot? ReadKey(RegistryHive hive, RegistryView view, string subKey);
}

/// <summary>A per-user UWP/AppX package (read-only listing for the inventory).</summary>
public sealed record InstalledAppx
{
    public required string PackageFullName { get; init; }
    public string? PackageFamilyName { get; init; }
    public required string DisplayName { get; init; }
    public string? PublisherDisplayName { get; init; }
    public string? Version { get; init; }
    public string? InstallLocation { get; init; }
    /// <summary>Framework/resource/system packages are not user-facing apps; flagged so the UI can hide them.</summary>
    public bool IsFrameworkOrSystem { get; init; }
}

/// <summary>Whether the current-user AppX inventory was complete, partial, or unavailable.</summary>
public enum AppxReadStatus
{
    Complete,
    Partial,
    Unavailable,
}

/// <summary>
/// Typed AppX inventory outcome. An unavailable packaging API or a failed enumeration is never represented as
/// a legitimate empty package list; callers can surface the degraded result honestly.
/// </summary>
public sealed record AppxReadResult(
    IReadOnlyList<InstalledAppx> Packages,
    AppxReadStatus Status,
    int FailedPackageCount = 0)
{
    public static AppxReadResult Complete(IReadOnlyList<InstalledAppx> packages)
        => new(packages, AppxReadStatus.Complete);
}

/// <summary>
/// Lists per-user AppX packages. v1 is per-user only — provisioned / all-users / framework removal is
/// out of scope (spec §1.1). This is read-only.
/// </summary>
public interface IAppxReader
{
    IReadOnlyList<InstalledAppx> ReadCurrentUserPackages();

    /// <summary>Inventory plus source-read health. Simple fakes remain complete by default.</summary>
    AppxReadResult ReadCurrentUserPackagesWithStatus()
        => AppxReadResult.Complete(ReadCurrentUserPackages());
}
