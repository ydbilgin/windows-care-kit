using System.IO;

namespace WindowsCareKit.Core.Modules.Migration.Discovery;

/// <summary>
/// Core implementation of <see cref="IAppDiscoveryProbe"/>. Walks <see cref="ProfileRoots"/> to find
/// recipe-less app directories; applies cache/secret prune; emits <see cref="DiscoveredApp"/> records.
///
/// <para><b>Security invariants (must-not-cut):</b>
/// <list type="bullet">
/// <item>Policy-owned walker — never calls a recursive BCL enumerate; calls <see cref="IDiscoveryFileSystem.EnumerateChildren"/>
///   one level at a time so per-directory prune decisions are always in Core's hands.</item>
/// <item>Reparse-point dirs are NEVER descended; candidate-root reparse points are surfaced with
///   <see cref="DiscoveryScanStatus.NotTraversedReparse"/> (F3 — do not silently omit).</item>
/// <item>Only the candidate root is canonicalized; non-reparse children are lexically contained in
///   their already-validated parent and do not require an additional canonicalize call (F2).</item>
/// <item>Global entry-count cap (<see cref="DiscoveryScanOptions.MaxGlobalEntries"/>) is the PRIMARY
///   determinism boundary; wall-time via <see cref="CancellationToken"/> is a backstop only (F2).</item>
/// <item><see cref="DiscoveredApp"/> carries only safe aggregates — no leaf paths, no matched glob names.</item>
/// </list>
/// </para>
/// </summary>
public sealed class AppDiscoveryEngine : IAppDiscoveryProbe
{
    private readonly ProfileRoots _roots;
    private readonly RecipePathResolver _resolver;
    private readonly IDiscoveryFileSystem _fs;

    public AppDiscoveryEngine(ProfileRoots roots, RecipePathResolver resolver, IDiscoveryFileSystem fs)
    {
        _roots = roots ?? throw new ArgumentNullException(nameof(roots));
        _resolver = resolver ?? throw new ArgumentNullException(nameof(resolver));
        _fs = fs ?? throw new ArgumentNullException(nameof(fs));
    }

    /// <inheritdoc/>
    public IReadOnlyList<DiscoveredApp> Discover(DiscoveryScanOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);

        var results = new List<DiscoveredApp>();
        int globalEntries = 0;

        // Enumerate candidates: direct child dirs of AppData and LocalAppData, plus dot-dirs of UserProfile.
        foreach ((KnownFolder folder, string root, bool dotDirsOnly) in Roots())
        {
            foreach (DiscoveryFileSystemEntry entry in SafeEnumerateChildren(root))
            {
                if (!entry.IsDirectory)
                    continue;

                // UserProfile: only dot-directories (name starts with '.').
                string leaf = Path.GetFileName(entry.Path);
                if (dotDirsOnly && !leaf.StartsWith('.'))
                    continue;

                // Cache-named or secret-named dirs at the top level are pruned (don't emit at all).
                if (CacheGlobOverlay.IsCacheLeaf(leaf) || SecretGlobOverlay.IsSecretLeaf(leaf))
                    continue;

                string candidatePath = entry.Path;

                // F3: if the candidate dir itself is a reparse point, surface it with NotTraversedReparse.
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    string relPath = MakeRelative(root, candidatePath);
                    results.Add(new DiscoveredApp(
                        Id: leaf,
                        DisplayName: leaf,
                        Root: folder,
                        RelativePath: relPath,
                        LastModifiedUtc: null,
                        Portability: PortabilityClass.Partial,  // PR-1 constant default; signal-based MachineLocked deferred to PR-2
                        Status: DiscoveryScanStatus.NotTraversedReparse));
                    continue;
                }

                // F2: canonicalize ONLY reparse-point dirs (the candidate root may follow a reparse).
                // A non-reparse candidate is lexically contained in root — no canonicalize needed.
                // But we do canonicalize here since the candidate itself might resolve through one.
                string? canonical = _fs.Canonicalize(candidatePath);
                if (canonical is null || !RecipePathResolver.IsContained(root, canonical))
                    continue; // escapes profile root or unresolvable — skip silently

                string relativePathForRecord = MakeRelative(root, candidatePath);

                // Walk the subtree bounded by depth and global entry budget.
                DiscoveryScanStatus status;
                DateTime? lastMod;
                (status, lastMod, globalEntries) = WalkSubtree(
                    candidatePath, options, ct, globalEntries, depth: 0);

                results.Add(new DiscoveredApp(
                    Id: leaf,
                    DisplayName: leaf,
                    Root: folder,
                    RelativePath: relativePathForRecord,
                    LastModifiedUtc: lastMod,
                    Portability: PortabilityClass.Partial,  // PR-1 constant default; signal-based MachineLocked deferred to PR-2
                    Status: status));
            }
        }

        return results;
    }

    // Returns (status, maxAllowedModTime, updatedGlobalEntries).
    private (DiscoveryScanStatus Status, DateTime? LastMod, int GlobalEntries) WalkSubtree(
        string dir, DiscoveryScanOptions options, CancellationToken ct, int globalEntries, int depth)
    {
        DateTime? lastMod = null;
        bool budgetExhausted = false;

        if (ct.IsCancellationRequested)
            return (DiscoveryScanStatus.Cancelled, null, globalEntries);

        // F5/A: the global budget is already spent before this app contributed anything — report honestly
        // (an empty/fully-pruned later app must not masquerade as Complete once the cap is reached).
        if (globalEntries >= options.MaxGlobalEntries)
            return (DiscoveryScanStatus.IncompleteBudget, null, globalEntries);

        List<DiscoveryFileSystemEntry> children;
        try
        {
            children = _fs.EnumerateChildren(dir).ToList(); // materialize to catch enumeration errors
        }
        catch
        {
            return (DiscoveryScanStatus.Inaccessible, null, globalEntries);
        }

        // F2: impose a deterministic order — production EnumerateChildren order is filesystem-dependent, so
        // without this the cap-bite point (and thus LastModified / Complete-vs-Incomplete) is not reproducible.
        children.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path));

        foreach (DiscoveryFileSystemEntry entry in children)
        {
            if (ct.IsCancellationRequested)
                return (DiscoveryScanStatus.Cancelled, lastMod, globalEntries);

            string entryLeaf = Path.GetFileName(entry.Path);

            if (entry.IsDirectory)
            {
                // Prune cache/secret dirs and reparse-point children — never descend.
                if (CacheGlobOverlay.IsCacheLeaf(entryLeaf) || SecretGlobOverlay.IsSecretLeaf(entryLeaf))
                    continue; // pruned; entries NOT counted (prune happens before budget consumption, F5)

                // Per F2: skip reparse-point child dirs. EnumerateChildren RETURNS reparse entries (it does
                // not omit them), so this guard is what actually prunes a reparse child directory here.
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;

                // Count this directory entry against global budget.
                globalEntries++;
                if (globalEntries > options.MaxGlobalEntries)
                {
                    budgetExhausted = true;
                    break;
                }

                // Recurse if within depth limit.
                if (depth + 1 < options.MaxDepth)
                {
                    DiscoveryScanStatus subStatus;
                    DateTime? subMod;
                    (subStatus, subMod, globalEntries) = WalkSubtree(
                        entry.Path, options, ct, globalEntries, depth + 1);

                    if (subStatus == DiscoveryScanStatus.Cancelled)
                        return (DiscoveryScanStatus.Cancelled, Max(lastMod, subMod), globalEntries);
                    if (subStatus == DiscoveryScanStatus.IncompleteBudget)
                    {
                        budgetExhausted = true;
                        lastMod = Max(lastMod, subMod);
                        break;
                    }

                    lastMod = Max(lastMod, subMod);
                }
            }
            else
            {
                // File leaf: exclude secret/cache names from activity tracking (never surfaced in result).
                if (SecretGlobOverlay.IsSecretLeaf(entryLeaf) || CacheGlobOverlay.IsCacheLeaf(entryLeaf))
                    continue;

                // Count toward global budget.
                globalEntries++;
                if (globalEntries > options.MaxGlobalEntries)
                {
                    budgetExhausted = true;
                    break;
                }

                // Update activity from allowed file-leaf modtime.
                lastMod = Max(lastMod, entry.LastWriteTimeUtc);
            }
        }

        if (budgetExhausted)
            return (DiscoveryScanStatus.IncompleteBudget, lastMod, globalEntries);

        return (DiscoveryScanStatus.Complete, lastMod, globalEntries);
    }

    private IReadOnlyList<DiscoveryFileSystemEntry> SafeEnumerateChildren(string dir)
    {
        List<DiscoveryFileSystemEntry> list;
        try { list = _fs.EnumerateChildren(dir).ToList(); }
        catch { return Array.Empty<DiscoveryFileSystemEntry>(); }
        list.Sort(static (a, b) => string.CompareOrdinal(a.Path, b.Path)); // F2: deterministic candidate order
        return list;
    }

    private IEnumerable<(KnownFolder Folder, string Root, bool DotDirsOnly)> Roots()
    {
        yield return (KnownFolder.AppData, _resolver.RootFor(KnownFolder.AppData), false);
        yield return (KnownFolder.LocalAppData, _resolver.RootFor(KnownFolder.LocalAppData), false);
        yield return (KnownFolder.UserProfile, _resolver.RootFor(KnownFolder.UserProfile), true);
    }

    private static string MakeRelative(string root, string absolute)
    {
        string r = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string a = absolute.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (a.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            return a.Substring(r.Length + 1);
        return a; // fallback (should not happen when IsContained was verified)
    }

    private static DateTime? Max(DateTime? a, DateTime? b)
    {
        if (a is null) return b;
        if (b is null) return a;
        return a.Value > b.Value ? a : b;
    }
}
