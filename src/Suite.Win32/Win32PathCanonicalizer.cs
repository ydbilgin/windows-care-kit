using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Win32;

/// <summary>
/// Resolves a path to its true target with <c>GetFinalPathNameByHandle</c>, following junctions and
/// symlinks. This is what the SafetyGate relies on to stop a reparse point from smuggling a protected
/// directory past the path checks (spec §3, §4). <c>Path.GetFullPath</c> alone does not follow links.
/// </summary>
public sealed class Win32PathCanonicalizer : IPathCanonicalizer
{
    private const uint OPEN_EXISTING = 3;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000; // required to open a directory handle
    private const uint FILE_SHARE_READ = 0x1;
    private const uint FILE_SHARE_WRITE = 0x2;
    private const uint FILE_SHARE_DELETE = 0x4;
    private const uint FILE_NAME_NORMALIZED = 0x0;

    public CanonicalPath Canonicalize(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return new CanonicalPath(path ?? string.Empty, string.Empty, false, false);

        bool leafReparse = IsReparsePoint(path);

        // 1) The leaf itself exists and can be opened: resolve it directly (follows its own reparse point).
        string? final = TryGetFinalPath(path);
        if (final is not null)
            return new CanonicalPath(path, final, leafReparse, true);

        // 2) The leaf does not exist (typical for a copy/restore destination). Resolve reparse points on the
        //    PARENT chain: walk up to the nearest existing ancestor, resolve THAT via GetFinalPathNameByHandle
        //    (which follows every junction/symlink in its path), then re-append the not-yet-existing segments.
        //    This closes the gap where a junction parent under a missing leaf was judged on its benign literal.
        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return new CanonicalPath(path, path, leafReparse, false);
        }

        var tail = new List<string>();
        string? cursor = full;
        while (cursor is not null && !Directory.Exists(cursor))
        {
            string? parent = Path.GetDirectoryName(cursor);
            if (parent is null)
            {
                cursor = null; // reached a root and nothing on the way exists
                break;
            }
            tail.Add(Path.GetFileName(cursor));
            cursor = parent;
        }

        if (cursor is null || !Directory.Exists(cursor))
            return new CanonicalPath(path, full, leafReparse, false); // no existing ancestor → unresolved

        bool ancestorReparse = leafReparse || AnyReparseUpChain(cursor);
        string? ancestorFinal = TryGetFinalPath(cursor);
        if (ancestorFinal is null)
            return new CanonicalPath(path, full, ancestorReparse, false); // ancestor unopenable → fail closed

        tail.Reverse();
        string combined = ancestorFinal;
        foreach (string seg in tail)
            combined = Path.Combine(combined, seg);

        // If resolving the ancestor changed the path, a junction/symlink was followed.
        bool resolvedDiffers = !string.Equals(
            Path.TrimEndingDirectorySeparator(ancestorFinal),
            Path.TrimEndingDirectorySeparator(cursor),
            StringComparison.OrdinalIgnoreCase);

        return new CanonicalPath(path, combined, ancestorReparse || resolvedDiffers, true);
    }

    /// <summary>
    /// Literal-form hardening (L12): full-qualify → expand 8.3 short names via <c>GetLongPathNameW</c> →
    /// strip trailing dots/spaces per segment. Does NOT follow reparse points. Best-effort: on any failure
    /// it returns the most-normalized form it has (never throws).
    /// </summary>
    public string ExpandLongPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return path ?? string.Empty;

        string full;
        try
        {
            full = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return StripTrailingDotSpacePerSegment(path);
        }

        string expanded = TryGetLongPathName(full) ?? full;
        return StripTrailingDotSpacePerSegment(expanded);
    }

    private static string? TryGetLongPathName(string path)
    {
        var sb = new StringBuilder(1024);
        uint len = GetLongPathName(path, sb, (uint)sb.Capacity);
        if (len == 0)
            return null; // the path may not exist — caller falls back to the full path
        if (len > sb.Capacity)
        {
            sb.EnsureCapacity((int)len + 1);
            len = GetLongPathName(path, sb, (uint)sb.Capacity);
            if (len == 0)
                return null;
        }
        return sb.ToString();
    }

    /// <summary>Strip trailing '.'/' ' from each path segment (NTFS treats "foo." / "foo " as "foo").</summary>
    internal static string StripTrailingDotSpacePerSegment(string path)
    {
        if (string.IsNullOrEmpty(path))
            return path;

        int prefixLen = 0;
        // Preserve a drive-qualifier ("C:") or UNC prefix so we don't trim it away.
        if (path.Length >= 2 && char.IsLetter(path[0]) && path[1] == ':')
            prefixLen = 2;

        string prefix = path.Substring(0, prefixLen);
        string rest = path.Substring(prefixLen);

        char[] seps = { '\\', '/' };
        string[] segments = rest.Split(seps);
        for (int i = 0; i < segments.Length; i++)
            segments[i] = segments[i].TrimEnd('.', ' ');

        // Rejoin with backslashes (the canonical Windows separator); empty leading segment keeps the root slash.
        return prefix + string.Join('\\', segments);
    }

    /// <summary>True when the directory or any of its ancestors is a reparse point.</summary>
    private static bool AnyReparseUpChain(string dir)
    {
        string? cur = dir;
        while (cur is not null)
        {
            if (IsReparsePoint(cur))
                return true;
            string? parent = Path.GetDirectoryName(cur);
            if (parent is null || string.Equals(parent, cur, StringComparison.OrdinalIgnoreCase))
                break;
            cur = parent;
        }
        return false;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
        }
        catch
        {
            return false; // does not exist / inaccessible — treated as not-a-reparse for this flag
        }
    }

    private static string? TryGetFinalPath(string path)
    {
        using SafeFileHandle handle = CreateFile(
            path,
            0, // query only; no read/write access needed for the final-path query
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE,
            IntPtr.Zero,
            OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS,
            IntPtr.Zero);

        if (handle.IsInvalid)
            return null;

        var sb = new StringBuilder(1024);
        uint len = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, FILE_NAME_NORMALIZED);
        if (len == 0)
            return null;

        if (len > sb.Capacity)
        {
            sb.EnsureCapacity((int)len + 1);
            len = GetFinalPathNameByHandle(handle, sb, (uint)sb.Capacity, FILE_NAME_NORMALIZED);
            if (len == 0)
                return null;
        }

        return StripExtendedPrefix(sb.ToString());
    }

    /// <summary>Turn the <c>\\?\</c> / <c>\\?\UNC\</c> form returned by the API into an ordinary path.</summary>
    internal static string StripExtendedPrefix(string p)
    {
        if (p.StartsWith(@"\\?\UNC\", StringComparison.Ordinal))
            return @"\\" + p.Substring(@"\\?\UNC\".Length);
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
            return p.Substring(@"\\?\".Length);
        return p;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(
        SafeFileHandle hFile,
        StringBuilder lpszFilePath,
        uint cchFilePath,
        uint dwFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(
        string lpszShortPath,
        StringBuilder lpszLongPath,
        uint cchBuffer);
}
