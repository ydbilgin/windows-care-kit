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

    /// <summary><c>%PROGRAMDATA%</c> — non-profile machine data. Inventory-only in Slice 1.</summary>
    ProgramData,

    /// <summary><c>%ProgramFiles%</c> — non-profile install root. Inventory-only in Slice 1.</summary>
    ProgramFiles,

    /// <summary><c>%ProgramFiles(x86)%</c> — non-profile install root. Inventory-only in Slice 1.</summary>
    ProgramFilesX86,

    /// <summary>Windows drivers/etc content such as hosts. Inventory/export only in Slice 1.</summary>
    WindowsEtc,
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

/// <summary>Honest restore capability tier for v3 recipes. It gates claims; Slice 1 does not execute restores.</summary>
public enum RestoreTier
{
    /// <summary>Only for legacy restore manifests that predate the manifest restoreTier field.</summary>
    Unspecified = 0,
    InventoryOnly,
    ConfigCopy,
    MergeAfterInstall,
}

/// <summary>Safety/curation tier for catalog governance. Trusted never bypasses badge/secret floors.</summary>
public enum CatalogTier
{
    Trusted,
    Community,
}

/// <summary>Recipe item route. Only <see cref="ProfilePath"/> is copied by the Slice-1 profile resolver.</summary>
public enum RecipeItemKind
{
    ProfilePath,
    MachineRoot,
    ExportCmd,
    WindowsEtc,
    ManualTodo,
}

/// <summary>Closed export kinds. Recipes name data intent, never command strings.</summary>
public enum ExportKind
{
    WifiProfiles,
    RegistrySubtree,
    WingetList,
    NpmGlobalList,
    PathDump,
    ScheduledTasks,
}

public sealed record RecipeItemVerify(IReadOnlyList<string> Exists, int? MaxSizeMB);

public enum InstallerSource
{
    Winget,
    Npm,
    MicrosoftStore,
    ManualDownload,
    ExistingInstaller,
    Unknown,
}

public enum LicenseSource
{
    AccountLogin,
    ProductKey,
    LicenseFile,
    Subscription,
    None,
    Unknown,
}

public sealed record LocalizedText(string? En, string? Tr);

public sealed record MigrationRecipeMeta(
    LocalizedText? UiWarning,
    IReadOnlyList<string> ManualSteps,
    IReadOnlyList<string> ManualTodo,
    InstallerSource? InstallerSource,
    LicenseSource? LicenseSource,
    bool RequiresRelogin,
    bool BackedUpButNotRestored,
    bool SurvivesOnOtherDrive);

/// <summary>The <c>detect</c> block: where the app's data lives and whether to bother at all.</summary>
/// <param name="KnownFolder">The closed-enum root the app's data hangs off (F1).</param>
/// <param name="Path">The relative path under <paramref name="KnownFolder"/>, e.g. <c>.claude</c> or <c>discord</c>.</param>
/// <param name="Exists">When true, the recipe is only applied if the detect path actually exists on disk.</param>
public sealed record RecipeDetect(KnownFolder KnownFolder, string Path, bool Exists);

/// <summary>One item to back up within a recipe.</summary>
/// <param name="Path">Path relative to the recipe's <see cref="RecipeDetect.KnownFolder"/> root.</param>
/// <param name="Include">Optional include allow-list globs (relative to this item's path).</param>
/// <param name="Exclude">Optional exclude globs (relative to this item's path).</param>
public sealed record RecipeItem(string Path, IReadOnlyList<string> Include, IReadOnlyList<string> Exclude)
{
    public RecipeItemKind Kind { get; init; } = RecipeItemKind.ProfilePath;
    public string? LibraryDetector { get; init; }
    public string? LauncherId { get; init; }
    public ExportKind? ExportKind { get; init; }
    public IReadOnlyList<string> ManualTodo { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RequiresClosedProcesses { get; init; } = Array.Empty<string>();
    public RecipeItemVerify? Verify { get; init; }
}

/// <summary>The restore META block (strategy/phase/preconditions). Slice 1: declarative only, no execution.</summary>
/// <param name="Strategy">How to restore (F5 dik-eksen).</param>
/// <param name="Phase">When to restore (F5 dik-eksen).</param>
/// <param name="Preconditions">Required conditions before restore, e.g. <c>process-closed</c>.</param>
public sealed record RecipeRestore(
    RestoreStrategy Strategy,
    RestorePhase Phase,
    IReadOnlyList<string> Preconditions);

/// <summary>
/// The closed set of install methods a recipe may declare (recipe schema v2). It MIRRORS the Kur module's
/// <see cref="WindowsCareKit.Core.Modules.Install.InstallMethod"/> string constants but is a typed enum on the
/// recipe so the strict loader can reject any value outside this set fail-closed (a recipe is DATA, never a
/// command string). The projector maps it 1:1 onto an <c>InstallEntry.Method</c> so the SAME gated
/// <c>InstallPlanner</c> builds the action — there is no second command-builder.
/// </summary>
public enum RecipeInstallMethod
{
    /// <summary>Install via <c>winget</c> (<see cref="RecipeInstall.WingetId"/> required + id-validated).</summary>
    Winget,

    /// <summary>Install a global <c>npm</c> package (<see cref="RecipeInstall.NpmPackage"/> required + name-validated).</summary>
    Npm,

    /// <summary>Manual download (<see cref="RecipeInstall.ManualUrl"/> required) — listed only, NEVER executed.</summary>
    UrlManual,
}

/// <summary>
/// Optional declarative install intent (recipe schema v2). DATA only — never a command string. Exactly ONE
/// locator is populated, matching <see cref="Method"/>: <see cref="WingetId"/> for <see cref="RecipeInstallMethod.Winget"/>,
/// <see cref="NpmPackage"/> for <see cref="RecipeInstallMethod.Npm"/>, <see cref="ManualUrl"/> for
/// <see cref="RecipeInstallMethod.UrlManual"/>. The loader validates the winget id / npm name through the SAME
/// allow-lists the gated <see cref="WindowsCareKit.Core.Modules.Install.InstallPlanner"/> applies, so a recipe
/// can only ever describe the reviewed install command — never a path/flag-shaped one.
/// </summary>
/// <param name="Method">The install method (closed enum).</param>
/// <param name="WingetId">Required iff <see cref="Method"/> is <see cref="RecipeInstallMethod.Winget"/>; validated by <c>InstallPlanner.IsValidWingetId</c>.</param>
/// <param name="NpmPackage">Required iff <see cref="Method"/> is <see cref="RecipeInstallMethod.Npm"/>; validated by <c>InstallPlanner.IsValidNpmPackage</c>.</param>
/// <param name="ManualUrl">Required iff <see cref="Method"/> is <see cref="RecipeInstallMethod.UrlManual"/> (listed, never opened).</param>
/// <param name="RequiresAdmin">When true the install needs elevation (default false).</param>
/// <param name="RebootExpected">When true a reboot is expected after install (default false) — drives the future manual/reboot barrier.</param>
public sealed record RecipeInstall(
    RecipeInstallMethod Method,
    string? WingetId,
    string? NpmPackage,
    string? ManualUrl,
    bool RequiresAdmin,
    bool RebootExpected);

/// <summary>
/// A declarative migration recipe — DATA, never code (decision §"Bildirimsel-only"). It describes WHAT to
/// back up from one app's profile-relative footprint; it can express paths, globs and a closed set of enums
/// and nothing else. Every recipe is validated by the strict loader (<see cref="MigrationRecipeLoader"/>),
/// resolved + sandbox-contained by <see cref="RecipeResolver"/>, and only then bridged to copy actions.
/// </summary>
/// <param name="SchemaVersion">Recipe schema version. The loader accepts 1 and 2 (v2 adds the optional <c>install</c> block; v1 rejects it). Other versions are rejected.</param>
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
    RecipeRestore Restore)
{
    /// <summary>
    /// Optional declarative install intent (recipe schema v2). INIT-ONLY (NOT a positional parameter) so the
    /// existing positional construction sites — the loader and the round-trip/backup tests — compile unchanged
    /// (critic fix #1: a plain positional <c>null</c>-defaulted param would still be a positional-arity change
    /// for callers that use positional args). The strict loader sets this via object-initializer for a v2 recipe
    /// that carries an <c>install</c> block; a v1 recipe leaves it null. When present, the backup projects it into
    /// exactly one gated install entry (<see cref="MigrationInstallProjector"/>).
    /// </summary>
    public RecipeInstall? Install { get; init; }

    public string? WingetId { get; init; }
    public IReadOnlyList<string> ProductCode { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> UpgradeCode { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> PackageFamilyName { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> InstallPathHint { get; init; } = Array.Empty<string>();
    public RestoreTier RestoreTier { get; init; } = RestoreTier.ConfigCopy;
    public MigrationRecipeMeta? MigrationMeta { get; init; }
    public CatalogTier CatalogTier { get; init; } = CatalogTier.Trusted;
}
