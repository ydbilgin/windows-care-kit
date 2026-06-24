using System.Text;
using System.Text.RegularExpressions;

namespace WindowsCareKit.Core.Modules.Migration;

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
    public bool HasCredentialStoreHeader { get; init; }
    public bool IsInconclusive { get; init; }
    public int BytesInspected { get; init; }

    public bool HasMachineBoundContent =>
        HasDpapiBlob
        || HasSidBinding
        || HasMachineGuidBinding
        || HasAbsolutePathBinding
        || HasCredentialStoreHeader
        || IsInconclusive;

    public static ContentSignature Inconclusive(int bytesInspected = 0) =>
        new() { IsInconclusive = true, BytesInspected = Math.Max(0, bytesInspected) };
}

/// <summary>
/// Core contract for the production read-only file probe. Implementations must impose a bounded read
/// footprint and fail closed when a file cannot be classified safely.
/// </summary>
public interface IContentSignatureProbe
{
    ContentSignature ProbeFile(string path);
}

/// <summary>
/// IO-free signature recognition over caller-supplied bytes/streams. Test fixtures are synthetic byte
/// sequences; no real DPAPI payload, credential store, SID, or user file is required.
/// </summary>
public static partial class ContentSignatureClassifier
{
    public const int DefaultMaxBytes = 64 * 1024;

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
        return Classify(bytes.AsSpan());
    }

    public static ContentSignature Classify(ReadOnlySpan<byte> bytes)
    {
        bool dpapi = Contains(bytes, DpapiProviderHeader);
        bool credentialStore =
            bytes.StartsWith(SqliteHeader)
            || Contains(bytes, LevelDbTableMagic)
            || StartsWithAsciiIgnoreCase(bytes, "MANIFEST-");

        string utf8 = Encoding.UTF8.GetString(bytes);
        string utf16 = bytes.Length >= 2
            ? Encoding.Unicode.GetString(bytes[..(bytes.Length - (bytes.Length % 2))])
            : string.Empty;

        bool sid = SidRegex().IsMatch(utf8) || SidRegex().IsMatch(utf16);
        bool machineGuid = MachineGuidRegex().IsMatch(utf8) || MachineGuidRegex().IsMatch(utf16);
        bool absolutePath =
            CountAbsoluteProfilePaths(utf8) >= 3
            || CountAbsoluteProfilePaths(utf16) >= 3;

        return new ContentSignature
        {
            HasDpapiBlob = dpapi,
            HasSidBinding = sid,
            HasMachineGuidBinding = machineGuid,
            HasAbsolutePathBinding = absolutePath,
            HasCredentialStoreHeader = credentialStore,
            BytesInspected = bytes.Length,
        };
    }

    public static ContentSignature Classify(Stream stream, int maxBytes = DefaultMaxBytes)
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

        return Classify(buffer.AsSpan(0, total));
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

    private static int CountAbsoluteProfilePaths(string text)
    {
        int count = 0;
        foreach (Match _ in AbsoluteProfilePathRegex().Matches(text))
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

    [GeneratedRegex(@"(?i:[a-z]:\\users\\[^\\\s""']+\\)", RegexOptions.CultureInvariant)]
    private static partial Regex AbsoluteProfilePathRegex();
}
