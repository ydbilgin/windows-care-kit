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
    IReadOnlyList<string> Preconditions);
