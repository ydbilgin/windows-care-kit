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
/// <item>every file is resolved with <c>GetFinalPathNameByHandle</c> and any file reparse point (symlink/
/// hardlink alias of a secret store) is skipped — leaf-name checks alone cannot catch a renamed link.</item>
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
        {
            string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string bak = $"{dest}.bak.{stamp}";
            CopyFileWithRetry(dest, bak);
        }

        CopyFileWithRetry(action.Source, dest);
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
        private readonly HashSet<string> _forbiddenFull;
        private readonly IReadOnlyList<string> _include;
        private readonly Win32PathCanonicalizer _canon;

        private Exclusions(HashSet<string> leaves, HashSet<string> forbiddenFull, IReadOnlyList<string> include, Win32PathCanonicalizer canon)
        {
            _leaves = leaves;
            _forbiddenFull = forbiddenFull;
            _include = include;
            _canon = canon;
        }

        public static Exclusions From(CopyAction action, Win32PathCanonicalizer canon)
        {
            var leaves = new HashSet<string>(ForbiddenSourceLeaves, StringComparer.OrdinalIgnoreCase);
            foreach (string leaf in action.ExcludeLeaves)
                if (!string.IsNullOrWhiteSpace(leaf)) leaves.Add(leaf.Trim());

            var full = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string p in action.ForbiddenSources)
                if (!string.IsNullOrWhiteSpace(p)) full.Add(NormalizeFull(p));

            return new Exclusions(leaves, full, action.Include, canon);
        }

        public bool IsForbiddenLeaf(string leaf) => _leaves.Contains(leaf);
        public bool IsForbiddenFull(string path) => _forbiddenFull.Count > 0 && _forbiddenFull.Contains(NormalizeFull(path));

        /// <summary>A file may be copied only if it passes the include allow-list (if any), is not excluded/
        /// forbidden by literal OR resolved name, and is not a reparse point (symlink/hardlink to a secret).</summary>
        public bool AllowsFile(string file, string sourceRoot)
        {
            // Skip any file reparse point outright — a link can alias a secret store under an innocent name.
            try { if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint)) return false; }
            catch { return false; }

            // Resolve the true target so a renamed hardlink/symlink to "Login Data" is still caught.
            var canon = _canon.Canonicalize(file);
            string literalLeaf = Path.GetFileName(file.TrimEnd('\\', '/'));
            string resolvedLeaf = Path.GetFileName(canon.FinalPath.TrimEnd('\\', '/'));

            if (_leaves.Contains(literalLeaf) || _leaves.Contains(resolvedLeaf))
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
            if (_leaves.Contains(leaf))
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
            if (pattern.EndsWith("/**", StringComparison.Ordinal))
            {
                string prefix = pattern[..^3];
                return r == prefix || r.StartsWith(prefix + "/", StringComparison.Ordinal);
            }
            if (pattern.Contains('*'))
            {
                var rx = new Regex("^" + Regex.Escape(pattern).Replace("\\*", "[^/]*") + "$", RegexOptions.IgnoreCase);
                return rx.IsMatch(r) || rx.IsMatch(LeafOf(r));
            }
            return r == pattern || r.StartsWith(pattern + "/", StringComparison.Ordinal) || LeafOf(r) == pattern;
        }

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
