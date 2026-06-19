namespace WindowsCareKit.Core.Safety;

/// <summary>
/// Whether an action can be reversed. The spec forbids presenting every action as reversible:
/// recycle-bin file deletes and registry .reg backups are <see cref="Full"/>; an in-place merge
/// with a .bak is <see cref="Partial"/>; UWP/winget/service deletes are <see cref="None"/> (spec §3).
/// </summary>
public enum UndoCapability
{
    None = 0,
    Partial = 1,
    Full = 2,
}
