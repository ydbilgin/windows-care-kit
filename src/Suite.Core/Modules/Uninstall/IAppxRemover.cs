namespace WindowsCareKit.Core.Modules.Uninstall;

/// <summary>The result of attempting a per-user AppX removal.</summary>
/// <param name="Removed">True only when the package was actually removed for the current user.</param>
/// <param name="Reason">Why removal happened or was refused (surfaced in the UI and the log).</param>
public sealed record AppxRemovalResult(bool Removed, string Reason);

/// <summary>
/// Per-user AppX removal contract. v1 is per-user only (spec §1.1, §7): framework / system /
/// resource / provisioned / all-users packages are refused. AppX removal is NOT a typed
/// <c>PlannedAction</c> (it is an async COM call, not a recycle-bin/registry/process operation), so it
/// does not flow through <c>OperationPlan</c> / the executor — it is a separate, explicitly-confirmed,
/// gated-by-its-own-guard call. It is irreversible (<c>UndoCapability.None</c> conceptually); the UI must warn.
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
