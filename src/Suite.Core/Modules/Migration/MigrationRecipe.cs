namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>
/// The closed set of source roots a recipe path may anchor to (critic fix F1). A recipe NEVER references
/// an arbitrary <c>%ENV%</c> token or an absolute/rooted path — only one of these named, profile-relative
/// roots. The resolver (<see cref="RecipePathResolver"/>) expands ONLY these; anything else is rejected
/// fail-closed. This is what keeps a careless/malicious recipe from reaching outside the user's profile.
/// </summary>
public enum KnownFolder
{
    /// <summary><c>%USERPROFILE%</c> — the user's home directory (e.g. <c>C:\Users\alice</c>).</summary>
    UserProfile,

    /// <summary><c>%APPDATA%</c> — roaming app data (e.g. <c>C:\Users\alice\AppData\Roaming</c>).</summary>
    AppData,

    /// <summary><c>%LOCALAPPDATA%</c> — local app data (e.g. <c>C:\Users\alice\AppData\Local</c>).</summary>
    LocalAppData,
}

/// <summary>
/// How portable a recipe's data is across machines (decision §"Makine-taşınabilirliği"). Drives the
/// <see cref="PortabilityBadge"/> fail-safe: a <see cref="MachineLocked"/> item is NEVER shown as a
/// confident "it will work" green badge — it can only be inventoried/warned.
/// </summary>
public enum PortabilityClass
{
    /// <summary>Path/data is relative to the user profile and rebinds cleanly on a new machine — portable.</summary>
    ProfileRelative,

    /// <summary>Bound to this machine (DPAPI / SID / hardware / absolute path) — inventory/warn only, never "works".</summary>
    MachineLocked,

    /// <summary>Some of the data is portable, some is machine-locked — partial confidence.</summary>
    Partial,
}

/// <summary>
/// Restore strategy: HOW the data is written back on the new machine. Orthogonal to <see cref="RestorePhase"/>
/// and preconditions (critic fix F5). Slice 1 carries this as META only — there is NO restore execution yet.
/// </summary>
public enum RestoreStrategy
{
    /// <summary>Write the config file(s) into place.</summary>
    ConfigWrite,

    /// <summary>Merge onto whatever the freshly-installed app created (does not clobber).</summary>
    MergeAfterInstall,

    /// <summary>Replace the destination wholesale.</summary>
    Replace,
}

/// <summary>
/// Restore phase: WHEN in the new-machine setup the restore runs. Orthogonal to strategy (critic fix F5).
/// </summary>
public enum RestorePhase
{
    /// <summary>During/with the app install step.</summary>
    Install,

    /// <summary>Right after first run has seeded the profile.</summary>
    FirstRunSeed,

    /// <summary>Plain config-write phase (the safe default for most config files).</summary>
    ConfigWrite,
}

/// <summary>The <c>detect</c> block: where the app's data lives and whether to bother at all.</summary>
/// <param name="KnownFolder">The closed-enum root the app's data hangs off (F1).</param>
/// <param name="Path">The relative path under <paramref name="KnownFolder"/>, e.g. <c>.claude</c> or <c>discord</c>.</param>
/// <param name="Exists">When true, the recipe is only applied if the detect path actually exists on disk.</param>
public sealed record RecipeDetect(KnownFolder KnownFolder, string Path, bool Exists);

/// <summary>One item to back up within a recipe.</summary>
/// <param name="Path">Path relative to the recipe's <see cref="RecipeDetect.KnownFolder"/> root.</param>
/// <param name="Include">Optional include allow-list globs (relative to this item's path).</param>
/// <param name="Exclude">Optional exclude globs (relative to this item's path).</param>
public sealed record RecipeItem(string Path, IReadOnlyList<string> Include, IReadOnlyList<string> Exclude);

/// <summary>The restore META block (strategy/phase/preconditions). Slice 1: declarative only, no execution.</summary>
/// <param name="Strategy">How to restore (F5 dik-eksen).</param>
/// <param name="Phase">When to restore (F5 dik-eksen).</param>
/// <param name="Preconditions">Required conditions before restore, e.g. <c>process-closed</c>.</param>
public sealed record RecipeRestore(
    RestoreStrategy Strategy,
    RestorePhase Phase,
    IReadOnlyList<string> Preconditions);

/// <summary>
/// A declarative migration recipe — DATA, never code (decision §"Bildirimsel-only"). It describes WHAT to
/// back up from one app's profile-relative footprint; it can express paths, globs and a closed set of enums
/// and nothing else. Every recipe is validated by the strict loader (<see cref="MigrationRecipeLoader"/>),
/// resolved + sandbox-contained by <see cref="RecipeResolver"/>, and only then bridged to copy actions.
/// </summary>
/// <param name="SchemaVersion">Recipe schema version (currently 1). Unknown versions are rejected.</param>
/// <param name="Id">Stable id, e.g. <c>anthropic.claude-code</c>.</param>
/// <param name="DisplayName">Human-readable name.</param>
/// <param name="Category">UI grouping, e.g. <c>dev-tools</c>.</param>
/// <param name="Detect">Where the data lives + whether it must exist.</param>
/// <param name="Items">The items to back up.</param>
/// <param name="Exclude">Recipe-wide exclude globs applied to every item.</param>
/// <param name="SecretRule">Secret-overlay selector (currently only <c>global</c> is recognized).</param>
/// <param name="PortabilityClass">Portability classification (drives the fail-safe badge).</param>
/// <param name="Restore">Restore META (no execution in Slice 1).</param>
public sealed record MigrationRecipe(
    int SchemaVersion,
    string Id,
    string DisplayName,
    string Category,
    RecipeDetect Detect,
    IReadOnlyList<RecipeItem> Items,
    IReadOnlyList<string> Exclude,
    string SecretRule,
    PortabilityClass PortabilityClass,
    RecipeRestore Restore);
