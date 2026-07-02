using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;
using WindowsCareKit.Core.Modules.Migration;

namespace WindowsCareKit.Win32;

/// <summary>
/// Production read-only content probe. It reads only the first configured bytes, never follows a path already
/// marked as a reparse point, and returns an inconclusive (therefore machine-bound) signature on any uncertainty.
/// </summary>
public sealed class Win32ContentSignatureProbe : IContentSignatureProbe
{
    public const int DefaultMaxBytes = ContentSignatureClassifier.DefaultMaxBytes;
    public const int DefaultDirectorySampleFileCount = 16;

    private readonly int _maxBytes;
    private readonly int _directorySampleFileCount;
    private readonly ContentSignatureOptions _defaultOptions;

    public Win32ContentSignatureProbe(
        int maxBytes = DefaultMaxBytes,
        int directorySampleFileCount = DefaultDirectorySampleFileCount,
        IReadOnlyList<string>? profileRoots = null)
    {
        if (maxBytes <= 0 || maxBytes > DefaultMaxBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), $"maxBytes must be in 1..{DefaultMaxBytes}");
        if (directorySampleFileCount <= 0)
            throw new ArgumentOutOfRangeException(nameof(directorySampleFileCount));

        _maxBytes = maxBytes;
        _directorySampleFileCount = directorySampleFileCount;
        _defaultOptions = new ContentSignatureOptions(
            (profileRoots ?? LoadProfileRoots()).ToArray());
    }

    public ContentSignature ProbeFile(string path, ContentSignatureOptions? options = null)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ContentSignature.Inconclusive();

        ContentSignatureOptions effectiveOptions = EffectiveOptions(options);
        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if (IsCloudPlaceholder(attributes))
                return ContentSignature.CloudPlaceholder();
            if (attributes.HasFlag(FileAttributes.Directory))
                return ProbeDirectory(path, effectiveOptions);
            if (attributes.HasFlag(FileAttributes.ReparsePoint) || HasReparsePointParent(path))
                return ContentSignature.Inconclusive();

            return ReadBounded(path, effectiveOptions);
        }
        catch (UnauthorizedAccessException)
        {
            return ContentSignature.Inaccessible();
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return ContentSignature.LockedNow();
        }
        catch (IOException ex) when (IsAccessDenied(ex))
        {
            return ContentSignature.Inaccessible();
        }
        catch
        {
            return ContentSignature.Inconclusive();
        }
    }

    private ContentSignatureOptions EffectiveOptions(ContentSignatureOptions? options)
    {
        if (options is null)
            return _defaultOptions;
        if (options.ProfileRoots.Count > 0)
            return options;
        return _defaultOptions with { ExpectedFormat = options.ExpectedFormat };
    }

    private ContentSignature ProbeDirectory(string path, ContentSignatureOptions? options)
    {
        if (HasReparsePointParent(path))
            return ContentSignature.Inconclusive();

        var sampled = new List<(string RelativePath, ContentSignature Signature)>();
        int eligibleSeen = 0;
        int cloudPlaceholdersSkipped = 0;
        int subtreesSkipped = 0;
        bool truncated = false;

        try
        {
            foreach (string file in EnumerateFilesDeterministically(path, () => subtreesSkipped++))
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(file);
                }
                catch
                {
                    continue;
                }

                string relative = Path.GetRelativePath(path, file).Replace('\\', '/');
                if (attributes.HasFlag(FileAttributes.Directory)
                    || attributes.HasFlag(FileAttributes.ReparsePoint)
                    || IsSecretOrCachePath(relative))
                    continue;
                if (IsCloudPlaceholder(attributes))
                {
                    cloudPlaceholdersSkipped++;
                    continue;
                }

                eligibleSeen++;
                if (sampled.Count >= _directorySampleFileCount)
                {
                    truncated = true;
                    break;
                }

                sampled.Add((relative, ReadBounded(file, options)));
            }
        }
        catch (UnauthorizedAccessException)
        {
            return ContentSignature.Inaccessible();
        }
        catch (IOException ex) when (IsAccessDenied(ex))
        {
            return ContentSignature.Inaccessible();
        }
        catch (IOException ex) when (IsSharingViolation(ex))
        {
            return ContentSignature.LockedNow();
        }
        catch
        {
            return ContentSignature.Inconclusive();
        }

        return ContentSignatureClassifier.MergeDirectory(
            sampled,
            eligibleSeen,
            truncated,
            cloudPlaceholdersSkipped,
            subtreesSkipped);
    }

    private static IEnumerable<string> EnumerateFilesDeterministically(string root, Action skippedSubtree)
    {
        var pending = new Queue<(string Path, bool IsRoot)>();
        pending.Enqueue((root, true));

        while (pending.Count > 0)
        {
            (string dir, bool isRoot) = pending.Dequeue();
            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(dir);
            }
            catch (UnauthorizedAccessException)
            {
                if (isRoot)
                    throw;
                skippedSubtree();
                continue;
            }
            catch (IOException)
            {
                if (isRoot)
                    throw;
                skippedSubtree();
                continue;
            }
            catch
            {
                // Exotic (non-UA/non-IO) enumeration failure: still count the subtree as skipped so the
                // not-analyzed tier cap applies — a silently missing subtree must never look fully analyzed.
                // Root-level exotic failures keep the pre-existing lenient skip (probe-level catch decides).
                if (!isRoot)
                    skippedSubtree();
                continue;
            }

            Array.Sort(entries, StringComparer.OrdinalIgnoreCase);
            foreach (string entry in entries)
            {
                FileAttributes attributes;
                try
                {
                    attributes = File.GetAttributes(entry);
                }
                catch
                {
                    continue;
                }

                if (attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                if (attributes.HasFlag(FileAttributes.Directory))
                    pending.Enqueue((entry, false));
                else
                    yield return entry;
            }
        }
    }

    private static bool HasReparsePointParent(string path)
    {
        string current = Path.GetFullPath(path);
        current = Path.TrimEndingDirectorySeparator(current);
        string? parent = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(parent))
            return false;
        current = parent;

        while (true)
        {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return true;

            string trimmed = Path.TrimEndingDirectorySeparator(current);
            string? nextParent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(nextParent) || string.Equals(nextParent, current, StringComparison.OrdinalIgnoreCase))
                return false;
            current = nextParent;
        }
    }

    private ContentSignature ReadBounded(string path, ContentSignatureOptions? options)
    {
        byte[] buffer = new byte[_maxBytes];
        int total = 0;

        using SafeFileHandle handle = CreateReadHandleNoRecall(path);
        using var stream = new FileStream(handle, FileAccess.Read, bufferSize: 4096);
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
                break;
            total += read;
        }

        return ContentSignatureClassifier.Classify(buffer.AsSpan(0, total), options);
    }

    private static SafeFileHandle CreateReadHandleNoRecall(string path)
    {
        SafeFileHandle handle = CreateFileW(
            path,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            FileAttributeNormal | FileFlagSequentialScan | FileFlagOpenNoRecall,
            IntPtr.Zero);

        if (!handle.IsInvalid)
            return handle;

        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw error switch
        {
            ErrorSharingViolation => new IOException("file is locked by another process", HResultFromWin32(error)),
            ErrorAccessDenied => new UnauthorizedAccessException("access denied"),
            _ => new IOException($"CreateFile failed with Win32 error {error}", HResultFromWin32(error)),
        };
    }

    private static bool IsSecretOrCachePath(string relativePath)
    {
        string normalized = relativePath.Replace('\\', '/');
        string leaf = Path.GetFileName(normalized);
        if (MigrationSecretFilter.IsSecretLeafName(leaf))
            return true;

        return normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Any(segment => segment.Equals("cache", StringComparison.OrdinalIgnoreCase)
                            || segment.Equals("caches", StringComparison.OrdinalIgnoreCase)
                            || segment.Equals("code cache", StringComparison.OrdinalIgnoreCase)
                            || segment.Equals("gpucache", StringComparison.OrdinalIgnoreCase)
                            || segment.Equals("tmp", StringComparison.OrdinalIgnoreCase)
                            || segment.Equals("temp", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsCloudPlaceholder(FileAttributes attributes)
        => attributes.HasFlag(FileAttributes.Offline)
           || (attributes & RecallOnOpen) != 0
           || (attributes & RecallOnDataAccess) != 0;

    private static IReadOnlyList<string> LoadProfileRoots()
    {
        var roots = new List<string>();
        AddRoot(roots, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

        try
        {
            using RegistryKey? profileList = Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList");
            if (profileList is not null)
            {
                AddRoot(roots, profileList.GetValue("ProfilesDirectory") as string);
                foreach (string sid in profileList.GetSubKeyNames())
                {
                    using RegistryKey? profile = profileList.OpenSubKey(sid);
                    AddRoot(roots, profile?.GetValue("ProfileImagePath") as string);
                }
            }
        }
        catch
        {
            // Registry roots are a detection enhancement; Environment.UserProfile still keeps the probe honest.
        }

        AddRoot(roots, @"C:\Users");
        return roots.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    private static void AddRoot(List<string> roots, string? root)
    {
        if (string.IsNullOrWhiteSpace(root))
            return;

        try
        {
            string expanded = Environment.ExpandEnvironmentVariables(root);
            roots.Add(Path.TrimEndingDirectorySeparator(Path.GetFullPath(expanded)));
        }
        catch
        {
            roots.Add(root.TrimEnd('\\', '/'));
        }
    }

    private static bool IsSharingViolation(IOException ex)
        => (ex.HResult & 0xFFFF) == ErrorSharingViolation;

    private static bool IsAccessDenied(IOException ex)
        => (ex.HResult & 0xFFFF) == ErrorAccessDenied;

    private static int HResultFromWin32(int error)
        => unchecked((int)0x80070000) | error;

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileFlagSequentialScan = 0x08000000;
    private const uint FileFlagOpenNoRecall = 0x00100000;
    private const int ErrorAccessDenied = 5;
    private const int ErrorSharingViolation = 32;
    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string lpFileName,
        uint dwDesiredAccess,
        uint dwShareMode,
        IntPtr lpSecurityAttributes,
        uint dwCreationDisposition,
        uint dwFlagsAndAttributes,
        IntPtr hTemplateFile);
}
