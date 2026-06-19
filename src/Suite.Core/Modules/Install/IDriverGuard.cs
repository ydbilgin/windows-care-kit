namespace WindowsCareKit.Core.Modules.Install;

/// <summary>
/// Confirms a driver is a network driver (<c>Class=Net</c>) before any driver-restore action is emitted.
/// spec §1.4 forbids all-driver restore: a driver entry only enters the plan when this returns true,
/// otherwise the planner skips it and reports why. Implementations are read-only (PnP enumeration).
/// </summary>
public interface IDriverGuard
{
    /// <summary>True only when the given driver INF / PnP class is the network class (<c>Net</c>).</summary>
    bool IsNetClass(string driverIdentifier);
}
