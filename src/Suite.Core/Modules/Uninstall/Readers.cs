namespace WindowsCareKit.Core.Modules.Uninstall;

using WindowsCareKit.Core.Planning;

/// <summary>Reads classic installed-program inventory from the uninstall registry keys (read-only).</summary>
public interface IInstalledAppReader
{
    /// <summary>All entries across HKLM 64/32 and HKCU. System components are included but flagged.</summary>
    IReadOnlyList<InstalledApp> ReadAll();
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

/// <summary>
/// Lists per-user AppX packages. v1 is per-user only — provisioned / all-users / framework removal is
/// out of scope (spec §1.1). This is read-only.
/// </summary>
public interface IAppxReader
{
    IReadOnlyList<InstalledAppx> ReadCurrentUserPackages();
}
