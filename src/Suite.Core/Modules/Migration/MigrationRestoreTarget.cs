namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>
/// The typed restore provenance for ONE backed-up item, written to <c>migration-manifest.json</c> alongside
/// the payload (decision §"KİLİT İÇGÖRÜ"). It is what lets the restore runner map a package file back to the
/// CORRECT place on a freshly-installed machine — even with a different username / drive letter / relocated
/// AppData — because it records WHICH closed <see cref="KnownFolder"/> the item anchors to and its normalized
/// relative path, instead of leaving the runner to guess from the package layout.
/// </summary>
/// <param name="RecipeId">The recipe this item came from (e.g. <c>anthropic.claude-code</c>).</param>
/// <param name="EntryId">The matching backup entry id (one target per entry).</param>
/// <param name="KnownFolder">The closed-enum profile root the destination anchors to (resolved on the TARGET machine).</param>
/// <param name="RelativePath">
/// The destination path RELATIVE to <see cref="KnownFolder"/>, already normalized through the recipe path
/// rules (forward slashes, no <c>%ENV%</c>, no rooted/UNC/<c>..</c> segments) — F5: produced via the same
/// containment-checked resolver the backup side uses, never a raw string-join.
/// </param>
/// <param name="PackageRelativeSource">Where the bytes live INSIDE the package (forward slashes), e.g. <c>migration/git.config/.gitconfig</c>.</param>
/// <param name="RestoreStrategy">How to restore (Slice 2 executes only <see cref="RestoreStrategy.ConfigWrite"/>-class single files).</param>
/// <param name="RestorePhase">When to restore.</param>
/// <param name="Preconditions">Required conditions before restore, e.g. <c>process-closed</c>.</param>
/// <param name="PortabilityClass">Portability classification — the runner BLOCKS machine-locked/partial items (F4).</param>
/// <param name="Sha256">Lowercase-hex SHA-256 of the packaged bytes (provenance + integrity).</param>
public sealed record MigrationRestoreTarget(
    string RecipeId,
    string EntryId,
    KnownFolder KnownFolder,
    string RelativePath,
    string PackageRelativeSource,
    RestoreStrategy RestoreStrategy,
    RestorePhase RestorePhase,
    IReadOnlyList<string> Preconditions,
    PortabilityClass PortabilityClass,
    string Sha256)
{
    /// <summary>
    /// Recipe-carried restore eligibility. Additive for old package manifests: absent values load as the
    /// legacy unspecified tier, while v3 packages carry the catalog's explicit value.
    /// </summary>
    public RestoreTier RestoreTier { get; init; } = RestoreTier.Unspecified;

    /// <summary>Recipe-carried honesty/manual guidance rendered by the restore success/report model.</summary>
    public MigrationRecipeMeta? MigrationMeta { get; init; }
}

/// <summary>
/// The restore-manifest written to <c>migration-manifest.json</c> at the package root: the schema version and
/// every item's <see cref="MigrationRestoreTarget"/>. The restore runner reads ONLY this — it never re-derives
/// destinations from the package directory layout (which carries no KnownFolder provenance).
/// </summary>
/// <param name="SchemaVersion">Manifest schema version (currently 1). Unknown versions are rejected on load.</param>
/// <param name="Targets">One restore target per backed-up item.</param>
public sealed record MigrationRestoreManifest(
    int SchemaVersion,
    IReadOnlyList<MigrationRestoreTarget> Targets)
{
    /// <summary>The current manifest schema version.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The fixed manifest file name written at the package root (decision §"KİLİT İÇGÖRÜ").</summary>
    public const string FileName = "migration-manifest.json";
}
