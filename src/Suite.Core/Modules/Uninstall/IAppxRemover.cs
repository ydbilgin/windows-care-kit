namespace WindowsCareKit.Core.Modules.Uninstall;

/// <summary>The result of attempting a per-user AppX removal.</summary>
/// <param name="Removed">True only when the package was actually removed for the current user.</param>
/// <param name="Reason">Why removal happened or was refused (surfaced in the UI and the log).</param>
public sealed record AppxRemovalResult(bool Removed, string Reason);

/// <summary>
/// Per-user AppX removal sink contract. Framework, system, resource, provisioned, and all-users packages
/// are refused. Production dispatch is a typed <c>AppxRemoveAction</c> inside an approved
/// <c>OperationPlan</c>; only <c>GatedExecutor</c> calls the sanctioned Suite.Execution adapter after the
/// shared gate revalidates the action at execution time. Removal is irreversible, so the UI must warn.
/// </summary>
public interface IAppxRemover
{
    /// <summary>
    /// Removes a per-user package by <see cref="InstalledAppx.PackageFullName"/>. The implementation MUST
    /// refuse if the package is framework/system (<see cref="InstalledAppx.IsFrameworkOrSystem"/>) or is not
    /// present in the current user's package list. Returns a non-removed result with a reason instead of throwing.
    /// </summary>
    Task<AppxRemovalResult> RemoveCurrentUserAsync(InstalledAppx package, CancellationToken ct = default);
}
