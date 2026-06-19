using WindowsCareKit.Core.Planning;

namespace WindowsCareKit.Core.Modules.Clean;

/// <summary>Where a startup entry lives. Drives which kind of action disables it.</summary>
public enum StartupSource
{
    /// <summary><c>HKCU\Software\Microsoft\Windows\CurrentVersion\Run</c> value.</summary>
    HkcuRun,

    /// <summary><c>HKLM\Software\Microsoft\Windows\CurrentVersion\Run</c> value.</summary>
    HklmRun,

    /// <summary><c>HKCU\…\RunOnce</c> value.</summary>
    HkcuRunOnce,

    /// <summary><c>HKLM\…\RunOnce</c> value.</summary>
    HklmRunOnce,

    /// <summary>A <c>.lnk</c> shortcut in a Startup folder.</summary>
    StartupFolder,
}

/// <summary>
/// One startup entry the user could disable: a Run/RunOnce registry value or a Startup-folder shortcut.
/// Read-only metadata; disabling is a typed action built by <see cref="StartupPlanner"/>.
/// </summary>
/// <param name="Name">The Run value name, or the shortcut display name.</param>
/// <param name="Command">The command/target the entry launches (shown to the user, never executed).</param>
/// <param name="Source">Which store the entry came from.</param>
/// <param name="FolderPath">For <see cref="StartupSource.StartupFolder"/>: the full <c>.lnk</c> path. Null otherwise.</param>
public sealed record StartupEntry(string Name, string Command, StartupSource Source, string? FolderPath)
{
    /// <summary>True when this entry lives in a registry Run/RunOnce key (vs. a Startup folder).</summary>
    public bool IsRegistry => Source != StartupSource.StartupFolder;

    /// <summary>The Core hive for a registry entry (HKCU vs HKLM). Meaningless for folder entries.</summary>
    public RegistryHive Hive => Source is StartupSource.HklmRun or StartupSource.HklmRunOnce
        ? RegistryHive.LocalMachine
        : RegistryHive.CurrentUser;

    /// <summary>The Run/RunOnce subkey path for a registry entry (hive-relative, no leading backslash).</summary>
    public string SubKeyPath => Source switch
    {
        StartupSource.HkcuRun or StartupSource.HklmRun => @"Software\Microsoft\Windows\CurrentVersion\Run",
        StartupSource.HkcuRunOnce or StartupSource.HklmRunOnce => @"Software\Microsoft\Windows\CurrentVersion\RunOnce",
        _ => string.Empty,
    };
}

/// <summary>Read-only listing of HKCU/HKLM Run + RunOnce values and Startup-folder shortcuts (spec §1.2).</summary>
public interface IStartupProbe
{
    /// <summary>Every startup entry across the registry Run/RunOnce keys and the Startup folders.</summary>
    IReadOnlyList<StartupEntry> ReadAll();
}
