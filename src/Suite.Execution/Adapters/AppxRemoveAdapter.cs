using Windows.ApplicationModel;
using Windows.Management.Deployment;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Uninstall;

namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// The sanctioned per-user AppX removal sink. <see cref="GatedExecutor"/> invokes this adapter only after an
/// <c>AppxRemoveAction</c> has passed the shared safety gate and its approved plan hash has been revalidated.
/// The adapter then re-resolves the package for the current user and rechecks the OS protection flags at the
/// final destructive boundary. Provisioned/all-user removal remains out of scope.
/// </summary>
public sealed class AppxRemoveAdapter : IAppxRemover
{
    private readonly ExecutionLog? _log;

    public AppxRemoveAdapter(ExecutionLog? log = null) => _log = log;

    /// <inheritdoc />
    public async Task<AppxRemovalResult> RemoveCurrentUserAsync(InstalledAppx package, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(package);

        string fullName = package.PackageFullName ?? string.Empty;
        Log("appx.remove.start", "AppX removal requested", fullName);

        if (string.IsNullOrWhiteSpace(package.PackageFullName))
            return Refused(fullName, "missing package full name");

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
            return Refused(fullName, "could not verify package is per-user (refused)");
        }

        try
        {
#pragma warning disable RS0030 // Sanctioned sink: GatedExecutor is the only production caller.
            DeploymentResult result = await manager
                .RemovePackageAsync(package.PackageFullName, RemovalOptions.None)
                .AsTask(ct)
                .ConfigureAwait(false);
#pragma warning restore RS0030

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
