namespace WindowsCareKit.Core.Planning;

/// <summary>Registry root hive for a <see cref="RegistryDeleteAction"/>.</summary>
public enum RegistryHive
{
    ClassesRoot,
    CurrentUser,
    LocalMachine,
    Users,
    CurrentConfig,
}

/// <summary>
/// Which registry view to operate on. The scanner must read both 64- and 32-bit views explicitly
/// (no hardcoded <c>Wow6432Node</c> paths) so 32-bit app remnants aren't invisible (spec §1.1, §4).
/// </summary>
public enum RegistryView
{
    Registry64,
    Registry32,
}

/// <summary>Service cleanup is risk-tiered: stopping is reversible, deleting is not (spec §3).</summary>
public enum ServiceOperation
{
    Stop,
    Disable,
    Delete,
}

/// <summary>Scheduled-task cleanup tiers, mirroring <see cref="ServiceOperation"/>.</summary>
public enum TaskOperation
{
    Disable,
    Delete,
}
