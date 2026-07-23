namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>
/// The single owner of the pure capability rules used by the restore pipeline. Each boundary —
/// <see cref="MigrationRecipeLoader"/> (load-time inventory cap), <see cref="RecipeResolver"/>
/// (backup-time sandbox), <c>RecipeCapabilityHonestyGate</c> (conversion-time over-claim check),
/// and <see cref="MigrationRestoreRunner"/> (execution-path fail-safe) — composes these predicates
/// independently, in its own required order, from the data available at that boundary. Sharing
/// policy does <b>not</b> remove any boundary's obligation to make its own decision: defence in
/// depth is intentional, and the runner must never trust a precomputed boolean, cached verdict,
/// or presentation badge.
/// </summary>
public static class RestoreCapabilityPolicy
{
    /// <summary>Returns <see langword="true"/> when the declared tier permits an automatic file write.</summary>
    public static bool AllowsAutomaticWrite(RestoreTier tier)
        => tier >= RestoreTier.ConfigCopy;

    /// <summary>
    /// Returns <see langword="true"/> when the portability classification permits an automatic file write.
    /// </summary>
    public static bool AllowsAutomaticWrite(PortabilityClass portability)
        => portability == PortabilityClass.ProfileRelative;

    /// <summary>
    /// Returns <see langword="true"/> when <paramref name="folder"/> is a profile-relative root
    /// whose data may be restored automatically. Non-profile roots (ProgramData, ProgramFiles,
    /// ProgramFilesX86, WindowsEtc) are inventory/manual only — they are never written by the
    /// restore runner.
    /// </summary>
    public static bool IsProfileRoot(KnownFolder folder)
        => folder is KnownFolder.UserProfile or KnownFolder.AppData or KnownFolder.LocalAppData;

    /// <summary>Returns <see langword="true"/> for the only item kind the profile resolver can copy.</summary>
    public static bool IsProfilePathItem(RecipeItemKind itemKind)
        => itemKind == RecipeItemKind.ProfilePath;

    /// <summary>Returns <see langword="true"/> for item kinds that force a recipe to inventory-only.</summary>
    public static bool RequiresInventoryOnlyTier(RecipeItemKind itemKind)
        => itemKind is RecipeItemKind.MachineRoot
            or RecipeItemKind.WindowsEtc
            or RecipeItemKind.ExportCmd
            or RecipeItemKind.ManualTodo;

    /// <summary>Returns <see langword="true"/> for strategies implemented by the file-placement runner.</summary>
    public static bool AllowsAutomaticWrite(RestoreStrategy strategy)
        => strategy is RestoreStrategy.ConfigWrite or RestoreStrategy.MergeAfterInstall;

    /// <summary>
    /// Returns <see langword="true"/> when load/conversion must rewrite the declared tier to
    /// <see cref="RestoreTier.InventoryOnly"/>. <see cref="PortabilityClass.Partial"/> is intentionally
    /// not rewritten here; the honesty gate and execution runner still reject it for automatic writes.
    /// </summary>
    public static bool RequiresInventoryOnlyTier(PortabilityClass portability)
        => portability == PortabilityClass.MachineLocked;

    /// <summary>
    /// Resolves the tier used on the execution path. Only legacy targets with an unspecified tier consult
    /// the old allow-list; new targets retain their recipe-carried tier.
    /// </summary>
    public static RestoreTier GetEffectiveTier(RestoreTier declaredTier, string? recipeId)
        => declaredTier == RestoreTier.Unspecified
            ? RestoreAllowList.LegacyTierFor(recipeId)
            : declaredTier;
}
