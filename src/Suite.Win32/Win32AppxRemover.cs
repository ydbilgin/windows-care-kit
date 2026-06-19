using Windows.ApplicationModel;
using Windows.Management.Deployment;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Uninstall;

namespace WindowsCareKit.Win32;

/// <summary>
/// Removes a per-user AppX/UWP package via <see cref="PackageManager.RemovePackageAsync(string, RemovalOptions)"/>
/// (per-user, <see cref="RemovalOptions.None"/> — spec §1.1, §4). This is the ONLY sanctioned destructive Win32
/// call that is not behind <c>Suite.Execution</c>: it is async COM, not a typed <c>PlannedAction</c>, so it lives
/// here as a tiny, auditable class that performs its OWN per-user / framework guard before calling Remove.
///
/// <para>Guards (fail closed): it refuses framework/system/resource packages
/// (<see cref="InstalledAppx.IsFrameworkOrSystem"/>) and refuses any package whose
/// <see cref="InstalledAppx.PackageFullName"/> is not in the CURRENT user's package list. Removal is
/// irreversible (<c>UndoCapability.None</c> conceptually); the caller must have confirmed first.</para>
///
/// <para>The banned-API analyzer bans <c>System.IO</c>/registry/<c>Process</c>, NOT <c>PackageManager</c>, so
/// this stays analyzer-clean inside <c>Suite.Win32</c>.</para>
/// </summary>
public sealed class Win32AppxRemover : IAppxRemover
{
    private readonly ExecutionLog? _log;

    /// <param name="log">Optional audit log. Kept optional (default null) so existing
    /// <c>new Win32AppxRemover()</c> callers/tests still compile and logging stays best-effort.</param>
    public Win32AppxRemover(ExecutionLog? log = null) => _log = log;

    /// <inheritdoc />
    public async Task<AppxRemovalResult> RemoveCurrentUserAsync(InstalledAppx package, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        string fullName = package.PackageFullName ?? string.Empty;
        Log("appx.remove.start", "AppX removal requested", fullName);

        if (string.IsNullOrWhiteSpace(package.PackageFullName))
            return Refused(fullName, "missing package full name");

        // Guard 1: never touch framework / system / resource packages.
        if (package.IsFrameworkOrSystem)
            return Refused(fullName, "framework/system packages are out of scope (per-user only)");

        PackageManager manager;
        try
        {
            manager = new PackageManager();
        }
        catch (Exception ex)
        {
            return Failed(fullName, $"packaging API unavailable: {ex.GetType().Name}");
        }

        // Guard 2: the package MUST be in the current user's package list. This also re-confirms it is per-user
        // (an empty user SID resolves to the current user) and that it is not framework/system at the OS level.
        Package? resolved;
        try
        {
            resolved = manager
                .FindPackagesForUser(string.Empty)
                .FirstOrDefault(p => string.Equals(p.Id.FullName, package.PackageFullName, StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            return Failed(fullName, $"could not enumerate current-user packages: {ex.GetType().Name}");
        }

        if (resolved is null)
            return Refused(fullName, "package is not installed for the current user");

        // Defense in depth: re-check the OS flags on the resolved package (the inventory could be stale).
        try
        {
            if (resolved.IsFramework || resolved.IsResourcePackage
                || resolved.SignatureKind == PackageSignatureKind.System)
            {
                return Refused(fullName, "resolved package is framework/system (refused)");
            }
        }
        catch (Exception)
        {
            // If the flags cannot be read, fail closed.
            return Refused(fullName, "could not verify package is per-user (refused)");
        }

        // Remove — per-user, no options. RemovalOptions.None keeps it to the current user (no all-users/provisioned).
        try
        {
            DeploymentResult result = await manager
                .RemovePackageAsync(package.PackageFullName, RemovalOptions.None)
                .AsTask(ct)
                .ConfigureAwait(false);

            if (result.ExtendedErrorCode is not null)
                return Failed(fullName, $"removal failed: {result.ErrorText}");

            Log("appx.remove.done", "removed for the current user", fullName);
            return new AppxRemovalResult(true, "removed for the current user");
        }
        catch (OperationCanceledException)
        {
            return Failed(fullName, "cancelled");
        }
        catch (Exception ex)
        {
            return Failed(fullName, $"removal threw: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private AppxRemovalResult Refused(string packageFullName, string reason)
    {
        Log("appx.remove.refused", reason, packageFullName);
        return new AppxRemovalResult(false, reason);
    }

    private AppxRemovalResult Failed(string packageFullName, string reason)
    {
        Log("appx.remove.failed", reason, packageFullName);
        return new AppxRemovalResult(false, reason);
    }

    private void Log(string eventType, string message, string packageFullName)
        => _log?.Append(eventType, message, new Dictionary<string, string?> { ["PackageFullName"] = packageFullName });
}
