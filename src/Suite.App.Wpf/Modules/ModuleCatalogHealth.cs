namespace WindowsCareKit.App.Modules;

/// <summary>What the module load root as a whole turned out to be.</summary>
public enum ModuleInventoryStatus
{
    /// <summary>No component is installed. Covers both "the Modules folder is not there" (the supported
    /// compact install) and "it is there and holds nothing". A supported, calm state.</summary>
    NotInstalled,

    /// <summary>Every component directory found under the root produced a usable module.</summary>
    Complete,

    /// <summary>The root was read, but at least one component directory did not produce a usable module.
    /// Part of the app is missing and the user must be told.</summary>
    Degraded,

    /// <summary>The root itself could not be enumerated, so nothing can be stated about what is installed.
    /// This is NOT "nothing is installed" and must never be rendered as such.</summary>
    Unavailable,
}

/// <summary>What one component directory under the root turned out to be.</summary>
public enum ModuleComponentStatus
{
    /// <summary>The component's module was loaded and is in the nav rail.</summary>
    Loaded,

    /// <summary>The directory is there but the expected <c>Suite.Module.&lt;folder&gt;.dll</c> is not.</summary>
    Incomplete,

    /// <summary>The file is there but is not a usable module: bad image, missing dependency, not exactly one
    /// <c>IWckModule</c>, not constructible, id mismatch, reserved id, or a path that escapes the root.</summary>
    Malformed,

    /// <summary>The file is there but could not be read: locked, denied, or an I/O failure.</summary>
    Unreadable,
}

/// <summary>
/// One component directory's outcome. <paramref name="DirectoryName"/> is ALREADY sanitized by
/// <see cref="ModuleDirectoryLabel"/> — a directory under <c>Modules\</c> is user-writable, so its name is
/// untrusted text and this record is the last place it can still be made safe before a view binds it.
/// <paramref name="FailureCategory"/> is a code-owned token or a CLR exception type name, never file content
/// and never an exception message.
/// </summary>
public sealed record ModuleComponentRecord(
    string DirectoryName,
    ModuleComponentStatus Status,
    string? FailureCategory);

/// <summary>
/// What the shell can honestly say about the installed component set after one discovery pass. Returned from
/// the catalog together with the modules themselves (see <see cref="ModuleCatalogResult"/>) so the two cannot
/// be separated by a caller that only wanted the list — which is exactly how the previous
/// <c>Diagnostics</c> side-channel came to have no production reader.
/// </summary>
public sealed record ModuleCatalogHealth(
    ModuleInventoryStatus Status,
    string ModulesRoot,
    IReadOnlyList<ModuleComponentRecord> Components,
    string? FailureCategory)
{
    /// <summary>Category tokens for failures that are not exceptions. Named here so the loader and the tests
    /// share one owner and no literal drifts (C11).</summary>
    public const string CategoryOutsideRoot = "OutsideRoot";
    public const string CategoryNoModuleType = "NoModuleType";
    public const string CategoryMultipleModuleTypes = "MultipleModuleTypes";
    public const string CategoryNotConstructible = "NotConstructible";
    public const string CategoryIdMismatch = "IdMismatch";
    public const string CategoryReservedId = "ReservedId";

    /// <summary>The root could not be enumerated. No component list exists, and none may be implied.</summary>
    public static ModuleCatalogHealth Unavailable(string modulesRoot, string failureCategory)
        => new(ModuleInventoryStatus.Unavailable, modulesRoot, [], failureCategory);

    /// <summary>The root was read. Zero components is <see cref="ModuleInventoryStatus.NotInstalled"/>, any
    /// non-<see cref="ModuleComponentStatus.Loaded"/> record is <see cref="ModuleInventoryStatus.Degraded"/>,
    /// otherwise <see cref="ModuleInventoryStatus.Complete"/>. One place decides, so no caller can pick a
    /// status that contradicts the records it carries.</summary>
    public static ModuleCatalogHealth FromComponents(
        string modulesRoot, IReadOnlyList<ModuleComponentRecord> components)
        => new(
            components.Count == 0
                ? ModuleInventoryStatus.NotInstalled
                : components.Any(c => c.Status != ModuleComponentStatus.Loaded)
                    ? ModuleInventoryStatus.Degraded
                    : ModuleInventoryStatus.Complete,
            modulesRoot,
            components,
            null);
}

/// <summary>One discovery pass: the modules the shell may compose, and what it may honestly say about them.</summary>
public sealed record ModuleCatalogResult(
    IReadOnlyList<IWckModule> Modules,
    ModuleCatalogHealth Health);
