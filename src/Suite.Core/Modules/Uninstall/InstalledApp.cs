using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Modules.Uninstall;

/// <summary>Where an app's uninstall entry was found.</summary>
public enum InstalledAppSource
{
    /// <summary>HKLM\…\Uninstall, 64-bit view.</summary>
    MachineWide64,
    /// <summary>HKLM\…\Uninstall, 32-bit (WOW6432Node) view.</summary>
    MachineWide32,
    /// <summary>HKCU\…\Uninstall, per-user.</summary>
    CurrentUser,
}

/// <summary>
/// A classic (MSI / Win32) installed program, read from an uninstall registry key. Read-only data:
/// nothing here removes anything (spec §1.1 PR1 = read-only inventory).
/// </summary>
public sealed record InstalledApp
{
    public required string DisplayName { get; init; }
    public string? Publisher { get; init; }
    public string? DisplayVersion { get; init; }
    public string? InstallLocation { get; init; }
    public string? UninstallString { get; init; }
    public string? QuietUninstallString { get; init; }

    /// <summary>The registry subkey path under the uninstall root (e.g. a product GUID or app name).</summary>
    public required string RegistryKeyName { get; init; }
    public required InstalledAppSource Source { get; init; }

    /// <summary>System components are hidden from the default list (SystemComponent=1).</summary>
    public bool IsSystemComponent { get; init; }

    /// <summary>The registry view to use when later operating on this app's keys.</summary>
    public RegistryView View => Source == InstalledAppSource.MachineWide32
        ? RegistryView.Registry32
        : RegistryView.Registry64;

    /// <summary>True when run for all users (HKLM) and therefore needs elevation to remove.</summary>
    public bool IsMachineWide => Source != InstalledAppSource.CurrentUser;
}
