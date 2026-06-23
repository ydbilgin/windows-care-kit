namespace WindowsCareKit.Core.Modules.Migration.Detection;

/// <summary>
/// Tri-state installation scope (B-4). Determines which user(s) are affected and whether
/// elevation is needed for management operations.
/// </summary>
public enum ProgramScope
{
    /// <summary>Machine-wide install (HKLM); visible to all users, elevation typically required.</summary>
    Machine,

    /// <summary>Per-user install (HKCU) for the currently-enumerated user.</summary>
    CurrentUser,

    /// <summary>Installed for a different user whose hive is not currently mounted and readable.</summary>
    OtherUserNotEnumerable,
}
