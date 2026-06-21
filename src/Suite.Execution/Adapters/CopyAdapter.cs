using System.Globalization;
using System.Text.RegularExpressions;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Win32;

namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// Performs Backup copies and config-restore merges. The copy engine is long-path aware, guards against
/// junction/symlink loops, retries transient sharing violations, and enforces secret-store protection at
/// copy time in three layers (spec §1.3):
/// <list type="number">
/// <item>an <c>Include</c> allow-list (when present, ONLY matching paths are copied);</item>
/// <item>the per-action <c>ExcludeLeaves</c>/<c>ForbiddenSources</c> from the manifest PLUS a hardened
/// built-in superset of credential/cookie/session leaves;</item>
/// <item>every file is resolved with <c>GetFinalPathNameByHandle</c> so a renamed SYMLINK to a secret store is
/// still caught, and any file reparse point (symlink) is skipped. A HARD LINK, however, is NOT de-aliased by
/// that API — both names are equal aliases of the same on-disk file and it returns whichever you opened — so a
/// multi-linked file (<c>nNumberOfLinks &gt; 1</c>) is instead REFUSED outright as a possible hardlink alias.</item>
/// </list>
/// <see cref="Merge"/> never blindly overwrites — it backs the destination up to a timestamped <c>.bak</c>.
/// </summary>
public sealed class CopyAdapter : ICopyAdapter
{
    private const int MaxRetries = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(150);
    private readonly Win32PathCanonicalizer _canon = new();

    // Hardened built-in superset: credential / cookie / autofill / session stores that must NEVER be copied.
    private static readonly string[] ForbiddenSourceLeaves =
    {
        // Chromium
        "Login Data", "Login Data For Account", "Local State", "Cookies", "Web Data",
        // Firefox
        "key4.db", "key3.db", "logins.json", "cert9.db", "signons.sqlite", "cookies.sqlite",
        "cookies.sqlite-wal", "cookies.sqlite-shm",
        // Firefox session / form / web storage (tokens, autofill)
        "sessionstore.jsonlz4", "sessionstore.js", "sessionstore-backups",
        "formhistory.sqlite", "webappsstore.sqlite", "storage",
    };

    /// <inheritdoc />
    public void Copy(CopyAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        var ex = Exclusions.From(action, _canon);
        GuardForbiddenSource(action.Source, ex);

        if (Directory.Exists(action.Source))
        {
            string root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(action.Source));
            CopyTree(action.Source, action.Destination, root, ex, new HashSet<string>(StringComparer.OrdinalIgnoreCase));
            return;
        }

        if (File.Exists(action.Source))
        {
            if (!ex.AllowsFile(action.Source, action.Source))
                return; // a single forbidden/excluded/reparse source file → nothing copied
            CopyFileWithRetry(action.Source, action.Destination);
            return;
        }

        throw new FileNotFoundException($"Copy source does not exist: {action.Source}", action.Source);
    }

    /// <inheritdoc />
    public void Merge(RestoreMergeAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (!File.Exists(action.Source))
            throw new FileNotFoundException($"Merge source does not exist: {action.Source}", action.Source);

        string dest = action.Destination;

        // Destination-side TOCTOU re-check (mirrors RecycleBinFileDeleteAdapter's pre-op reparse re-check):
        // the gate canonicalized the destination at authorize-time, but a same-user attacker could swap a
        // destination parent → junction between then and now, redirecting the write into a protected tree.
        GuardDestinationNotReparse(dest);

        string? destDir = Path.GetDirectoryName(LongPath(dest));
        if (!string.IsNullOrEmpty(destDir))
            Directory.CreateDirectory(destDir);

        if (action.CreateBak && File.Exists(dest))
            BackupToUniqueBak(dest);

        // F3 (crash-atomic restore): never write the live config in place. A direct File.Copy(overwrite)
        // onto the destination leaves a half-written / corrupt config if the process is killed mid-copy.
        // Instead stage the new bytes into a sibling temp file and atomically swap it into place — the
        // destination is replaced as a single filesystem operation, so an interrupted restore leaves EITHER
        // the untouched old file OR the complete new one, never a torn one. The .bak above is preserved.
        AtomicWrite(action.Source, dest);
    }

    /// <summary>
    /// Collision-proof backup of the existing destination before a restore overwrites it. The previous
    /// 1-second timestamp (<c>yyyyMMdd_HHmmss</c>) collided when two restore merges hit the SAME destination
    /// within the same second AND the copy used <c>overwrite: true</c> — the second backup clobbered the first,
    /// destroying the user's original. This uses a high-resolution stamp PLUS a short Guid and creates the
    /// <c>.bak</c> with <see cref="FileMode.CreateNew"/> (never overwrite), looping to a fresh name on the rare
    /// stamp+Guid tie. The original is therefore always recoverable from SOME <c>.bak</c>, never lost.
    /// </summary>
    private static void BackupToUniqueBak(string dest)
    {
        string longDest = LongPath(dest);
        for (int attempt = 1; ; attempt++)
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmssfffffff", CultureInfo.InvariantCulture);
            string suffix = Guid.NewGuid().ToString("N").Substring(0, 8);
            string bak = LongPath($"{dest}.bak.{stamp}.{suffix}");
            try
            {
                // FileMode.CreateNew → throws if the name already exists, so a backup can NEVER overwrite an
                // earlier one (the whole point of the fix). Copy the bytes ourselves instead of routing the
                // .bak through the overwrite copy path.
                using var src = new FileStream(longDest, FileMode.Open, FileAccess.Read, FileShare.Read);
                using var bakStream = new FileStream(bak, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                src.CopyTo(bakStream);
                return;
            }
            catch (IOException) when (attempt < MaxRetries)
            {
                // Either an astronomically unlikely stamp+Guid tie (CreateNew refused) or a transient sharing
                // violation on the source — spin a fresh name and retry. The retry never overwrites because
                // each attempt creates a brand-new uniquely-named .bak.
                Thread.Sleep(RetryDelay);
            }
        }
    }

    /// <summary>
    /// F3 atomic restore write: copy <paramref name="source"/> into a sibling <c>.wcktmp</c> staging file in
    /// the destination directory (same volume → the swap is a metadata-only rename), then atomically replace
    /// the destination with it. When the destination does not yet exist, an empty placeholder is created first
    /// so <see cref="File.Replace(string,string,string)"/>'s atomic swap can be used uniformly. <c>File.Move</c>
    /// is banned (BannedSymbols), so <see cref="File.Replace(string,string,string)"/> is the only sanctioned
    /// atomic swap. A leftover staging file from a previous crash is overwritten, so the operation is
    /// idempotent on resume.
    /// </summary>
    private static void AtomicWrite(string source, string dest)
    {
        string longDest = LongPath(dest);
        string? dir = Path.GetDirectoryName(longDest);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        // A deterministic, per-destination staging name keeps a crashed prior attempt from accumulating temp
        // files — the next run overwrites it. It lives beside the destination, on the SAME volume, so the
        // replace is atomic (a cross-volume File.Replace is not allowed and would not be atomic anyway).
        string staging = longDest + ".wcktmp";

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(LongPath(source), staging, overwrite: true);

                if (File.Exists(longDest))
                {
                    // Atomic in-place swap; no separate backup here (.bak already taken by Merge).
                    File.Replace(staging, longDest, destinationBackupFileName: null);
                }
                else
                {
                    // No destination yet: create an empty placeholder, then atomically swap the staged
                    // content onto it. This keeps the create path atomic too (no torn first write).
                    using (File.Create(longDest)) { }
                    File.Replace(staging, longDest, destinationBackupFileName: null);
                }
                return;
            }
            catch (IOException) when (attempt < MaxRetries)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }

    // ---- engine ----------------------------------------------------------------------------

    private void CopyTree(string sourceDir, string destDir, string sourceRoot, Exclusions ex, HashSet<string> visitedRealDirs)
    {
        string real = TryGetRealPath(sourceDir);
        if (!visitedRealDirs.Add(real))
            return;

        // Never follow a directory reparse point into another tree (junction-loop + redirect guard).
        if (IsReparsePoint(sourceDir))
            return;

        // Destination-side TOCTOU re-check at the write boundary (mirror of the delete adapter): refuse to
        // create/write under a destination whose existing parent chain contains a junction/symlink swapped in
        // after authorize-time, which would redirect the copy into a protected tree (fail-closed).
        GuardDestinationNotReparse(destDir);

        Directory.CreateDirectory(LongPath(destDir));

        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            if (!ex.AllowsFile(file, sourceRoot))
                continue;
            CopyFileWithRetry(file, Path.Combine(destDir, Path.GetFileName(file)));
        }

        foreach (string sub in Directory.EnumerateDirectories(sourceDir))
        {
            if (ex.IsExcludedDir(sub, sourceRoot))
                continue;
            CopyTree(sub, Path.Combine(destDir, Path.GetFileName(sub)), sourceRoot, ex, visitedRealDirs);
        }
    }

    private static void CopyFileWithRetry(string source, string destination)
    {
        // Destination-side TOCTOU re-check immediately before the write (mirror of the delete adapter): a
        // destination parent raced into a junction/symlink after authorize-time would redirect the file into
        // a protected/other tree. No legitimate copy targets a reparse-point parent, so this fails closed.
        GuardDestinationNotReparse(destination);

        string? dir = Path.GetDirectoryName(LongPath(destination));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                File.Copy(LongPath(source), LongPath(destination), overwrite: true);
                return;
            }
            catch (IOException) when (attempt < MaxRetries)
            {
                Thread.Sleep(RetryDelay);
            }
        }
    }

    private void GuardForbiddenSource(string source, Exclusions ex)
    {
        string leaf = Path.GetFileName(source.TrimEnd('\\', '/'));
        if (ex.IsForbiddenLeaf(leaf) || ex.IsForbiddenFull(source))
            throw new ForbiddenSourceException($"Refusing to copy a protected/forbidden source: {leaf} (spec §1.3).");
    }

    private static bool IsReparsePoint(string path)
    {
        try { return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint); }
        catch { return false; }
    }

    /// <summary>
    /// Write-boundary TOCTOU re-check on the COPY/MERGE destination, the write-side counterpart of the
    /// delete adapter's pre-op <c>GetAttributes(...).ReparsePoint</c> re-check. The destination leaf itself
    /// usually does not exist yet, so this walks the destination's EXISTING ancestor chain and refuses if any
    /// existing component (the leaf or any parent) is a junction/symlink — that is how an attacker would
    /// redirect the write into a protected tree between gate-authorize and the copy. Throws
    /// <see cref="DestinationReparseException"/> (fail-closed); no legitimate destination is a reparse point.
    /// </summary>
    private static void GuardDestinationNotReparse(string destination)
    {
        if (string.IsNullOrWhiteSpace(destination))
            return;

        string? cursor;
        try
        {
            cursor = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destination));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return; // a malformed path can never be written through; the copy will fail cleanly downstream
        }

        // Walk up to the nearest existing component and check it (and every existing ancestor) for a reparse
        // point. A not-yet-existing leaf/segment cannot be a live junction, so only existing ones matter.
        while (cursor is not null)
        {
            if (File.Exists(cursor) || Directory.Exists(cursor))
            {
                if (IsReparsePoint(cursor))
                    throw new DestinationReparseException(
                        $"Refusing to copy into a reparse point (junction/symlink) destination component: {cursor} (spec §1.3/§3).");
            }

            string? parent = Path.GetDirectoryName(cursor);
            if (parent is null || string.Equals(parent, cursor, StringComparison.OrdinalIgnoreCase))
                break;
            cursor = parent;
        }
    }

    private static string TryGetRealPath(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToLowerInvariant(); }
        catch { return path.ToLowerInvariant(); }
    }

    internal static string LongPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 260 || path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path.Substring(2);
        return @"\\?\" + path;
    }

    /// <summary>The resolved exclusion policy for one copy action: include allow-list, forbidden leaves/paths,
    /// and the file-reparse skip rule.</summary>
    private sealed class Exclusions
    {
        private readonly HashSet<string> _leaves;
        private readonly IReadOnlyList<Regex> _leafGlobs;
        private readonly HashSet<string> _forbiddenFull;
        private readonly IReadOnlyList<string> _include;
        private readonly Win32PathCanonicalizer _canon;

        private Exclusions(HashSet<string> leaves, IReadOnlyList<Regex> leafGlobs, HashSet<string> forbiddenFull, IReadOnlyList<string> include, Win32PathCanonicalizer canon)
        {
            _leaves = leaves;
            _leafGlobs = leafGlobs;
            _forbiddenFull = forbiddenFull;
            _include = include;
            _canon = canon;
        }

        public static Exclusions From(CopyAction action, Win32PathCanonicalizer canon)
        {
            // The hardened built-in superset is all EXACT leaves. An ExcludeLeaves entry containing '*' is a
            // LEAF GLOB (e.g. "*.key", "id_rsa*", "*Cache*") — split it out so a real secret/cache leaf is caught,
            // not just a file literally named "*.key" (council critic F3/HIGH: ExcludeLeaves was exact-match only,
            // which left the migration secret-glob overlay + recipe cache excludes inert at copy time).
            var leaves = new HashSet<string>(ForbiddenSourceLeaves, StringComparer.OrdinalIgnoreCase);
            var globs = new List<Regex>();
            foreach (string leaf in action.ExcludeLeaves)
            {
                if (string.IsNullOrWhiteSpace(leaf)) continue;
                string trimmed = leaf.Trim();
                if (trimmed.Contains('*')) globs.Add(CompileLeafGlob(trimmed));
                else leaves.Add(trimmed);
            }

            var full = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in action.ForbiddenSources)
                if (!string.IsNullOrWhiteSpace(p)) full.Add(NormalizeFull(p));

            return new Exclusions(leaves, globs, full, action.Include, canon);
        }

        public bool IsForbiddenLeaf(string leaf) => _leaves.Contains(leaf) || MatchesLeafGlob(leaf);
        public bool IsForbiddenFull(string path) => _forbiddenFull.Count > 0 && _forbiddenFull.Contains(NormalizeFull(path));

        /// <summary>A file may be copied only if it passes the include allow-list (if any), is not excluded/
        /// forbidden by literal OR resolved name, is not a reparse point (symlink), and is not a multi-linked
        /// (hard-linked) file. A symlink is de-referenced by <c>GetFinalPathNameByHandle</c>, but a HARD LINK is
        /// NOT — both names are equal aliases of the same on-disk file and the API returns whichever you opened,
        /// so a hard link under an innocuous leaf is refused outright (fail-safe) rather than copied.</summary>
        public bool AllowsFile(string file, string sourceRoot)
        {
            // Skip any file reparse point outright — a symlink can alias a secret store under an innocent name.
            try { if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) return false; }
            catch { return false; }

            // A hard link cannot be de-aliased by GetFinalPathNameByHandle (it returns the opened name, not the
            // "secret" sibling name), so a hard link under a benign leaf would slip past the leaf-name filter.
            // Refuse any multi-linked file: "multi-linked file (possible hardlink alias) excluded".
            if (HardLinkProbe.IsMultiLinked(file))
                return false;

            // Resolve the true target so a renamed symlink to "Login Data" is still caught.
            var canon = _canon.Canonicalize(file);
            string literalLeaf = Path.GetFileName(file.TrimEnd('\\', '/'));
            string resolvedLeaf = Path.GetFileName(canon.FinalPath.TrimEnd('\\', '/'));

            if (_leaves.Contains(literalLeaf) || _leaves.Contains(resolvedLeaf)
                || MatchesLeafGlob(literalLeaf) || MatchesLeafGlob(resolvedLeaf))
                return false;
            if (IsForbiddenFull(file) || IsForbiddenFull(canon.FinalPath))
                return false;

            if (_include.Count > 0)
            {
                string rel = RelativePath(file, sourceRoot);
                if (!MatchesAnyInclude(rel))
                    return false;
            }
            return true;
        }

        /// <summary>True when a directory is excluded by leaf name (so the whole sub-tree is skipped).</summary>
        public bool IsExcludedDir(string dir, string sourceRoot)
        {
            string leaf = Path.GetFileName(dir.TrimEnd('\\', '/'));
            if (_leaves.Contains(leaf) || MatchesLeafGlob(leaf))
                return true;
            // With an include allow-list, only prune a dir when nothing under it could ever match.
            if (_include.Count > 0)
            {
                string rel = RelativePath(dir, sourceRoot);
                if (!CouldContainInclude(rel))
                    return true;
            }
            return false;
        }

        private bool MatchesAnyInclude(string rel)
            => _include.Any(p => GlobMatches(rel, Normalize(p)));

        // A directory could contain an include if any include glob is the dir, under the dir, or a deep glob.
        private bool CouldContainInclude(string relDir)
        {
            string d = Normalize(relDir);
            foreach (string raw in _include)
            {
                string p = Normalize(raw);
                if (p == "**" || p.StartsWith(d + "/", StringComparison.Ordinal) || p == d
                    || d.StartsWith(TrimGlob(p) + "/", StringComparison.Ordinal) || TrimGlob(p).StartsWith(d + "/", StringComparison.Ordinal)
                    || p.Contains('*'))
                    return true;
            }
            return false;
        }

        private static bool GlobMatches(string rel, string pattern)
        {
            string r = Normalize(rel);
            if (pattern == "**") return true;
            if (pattern.Contains('*'))
            {
                Regex rx = CompilePathGlob(pattern);
                // A separator-free pattern (e.g. "*.md") is a LEAF glob — match the leaf too so a bare
                // wildcard keeps its historical "any file named X" meaning. Patterns that contain a
                // separator are anchored to the full relative path.
                if (rx.IsMatch(r)) return true;
                return !pattern.Contains('/') && rx.IsMatch(LeafOf(r));
            }
            return r == pattern || r.StartsWith(pattern + "/", StringComparison.Ordinal) || LeafOf(r) == pattern;
        }

        /// <summary>
        /// Translate a path glob to an anchored regex with TRUE <c>**</c> semantics: <c>**</c> matches across
        /// path separators (any depth, including zero segments when written as a leading/middle <c>**/</c>),
        /// while a single <c>*</c> stays within one segment (<c>[^/]*</c>). Separators are normalized to '/'
        /// and everything else is escaped literally. Anchored + case-insensitive.
        /// </summary>
        private static Regex CompilePathGlob(string pattern)
        {
            string p = Normalize(pattern);
            var sb = new System.Text.StringBuilder("^");
            for (int i = 0; i < p.Length; i++)
            {
                char c = p[i];
                if (c == '*')
                {
                    bool doubleStar = i + 1 < p.Length && p[i + 1] == '*';
                    if (doubleStar)
                    {
                        i++; // consume the second '*'
                        // A "**/" segment matches zero OR more leading path segments, so "**/x" also matches
                        // a bare "x". Consume the following '/' and emit an optional "any-segments/" group.
                        if (i + 1 < p.Length && p[i + 1] == '/')
                        {
                            i++; // consume the '/'
                            sb.Append("(?:.*/)?");
                        }
                        else
                        {
                            sb.Append(".*"); // trailing/standalone "**" → anything incl. separators
                        }
                    }
                    else
                    {
                        sb.Append("[^/]*"); // single '*' → within one segment
                    }
                }
                else
                {
                    sb.Append(Regex.Escape(c.ToString()));
                }
            }
            sb.Append('$');
            return new Regex(sb.ToString(), RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }

        /// <summary>True when a leaf name matches any '*'-bearing ExcludeLeaves entry (secret-glob overlay,
        /// recipe cache/blob excludes). Leaf glob: '*' → any run of non-separator chars; anchored, case-insensitive.</summary>
        private bool MatchesLeafGlob(string leaf)
        {
            if (string.IsNullOrEmpty(leaf)) return false;
            foreach (Regex rx in _leafGlobs)
                if (rx.IsMatch(leaf)) return true;
            return false;
        }

        private static Regex CompileLeafGlob(string glob)
            => new("^" + Regex.Escape(glob).Replace("\\*", "[^\\\\/]*") + "$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

        private static string TrimGlob(string p) => p.Replace("/**", string.Empty).Replace("*", string.Empty).Trim('/');
        private static string LeafOf(string rel) { int i = rel.LastIndexOf('/'); return i < 0 ? rel : rel[(i + 1)..]; }
        private static string Normalize(string p) => p.Replace('\\', '/').Trim('/').ToLowerInvariant();

        private static string RelativePath(string path, string sourceRoot)
        {
            try { return Normalize(Path.GetRelativePath(sourceRoot, path)); }
            catch { return Normalize(Path.GetFileName(path)); }
        }

        private static string NormalizeFull(string path)
        {
            try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)).ToLowerInvariant(); }
            catch { return path.ToLowerInvariant(); }
        }
    }
}
