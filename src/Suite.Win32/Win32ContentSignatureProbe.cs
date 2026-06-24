using System.IO;
using WindowsCareKit.Core.Modules.Migration;

namespace WindowsCareKit.Win32;

/// <summary>
/// Production read-only content probe. It reads only the first configured bytes, never follows a path already
/// marked as a reparse point, and returns an inconclusive (therefore machine-bound) signature on any uncertainty.
/// </summary>
public sealed class Win32ContentSignatureProbe : IContentSignatureProbe
{
    public const int DefaultMaxBytes = ContentSignatureClassifier.DefaultMaxBytes;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(2);

    private readonly int _maxBytes;
    private readonly TimeSpan _timeout;

    public Win32ContentSignatureProbe(int maxBytes = DefaultMaxBytes, TimeSpan? timeout = null)
    {
        if (maxBytes <= 0 || maxBytes > DefaultMaxBytes)
            throw new ArgumentOutOfRangeException(nameof(maxBytes), $"maxBytes must be in 1..{DefaultMaxBytes}");

        TimeSpan effectiveTimeout = timeout ?? DefaultTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(timeout));

        _maxBytes = maxBytes;
        _timeout = effectiveTimeout;
    }

    public ContentSignature ProbeFile(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return ContentSignature.Inconclusive();

        try
        {
            FileAttributes attributes = File.GetAttributes(path);
            if (attributes.HasFlag(FileAttributes.Directory))
                return ContentSignature.Inconclusive();
            if (HasReparsePointInPath(path))
                return ContentSignature.Inconclusive();

            return ReadBoundedAsync(path).GetAwaiter().GetResult();
        }
        catch
        {
            return ContentSignature.Inconclusive();
        }
    }

    private static bool HasReparsePointInPath(string path)
    {
        string current = Path.GetFullPath(path);
        while (true)
        {
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                return true;

            string trimmed = Path.TrimEndingDirectorySeparator(current);
            string? parent = Path.GetDirectoryName(trimmed);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.OrdinalIgnoreCase))
                return false;
            current = parent;
        }
    }

    private async Task<ContentSignature> ReadBoundedAsync(string path)
    {
        using var cts = new CancellationTokenSource(_timeout);
        byte[] buffer = new byte[_maxBytes];
        int total = 0;

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            bufferSize: 4096,
            options: FileOptions.Asynchronous | FileOptions.SequentialScan);

        while (total < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(total, buffer.Length - total), cts.Token)
                .ConfigureAwait(false);
            if (read == 0)
                break;
            total += read;
        }

        return ContentSignatureClassifier.Classify(buffer.AsSpan(0, total));
    }
}
