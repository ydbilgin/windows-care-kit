namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>
/// The single owner of the profile-root rule used by the restore pipeline. Each boundary —
/// <see cref="MigrationRecipeLoader"/> (load-time inventory cap), <see cref="RecipeResolver"/>
/// (backup-time sandbox), <c>RecipeCapabilityHonestyGate</c> (conversion-time over-claim check),
/// and <see cref="MigrationRestoreRunner"/> (execution-path fail-safe) — calls this predicate
/// independently in its own decision logic. Sharing the predicate does <b>not</b> remove any
/// boundary's obligation to make its own decision: defence in depth is intentional, and the
/// runner must never trust a precomputed boolean or a presentation badge.
/// </summary>
public static class RestoreCapabilityPolicy
{
    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="folder"/> is a profile-relative root
    /// whose data may be restored automatically. Non-profile roots (ProgramData, ProgramFiles,
    /// ProgramFilesX86, WindowsEtc) are inventory/manual only — they are never written by the
    /// restore runner.
    /// </summary>
    public static bool IsProfileRoot(KnownFolder folder)
        => folder is KnownFolder.UserProfile or KnownFolder.AppData or KnownFolder.LocalAppData;
}
