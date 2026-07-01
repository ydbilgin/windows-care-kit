namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>
/// The side-carrier for recipe-only metadata that does NOT belong on <c>BackupEntry</c> (critic fix F4:
/// extending the 462-test <c>BackupEntry</c> schema is off the table). One <see cref="MigrationItemMeta"/>
/// rides ALONGSIDE each <c>BackupEntry</c> the bridge produces, carrying the portability class and the
/// restore META (strategy/phase/preconditions) so the plan/report can surface them — without execution
/// (Slice 1 is backup-read only).
/// </summary>
/// <param name="RecipeId">The recipe this item came from (e.g. <c>anthropic.claude-code</c>).</param>
/// <param name="EntryId">The matching <c>BackupEntry.Id</c> (one meta per entry).</param>
/// <param name="PortabilityClass">Portability classification (drives the fail-safe badge).</param>
/// <param name="RestoreStrategy">Restore strategy META (no execution in Slice 1).</param>
/// <param name="RestorePhase">Restore phase META (no execution in Slice 1).</param>
/// <param name="Preconditions">Restore preconditions, e.g. <c>process-closed</c>.</param>
public sealed record MigrationItemMeta(
    string RecipeId,
    string EntryId,
    PortabilityClass PortabilityClass,
    RestoreStrategy RestoreStrategy,
    RestorePhase RestorePhase,
    IReadOnlyList<string> Preconditions)
{
    /// <summary>
    /// B-1 honesty fail-safe (decision §3A): true when this item DECLARES a secret leaf the name-based
    /// filter would exclude (e.g. an <c>include</c> pattern matching <c>id_rsa</c>/<c>*.key</c>). The bridge
    /// sets it per-item by aggregating the per-leaf <see cref="MigrationSecretFilter"/> over the item's
    /// declared leaves. <see cref="PortabilityBadge.Compute(MigrationItemMeta)"/> reads it as a FIRST-CLASS
    /// input so a declared <see cref="PortabilityClass.ProfileRelative"/> item that over-declares a secret can
    /// NEVER render a green "works" badge — the override lives in the pure function, not in UI layering.
    /// <para>Honesty residual: this is NAME-based; an unknown-named DPAPI blob under a broad include is only
    /// caught by the M2.5 content-signature probe. Init-only (defaults false) so the positional record's
    /// existing construction sites compile unchanged.</para>
    /// </summary>
    public bool HasExcludedSecret { get; init; }

    /// <summary>
    /// M2.5 honesty floor: true when bounded content inspection found a machine/user binding, a conservative
    /// credential-store signature, or could not classify the file safely. This can only downgrade a badge.
    /// </summary>
    public bool HasMachineBoundContent { get; init; }

    /// <summary>
    /// True when the probe could not inspect local bytes without side effects or user action (locked now,
    /// inaccessible, cloud placeholder). This blocks a "works" claim without calling it machine-locked.
    /// </summary>
    public bool HasUnanalyzedContent { get; init; }

    /// <summary>
    /// The last content-probe status observed while deriving the item signals. Complete means no special
    /// status was observed; non-complete values allow UI text to explain the honest downgrade.
    /// </summary>
    public ContentProbeStatus ContentProbeStatus { get; init; } = ContentProbeStatus.Complete;
}
