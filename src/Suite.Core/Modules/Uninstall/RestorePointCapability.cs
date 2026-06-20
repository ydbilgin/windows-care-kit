namespace WindowsCareKit.Core.Modules.Uninstall;

/// <summary>
/// Reports whether a System Restore point can actually be created on this machine RIGHT NOW. The wizard
/// flips its restore-point toggle from this (UI decision §5). It is deliberately NOT "is the service
/// present" — <c>SRSetRestorePointW</c> can return success even when System Restore is turned OFF, so a
/// service-presence check would lie. Availability requires BOTH:
///
/// <list type="number">
/// <item>System Restore is ENABLED on the system drive (the real SR config signal), AND</item>
/// <item>the process is ELEVATED (creating a restore point needs admin).</item>
/// </list>
///
/// The composing LOGIC lives in <see cref="DefaultRestorePointCapabilityProbe"/> over two injectable signal
/// probes, so it is host-testable with fakes; the real signals come from the Win32 implementation.
/// </summary>
public interface IRestorePointCapabilityProbe
{
    /// <summary>True only when a restore point can really be created now (SR enabled on the system drive AND elevated).</summary>
    bool IsAvailable();
}

/// <summary>The "is System Restore enabled on the system drive?" signal (the real config check, not service presence).</summary>
public interface ISystemRestoreConfigProbe
{
    /// <summary>True when System Restore is configured ON for the system drive.</summary>
    bool IsSystemRestoreEnabled();
}

/// <summary>The "is the current process elevated (admin)?" signal.</summary>
public interface IElevationProbe
{
    /// <summary>True when the process runs with administrative privileges.</summary>
    bool IsElevated();
}

/// <summary>
/// The capability LOGIC, isolated from Win32 so it is host-safe and fully testable with fakes: availability
/// is the AND of the two signals (SR enabled on the system drive AND elevated). Either signal false → not
/// available → the wizard keeps the honest disabled reason (UI decision §5).
/// </summary>
public sealed class DefaultRestorePointCapabilityProbe : IRestorePointCapabilityProbe
{
    private readonly ISystemRestoreConfigProbe _srConfig;
    private readonly IElevationProbe _elevation;

    public DefaultRestorePointCapabilityProbe(ISystemRestoreConfigProbe srConfig, IElevationProbe elevation)
    {
        _srConfig = srConfig ?? throw new ArgumentNullException(nameof(srConfig));
        _elevation = elevation ?? throw new ArgumentNullException(nameof(elevation));
    }

    /// <inheritdoc />
    public bool IsAvailable() => _elevation.IsElevated() && _srConfig.IsSystemRestoreEnabled();
}
