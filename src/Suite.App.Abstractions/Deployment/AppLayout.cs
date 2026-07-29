using System.IO;

namespace WindowsCareKit.App.Deployment;

/// <summary>What a shipped resource has to BE for the capability that needs it to exist.</summary>
public enum AppResourceKind
{
    /// <summary>A regular file — the base string table, a manifest.</summary>
    File,

    /// <summary>A directory whose contents are enumerated — the module folder, the manifests folder.</summary>
    Directory,
}

/// <summary>
/// A file the app ships with: where it lives relative to the app root, and what kind of thing it must be.
/// <para>
/// The kind is not decoration. A generic "does this path exist" probe reports a regular file named
/// <c>Modules</c> as present, while the catalog that needs it enumerates a directory and finds nothing — the
/// report would then claim a capability the install does not have (P20).
/// </para>
/// </summary>
public readonly record struct AppResource(string RelativePath, AppResourceKind Kind);

/// <summary>Outcome kinds of an <see cref="AppLayout.Verify"/> call.</summary>
public enum AppLayoutVerificationStatus
{
    /// <summary>Every required resource was found under the frozen root.</summary>
    Ok,

    /// <summary>The root is known, but at least one required resource is absent — see
    /// <see cref="AppLayoutVerification.MissingRelativePaths"/>.</summary>
    Missing,

    /// <summary>The root itself could not be determined, so nothing could be checked. This is deliberately
    /// NOT reported as "missing": "we do not know where to look" and "we looked and it was not there" are
    /// different statements (P20).</summary>
    Undetermined,
}

/// <summary>The result of verifying that a set of required resources is present under the app root.</summary>
public sealed class AppLayoutVerification
{
    private static readonly string[] NoPaths = [];

    private AppLayoutVerification(
        AppLayoutVerificationStatus status,
        IReadOnlyList<string> missingRelativePaths,
        string? undeterminedReason)
    {
        Status = status;
        MissingRelativePaths = missingRelativePaths;
        UndeterminedReason = undeterminedReason;
    }

    public AppLayoutVerificationStatus Status { get; }

    /// <summary>The required relative paths that were not found. Empty unless <see cref="Status"/> is
    /// <see cref="AppLayoutVerificationStatus.Missing"/>.</summary>
    public IReadOnlyList<string> MissingRelativePaths { get; }

    /// <summary>Why the root could not be determined. Non-null only for
    /// <see cref="AppLayoutVerificationStatus.Undetermined"/>.</summary>
    public string? UndeterminedReason { get; }

    public bool IsOk => Status == AppLayoutVerificationStatus.Ok;

    internal static AppLayoutVerification Complete() =>
        new(AppLayoutVerificationStatus.Ok, NoPaths, null);

    internal static AppLayoutVerification MissingPaths(IReadOnlyList<string> missing) =>
        new(AppLayoutVerificationStatus.Missing, missing, null);

    internal static AppLayoutVerification Undetermined(string reason) =>
        new(AppLayoutVerificationStatus.Undetermined, NoPaths, reason);
}

/// <summary>
/// The single authoritative owner of "where do this application's shipped files live" (P22). Every read of a
/// file the app ships with — the string table, the module directory, the manifests — resolves through here
/// instead of reading <c>AppContext.BaseDirectory</c> at the use site.
/// <para>
/// <b>Trust contract (non-negotiable).</b> The root is derived from <i>resolved process identity only</i>:
/// the directory of <see cref="Environment.ProcessPath"/> after <see cref="File.ResolveLinkTarget(string,bool)"/>
/// has followed any symlink/shim to its final target, and only when that directory agrees with
/// <see cref="AppContext.BaseDirectory"/>. Nothing else may select it — not a marker file, not the working
/// directory, not an environment variable, config or registry value, and not a search over candidate
/// directories. <c>DirectoryModuleCatalog</c> loads unsigned assemblies from this root, so a root
/// chosen by filesystem <i>content</i> would let anyone who can write a directory decide which code the
/// (elevation-gating) maintenance tool executes. Markers may <i>verify</i> a root — see <see cref="Verify"/>,
/// which is diagnostics-only — they may never <i>select</i> one.
/// </para>
/// <para>
/// <b>When the two identities disagree</b> (the app was launched through a link or shim, so
/// <c>BaseDirectory</c> points at the link's folder rather than the artifact) the layout is
/// <b>undetermined</b>: it never guesses, never falls back, and never silently yields a plausible-but-wrong
/// directory. Reading <see cref="Root"/> in that state throws; the shell's startup preflight turns it into a
/// visible, explained failure instead (that is exactly the defect this type exists to prevent — an install
/// whose resources were not where the app looked rendered its entire UI as raw i18n keys and explained
/// nothing).
/// </para>
/// <para>
/// <b>Ambient by design, narrowly.</b> <see cref="Current"/> is a process-wide frozen value rather than an
/// injected collaborator. It is not a service locator: it exposes one immutable fact about how this process
/// was launched (the same category as <c>Environment.ProcessPath</c> itself, which it replaces at ~4 call
/// sites), it cannot be re-pointed at runtime, and the decision rule is a pure function testable through the
/// internal <see cref="Create"/> seam with no filesystem, privileges or VM. Making it injectable would create
/// the very hole the trust contract forbids: a caller-supplied layout could redirect module loading.
/// </para>
/// </summary>
public sealed class AppLayout
{
    private static readonly Lazy<AppLayout> Instance =
        new(CreateFromCurrentProcess, LazyThreadSafetyMode.ExecutionAndPublication);

    private readonly Func<string, AppResourceKind, bool> _exists;
    private readonly string? _root;

    private AppLayout(
        string? root,
        string? undeterminedReason,
        string? processDirectory,
        string? baseDirectory,
        Func<string, AppResourceKind, bool> exists)
    {
        _root = root;
        _exists = exists;
        UndeterminedReason = undeterminedReason;
        ProcessDirectory = processDirectory;
        BaseDirectory = baseDirectory;
    }

    /// <summary>The process-wide layout, computed once from this process's own identity and frozen.</summary>
    public static AppLayout Current => Instance.Value;

    /// <summary>True when both launch identities agreed and <see cref="Root"/> is therefore trustworthy.</summary>
    public bool IsDetermined => _root is not null;

    /// <summary>Why the layout is undetermined, naming both candidate paths. Null when determined.</summary>
    public string? UndeterminedReason { get; }

    /// <summary>The normalized directory of the resolved process image, or null when it was unobtainable.
    /// Diagnostics only — never a fallback root.</summary>
    public string? ProcessDirectory { get; }

    /// <summary>The normalized <see cref="AppContext.BaseDirectory"/>, or null when it was unobtainable.
    /// Diagnostics only — never a fallback root.</summary>
    public string? BaseDirectory { get; }

    /// <summary>
    /// The directory the app's shipped files live in. Throws when the layout is undetermined — a bogus root
    /// must be impossible to read silently.
    /// </summary>
    /// <exception cref="InvalidOperationException">The layout is undetermined.</exception>
    public string Root => _root ?? throw new InvalidOperationException(
        "The application layout is undetermined, so no resource path can be resolved. " + UndeterminedReason);

    /// <summary>
    /// The absolute path of a shipped resource, addressed relative to <see cref="Root"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The layout is undetermined.</exception>
    /// <exception cref="ArgumentException"><paramref name="relativePath"/> is rooted or escapes the root.</exception>
    public string Resource(string relativePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);

        // A rooted argument would make Path.Combine discard the root entirely, and '..' segments would climb
        // out of it: either turns "a resource of this app" into "any path the caller likes" (P31).
        if (Path.IsPathRooted(relativePath))
            throw new ArgumentException(
                "A shipped resource is addressed relative to the app root; a rooted path would replace it.",
                nameof(relativePath));

        string root = Root;
        string rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        string combined = Path.GetFullPath(Path.Combine(root, relativePath));
        if (!combined.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException(
                "A shipped resource must resolve inside the app root.", nameof(relativePath));

        return combined;
    }

    /// <summary>True when a resource of the required <see cref="AppResource.Kind"/> exists under the root.
    /// False when it is absent, when something of the wrong kind sits at that path, OR when the layout is
    /// undetermined — callers that must tell those apart ask <see cref="IsDetermined"/> or use
    /// <see cref="Verify"/>. Intended for genuinely optional inventory (a component that may not be
    /// installed), never for deciding where the root is.</summary>
    public bool HasResource(AppResource resource) =>
        IsDetermined && _exists(Resource(resource.RelativePath), resource.Kind);

    /// <summary>
    /// Reports whether every required resource is present under the root. <b>Diagnostics only:</b> this reads
    /// the filesystem, so by the trust contract it may never influence which directory is the root — by the
    /// time it runs the root is already frozen. Optional inventory is simply not passed here; its absence is
    /// a valid state, not a failure (a "Base only" install ships no <c>Modules\</c> or <c>manifests\</c>).
    /// </summary>
    public AppLayoutVerification Verify(params AppResource[] required)
    {
        ArgumentNullException.ThrowIfNull(required);

        if (!IsDetermined)
            return AppLayoutVerification.Undetermined(UndeterminedReason!);

        var missing = new List<string>();
        foreach (AppResource resource in required)
        {
            if (!_exists(Resource(resource.RelativePath), resource.Kind))
                missing.Add(resource.RelativePath);
        }

        return missing.Count == 0 ? AppLayoutVerification.Complete() : AppLayoutVerification.MissingPaths(missing);
    }

    /// <summary>
    /// Production wiring: reads this process's real identity and hands it to the pure rule in
    /// <see cref="Create"/>. The only place in the codebase that may read <see cref="Environment.ProcessPath"/>
    /// or <see cref="AppContext.BaseDirectory"/> for layout purposes.
    /// </summary>
    private static AppLayout CreateFromCurrentProcess()
    {
        string? processPath = Environment.ProcessPath;
        string? resolvedTarget = null;

        if (!string.IsNullOrWhiteSpace(processPath))
        {
            try
            {
                // Returns null when the path is not a link; follows the whole chain when it is. A launch
                // through a shim therefore yields the artifact's real directory rather than the link's.
                resolvedTarget = File.ResolveLinkTarget(processPath, returnFinalTarget: true)?.FullName;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                // We could not establish what this process actually is. That is "unknown", not "not a link":
                // resolving it as a plain path here would silently accept a link's directory as the root.
                return new AppLayout(
                    root: null,
                    undeterminedReason:
                        $"the process image '{Describe(processPath)}' could not be resolved to its final target " +
                        $"({ex.GetType().Name}), so the app root cannot be established.",
                    processDirectory: null,
#pragma warning disable RS0030 // AppLayout is the sanctioned owner of this read - see the trust contract above.
                    baseDirectory: NormalizeDirectory(AppContext.BaseDirectory),
#pragma warning restore RS0030
                    exists: ResourceExists);
            }
        }

#pragma warning disable RS0030 // AppLayout is the sanctioned owner of this read - see the trust contract above.
        return Create(processPath, resolvedTarget, AppContext.BaseDirectory, ResourceExists);
#pragma warning restore RS0030
    }

    /// <summary>Probes for the required kind specifically: a directory named like the base table, or a regular
    /// file named like an optional component folder, is not that resource — it is something else in its way.</summary>
    private static bool ResourceExists(string path, AppResourceKind kind) =>
        kind == AppResourceKind.Directory ? Directory.Exists(path) : File.Exists(path);

    /// <summary>
    /// The selection rule, as a pure function over injected inputs — no filesystem, no privileges, no VM.
    /// <para>
    /// resolved := <paramref name="resolvedTarget"/> ?? <paramref name="processPath"/>; the root candidate is
    /// that file's directory. It becomes the frozen root only when it equals the normalized
    /// <paramref name="baseDirectory"/> (full path, trailing separator trimmed, ordinal case-insensitive).
    /// Any other outcome — either side unobtainable, not fully qualified, or the two disagreeing — is
    /// <c>Undetermined</c>. There is deliberately no third branch: no search, no marker probe, no fallback.
    /// </para>
    /// <paramref name="exists"/> is used by <see cref="Verify"/> and <see cref="HasResource"/> only; the
    /// selection above never consults it.
    /// </summary>
    internal static AppLayout Create(
        string? processPath,
        string? resolvedTarget,
        string? baseDirectory,
        Func<string, AppResourceKind, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);

        string? identityPath = string.IsNullOrWhiteSpace(resolvedTarget) ? processPath : resolvedTarget;
        string? processDirectory = NormalizeDirectory(DirectoryOf(identityPath));
        string? baseDir = NormalizeDirectory(baseDirectory);

        if (processDirectory is null || baseDir is null)
        {
            return new AppLayout(
                root: null,
                undeterminedReason:
                    "the app root cannot be established: one of the launch identities is unavailable " +
                    $"(process path {Describe(processPath)}, resolved target {Describe(resolvedTarget)}, " +
                    $"base directory {Describe(baseDirectory)}).",
                processDirectory,
                baseDir,
                exists);
        }

        if (!string.Equals(processDirectory, baseDir, StringComparison.OrdinalIgnoreCase))
        {
            return new AppLayout(
                root: null,
                undeterminedReason:
                    $"the resolved program directory ({processDirectory}) and the runtime base directory " +
                    $"({baseDir}) disagree, which means the app was launched through a link or shim rather " +
                    "than from its own folder. That is not a supported launch mode, and guessing which of " +
                    "the two holds the shipped files is exactly the mistake this check exists to prevent.",
                processDirectory,
                baseDir,
                exists);
        }

        return new AppLayout(processDirectory, undeterminedReason: null, processDirectory, baseDir, exists);
    }

    private static string? DirectoryOf(string? filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            return null;

        string? directory = Path.GetDirectoryName(filePath);
        return string.IsNullOrWhiteSpace(directory) ? null : directory;
    }

    /// <summary>Canonicalizes a directory for comparison, or returns null when it is unusable. A path that is
    /// not fully qualified is rejected rather than resolved, because resolving it would silently pull in the
    /// ambient current directory — an input no layout decision may depend on.</summary>
    private static string? NormalizeDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            return null;

        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string Describe(string? value) =>
        string.IsNullOrWhiteSpace(value) ? "<unavailable>" : "'" + value + "'";
}
