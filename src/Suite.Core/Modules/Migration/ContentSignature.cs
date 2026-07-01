using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace WindowsCareKit.Core.Modules.Migration;

public enum ContentProbeStatus
{
    Complete,
    Inconclusive,
    Inaccessible,
    LockedNow,
    CloudPlaceholder,
    ProbeTimedOut,
}

public sealed record ContentSignatureOptions(
    IReadOnlyList<string> ProfileRoots,
    string? ExpectedFormat = null)
{
    public static ContentSignatureOptions Default { get; } = new(Array.Empty<string>());
}

/// <summary>
/// Read-only content classification for one file. An inconclusive probe is deliberately machine-bound:
/// the honesty floor prefers a false warning over a false-green portability claim.
/// </summary>
public sealed record ContentSignature
{
    public bool HasDpapiBlob { get; init; }
    public bool HasSidBinding { get; init; }
    public bool HasMachineGuidBinding { get; init; }
    public bool HasAbsolutePathBinding { get; init; }
    public bool HasSqliteHeader { get; init; }
    public bool HasUnexpectedSqliteHeader { get; init; }
    public bool HasCredentialStoreHeader { get; init; }
    public bool IsInconclusive { get; init; }
    public ContentProbeStatus Status { get; init; } = ContentProbeStatus.Complete;
    public int BytesInspected { get; init; }
    public bool IsDirectorySignature { get; init; }
    public int DirectoryFilesSampled { get; init; }
    public int DirectoryFilesTotalSeen { get; init; }
    public bool DirectoryEnumerationTruncated { get; init; }
    public IReadOnlyList<string> DirectorySampledFiles { get; init; } = Array.Empty<string>();

    public bool HasMachineBoundContent =>
        HasDpapiBlob
        || HasSidBinding
        || HasMachineGuidBinding
        || HasAbsolutePathBinding
        || HasCredentialStoreHeader
        || IsInconclusive;

    public bool BlocksPortabilityClaim =>
        HasMachineBoundContent
        || HasUnexpectedSqliteHeader
        || DirectoryEnumerationTruncated
        || Status is ContentProbeStatus.Inaccessible
            or ContentProbeStatus.LockedNow
            or ContentProbeStatus.CloudPlaceholder
            or ContentProbeStatus.ProbeTimedOut;

    public static ContentSignature Inconclusive(int bytesInspected = 0) =>
        new()
        {
            IsInconclusive = true,
            Status = ContentProbeStatus.Inconclusive,
            BytesInspected = Math.Max(0, bytesInspected),
        };

    public static ContentSignature Inaccessible(int bytesInspected = 0) =>
        new()
        {
            Status = ContentProbeStatus.Inaccessible,
            BytesInspected = Math.Max(0, bytesInspected),
        };

    public static ContentSignature LockedNow(int bytesInspected = 0) =>
        new()
        {
            Status = ContentProbeStatus.LockedNow,
            BytesInspected = Math.Max(0, bytesInspected),
        };

    public static ContentSignature CloudPlaceholder() =>
        new() { Status = ContentProbeStatus.CloudPlaceholder };

    public static ContentSignature ProbeTimedOut(int bytesInspected = 0) =>
        new()
        {
            Status = ContentProbeStatus.ProbeTimedOut,
            BytesInspected = Math.Max(0, bytesInspected),
        };
}

/// <summary>
/// Core contract for the production read-only file probe. Implementations must impose a bounded read
/// footprint and fail closed when a file cannot be classified safely.
/// </summary>
public interface IContentSignatureProbe
{
    ContentSignature ProbeFile(string path, ContentSignatureOptions? options = null);
}

/// <summary>
/// IO-free signature recognition over caller-supplied bytes/streams. Test fixtures are synthetic byte
/// sequences; no real DPAPI payload, credential store, SID, or user file is required.
/// </summary>
public static partial class ContentSignatureClassifier
{
    public const int DefaultMaxBytes = 64 * 1024;
    private static readonly ConcurrentDictionary<(string Root, bool JsonEscapedBackslashes), Regex> ProfileRootRegexCache = new();

    private static ReadOnlySpan<byte> DpapiProviderHeader =>
    [
        0x01, 0x00, 0x00, 0x00,
        0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15, 0xD1, 0x11,
        0x8C, 0x7A, 0x00, 0xC0, 0x4F, 0xC2, 0x97, 0xEB,
    ];

    private static ReadOnlySpan<byte> SqliteHeader => "SQLite format 3\0"u8;

    // LevelDB table magic 0xdb4775248b80fb57, encoded little-endian in an SSTable footer.
    private static ReadOnlySpan<byte> LevelDbTableMagic => [0x57, 0xFB, 0x80, 0x8B, 0x24, 0x75, 0x47, 0xDB];

    public static ContentSignature Classify(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);
        return Classify(bytes.AsSpan(), ContentSignatureOptions.Default);
    }

    public static ContentSignature Classify(ReadOnlySpan<byte> bytes)
        => Classify(bytes, ContentSignatureOptions.Default);

    public static ContentSignature Classify(ReadOnlySpan<byte> bytes, ContentSignatureOptions? options)
    {
        options ??= ContentSignatureOptions.Default;
        bool dpapi = Contains(bytes, DpapiProviderHeader);
        bool sqliteHeader = bytes.StartsWith(SqliteHeader);
        bool credentialStore = Contains(bytes, LevelDbTableMagic)
                               || StartsWithAsciiIgnoreCase(bytes, "MANIFEST-");

        string utf8 = Encoding.UTF8.GetString(bytes);
        string utf16 = bytes.Length >= 2
            ? Encoding.Unicode.GetString(bytes[..(bytes.Length - (bytes.Length % 2))])
            : string.Empty;

        bool sid = SidRegex().IsMatch(utf8) || SidRegex().IsMatch(utf16);
        bool machineGuid = MachineGuidRegex().IsMatch(utf8) || MachineGuidRegex().IsMatch(utf16);
        bool profileRootProbeTimedOut = false;
        bool absolutePath = false;
        try
        {
            absolutePath =
                ContainsThisMachineProfilePath(utf8, options.ProfileRoots)
                || ContainsThisMachineProfilePath(utf16, options.ProfileRoots)
                || CountGenericAbsoluteProfilePaths(utf8) >= 3
                || CountGenericAbsoluteProfilePaths(utf16) >= 3;
        }
        catch (RegexMatchTimeoutException)
        {
            profileRootProbeTimedOut = true;
        }

        return new ContentSignature
        {
            HasDpapiBlob = dpapi,
            HasSidBinding = sid,
            HasMachineGuidBinding = machineGuid,
            HasAbsolutePathBinding = absolutePath,
            HasSqliteHeader = sqliteHeader,
            HasUnexpectedSqliteHeader = sqliteHeader
                                      && !string.Equals(options.ExpectedFormat, "sqlite", StringComparison.OrdinalIgnoreCase),
            HasCredentialStoreHeader = credentialStore,
            Status = profileRootProbeTimedOut ? ContentProbeStatus.ProbeTimedOut : ContentProbeStatus.Complete,
            BytesInspected = bytes.Length,
        };
    }

    public static ContentSignature Classify(Stream stream, int maxBytes = DefaultMaxBytes)
        => Classify(stream, maxBytes, ContentSignatureOptions.Default);

    public static ContentSignature Classify(
        Stream stream,
        int maxBytes,
        ContentSignatureOptions? options)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanRead)
            throw new ArgumentException("stream must be readable", nameof(stream));
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        byte[] buffer = new byte[maxBytes];
        int total = 0;
        while (total < buffer.Length)
        {
            int read = stream.Read(buffer, total, buffer.Length - total);
            if (read == 0)
                break;
            total += read;
        }

        return Classify(buffer.AsSpan(0, total), options);
    }

    private static bool Contains(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needle)
        => needle.Length > 0 && haystack.IndexOf(needle) >= 0;

    private static bool StartsWithAsciiIgnoreCase(ReadOnlySpan<byte> bytes, string expected)
    {
        if (bytes.Length < expected.Length)
            return false;
        for (int i = 0; i < expected.Length; i++)
        {
            byte actual = bytes[i];
            char wanted = expected[i];
            if (actual > 0x7F || char.ToUpperInvariant((char)actual) != char.ToUpperInvariant(wanted))
                return false;
        }
        return true;
    }

    public static ContentSignature MergeDirectory(
        IEnumerable<(string RelativePath, ContentSignature Signature)> samples,
        int filesTotalSeen,
        bool truncated = false)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var sampleList = samples.ToArray();
        return new ContentSignature
        {
            HasDpapiBlob = sampleList.Any(s => s.Signature.HasDpapiBlob),
            HasSidBinding = sampleList.Any(s => s.Signature.HasSidBinding),
            HasMachineGuidBinding = sampleList.Any(s => s.Signature.HasMachineGuidBinding),
            HasAbsolutePathBinding = sampleList.Any(s => s.Signature.HasAbsolutePathBinding),
            HasSqliteHeader = sampleList.Any(s => s.Signature.HasSqliteHeader),
            HasUnexpectedSqliteHeader = sampleList.Any(s => s.Signature.HasUnexpectedSqliteHeader),
            HasCredentialStoreHeader = sampleList.Any(s => s.Signature.HasCredentialStoreHeader),
            IsInconclusive = sampleList.Any(s => s.Signature.IsInconclusive),
            Status = sampleList.Any(s => s.Signature.Status != ContentProbeStatus.Complete)
                ? sampleList.First(s => s.Signature.Status != ContentProbeStatus.Complete).Signature.Status
                : ContentProbeStatus.Complete,
            BytesInspected = sampleList.Sum(s => s.Signature.BytesInspected),
            IsDirectorySignature = true,
            DirectoryFilesSampled = sampleList.Length,
            DirectoryFilesTotalSeen = Math.Max(0, filesTotalSeen),
            DirectoryEnumerationTruncated = truncated,
            DirectorySampledFiles = sampleList.Select(s => s.RelativePath).ToArray(),
        };
    }

    private static bool ContainsThisMachineProfilePath(string text, IReadOnlyList<string> profileRoots)
    {
        if (string.IsNullOrEmpty(text))
            return false;

        foreach (string root in ProfileRootCandidates(profileRoots))
        {
            if (ForceProfileRootRegexTimeoutForTests)
                throw new RegexMatchTimeoutException(text, root, TimeSpan.FromMilliseconds(250));

            if (ProfileRootRegex(root, jsonEscapedBackslashes: false).IsMatch(text)
                || ProfileRootRegex(root, jsonEscapedBackslashes: true).IsMatch(text))
                return true;
        }

        return false;
    }

    private static IEnumerable<string> ProfileRootCandidates(IReadOnlyList<string> profileRoots)
    {
        foreach (string root in profileRoots)
            if (!string.IsNullOrWhiteSpace(root))
                yield return root;

        string currentProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrWhiteSpace(currentProfile))
            yield return currentProfile;

        yield return @"C:\Users";
    }

    private static Regex ProfileRootRegex(string root, bool jsonEscapedBackslashes)
        => ProfileRootRegexCache.GetOrAdd((root, jsonEscapedBackslashes), static key => BuildProfileRootRegex(key.Root, key.JsonEscapedBackslashes));

    private static Regex BuildProfileRootRegex(string root, bool jsonEscapedBackslashes)
    {
        string normalized = root.Replace('\\', '/').TrimEnd('/');
        string[] segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0)
            return NeverMatchRegex();

        string separator = jsonEscapedBackslashes ? @"(?:\\\\|/)" : @"[\\/]";
        string prefix = string.Join(separator, segments.Select(Regex.Escape));
        string pattern = $@"(?i:{prefix}(?:{separator}|{separator}[^\\/""']+{separator}))";
        return new Regex(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled, TimeSpan.FromMilliseconds(250));
    }

    private static int CountGenericAbsoluteProfilePaths(string text)
    {
        int count = 0;
        foreach (Match _ in GenericAbsoluteProfilePathRegex().Matches(text))
        {
            count++;
            if (count >= 3)
                return count;
        }
        return count;
    }

    [GeneratedRegex(@"S-1-5-21-(?:\d+-){2,}\d+", RegexOptions.CultureInvariant)]
    private static partial Regex SidRegex();

    [GeneratedRegex(
        @"(?i:machine[\s_-]*guid)\s*[:=]?\s*[""']?\{?[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}\}?",
        RegexOptions.CultureInvariant)]
    private static partial Regex MachineGuidRegex();

    [GeneratedRegex(@"(?!)", RegexOptions.CultureInvariant)]
    private static partial Regex NeverMatchRegex();

    [GeneratedRegex(@"(?i:[a-z]:(?:\\|/)users(?:\\|/)[^\\/""']+(?:\\|/))", RegexOptions.CultureInvariant)]
    private static partial Regex GenericAbsoluteProfilePathRegex();

    internal static int ProfileRootRegexCacheCountForTests => ProfileRootRegexCache.Count;

    internal static void ResetProfileRootRegexCacheForTests() => ProfileRootRegexCache.Clear();

    internal static bool ForceProfileRootRegexTimeoutForTests { get; set; }
}
