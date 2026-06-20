using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Modules.Uninstall;

/// <summary>A directory that looks like it belongs to an uninstalled/installed app.</summary>
public sealed record LeftoverDirectory(string Path, string Note);

/// <summary>A registry key that looks app-related.</summary>
public sealed record LeftoverRegistryKey(RegistryHive Hive, string SubKeyPath, RegistryView View, string Note);

/// <summary>
/// A service that looks app-related. <paramref name="ImagePath"/> is the resolved executable path the probe
/// used to correlate the service to the app (null when it could not be resolved); it is the attribution
/// evidence the <see cref="LeftoverClassifier"/> re-checks before calling a service ProgramOwned.
/// </summary>
public sealed record LeftoverService(string ServiceName, string Note, string? ImagePath = null);

/// <summary>A scheduled task that looks app-related.</summary>
public sealed record LeftoverTask(string TaskPath, string Note);

/// <summary>
/// Enumerates candidate leftovers for an app — read-only. The Win32 implementation looks at the
/// install location, well-known data folders, the app's registry keys, and the service/task
/// inventory. It never deletes; classification and gating happen in <see cref="LeftoverScanner"/>.
/// </summary>
public interface ILeftoverProbe
{
    IReadOnlyList<LeftoverDirectory> FindLeftoverDirectories(InstalledApp app);
    IReadOnlyList<LeftoverRegistryKey> FindLeftoverRegistryKeys(InstalledApp app);
    IReadOnlyList<LeftoverService> FindRelatedServices(InstalledApp app);
    IReadOnlyList<LeftoverTask> FindRelatedTasks(InstalledApp app);
}
