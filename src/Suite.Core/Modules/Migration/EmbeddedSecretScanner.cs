using System.Text;
using System.Text.RegularExpressions;

namespace WindowsCareKit.Core.Modules.Migration;

/// <summary>
/// Detects embedded credentials in otherwise innocuous text files before Backup copies their bytes.
/// The copy engine supplies only a bounded prefix; this detector is pure and has no file-system dependency.
/// </summary>
public static class EmbeddedSecretScanner
{
    /// <summary>Maximum source bytes the copy engine should scan before copying a file.</summary>
    public const int MaxBytesToScan = 512 * 1024;

    private static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".7z", ".avi", ".bin", ".bmp", ".db", ".dll", ".dmg", ".exe", ".gif", ".gz", ".ico",
        ".iso", ".jpeg", ".jpg", ".mov", ".mp3", ".mp4", ".pdf", ".png", ".pdb", ".rar",
        ".sqlite", ".sqlite3", ".sqlite-shm", ".sqlite-wal", ".tar", ".wav", ".webp", ".zip",
    };

    private static readonly Regex PrefixTokenRegex = new(
        @"(?ix)
        (?:
            sk-[A-Za-z0-9_-]{16,}
          | gh[pous]_[A-Za-z0-9_]{16,}
          | ghs_[A-Za-z0-9_]{16,}
          | github_pat_[A-Za-z0-9_]{16,}
          | xox[baprs]-[A-Za-z0-9-]{10,}
          | ya29\.[A-Za-z0-9_-]{10,}
          | AIza[A-Za-z0-9_-]{10,}
          | 1//0[A-Za-z0-9_-]{10,}
          | AKIA[0-9A-Z]{12,}
          | ASIA[0-9A-Z]{12,}
          | glpat-[A-Za-z0-9_-]{10,}
        )",
        RegexOptions.CultureInvariant);

    private static readonly Regex PrivateKeyRegex = new(
        @"-----BEGIN [A-Z ]*PRIVATE KEY-----",
        RegexOptions.CultureInvariant);

    private static readonly Regex KeyValueSecretRegex = new(
        @"(?ix)
        [""']?
        [A-Za-z0-9_.-]*
        (?:api[_-]?key|client[_-]?secret|refresh[_-]?token|access[_-]?token|secret|token|password|bearer)
        [A-Za-z0-9_.-]*
        [""']?
        \s*[:=]\s*
        (?:
            ""(?<dq>[^""\r\n]*)""
          | '(?<sq>[^'\r\n]*)'
        )",
        RegexOptions.CultureInvariant);

    private static readonly Regex EntropyCandidateRegex = new(
        @"(?<![A-Za-z0-9+/=_-])[A-Za-z0-9+/=_-]{40,}(?![A-Za-z0-9+/=_-])",
        RegexOptions.CultureInvariant);

    /// <summary>Scan a bounded file prefix and report whether it contains an embedded secret.</summary>
    public static EmbeddedSecretScanResult Scan(ReadOnlySpan<byte> prefix, string sourcePath)
    {
        if (IsBinaryByExtension(sourcePath) || ContainsNul(prefix))
            return EmbeddedSecretScanResult.Clean;

        string text;
        try
        {
            text = Encoding.UTF8.GetString(prefix);
        }
        catch (DecoderFallbackException)
        {
            return EmbeddedSecretScanResult.Clean;
        }

        if (PrefixTokenRegex.IsMatch(text))
            return new EmbeddedSecretScanResult(true, "credential token prefix");

        if (PrivateKeyRegex.IsMatch(text))
            return new EmbeddedSecretScanResult(true, "private key block");

        foreach (Match match in KeyValueSecretRegex.Matches(text))
        {
            string value = match.Groups["dq"].Success ? match.Groups["dq"].Value : match.Groups["sq"].Value;
            if (!IsObviousPlaceholder(value))
                return new EmbeddedSecretScanResult(true, "secret-like key/value");
        }

        foreach (Match match in EntropyCandidateRegex.Matches(text))
        {
            string value = match.Value.TrimEnd('=');
            double entropy = ShannonEntropy(value);
            bool hex = IsHex(value);
            if ((hex && entropy >= 3.5) || (!hex && entropy > 4.0))
                return new EmbeddedSecretScanResult(true, "high-entropy token-like blob");
        }

        return EmbeddedSecretScanResult.Clean;
    }

    /// <summary>True when the leaf extension is an obvious binary format that should not be content-scanned.</summary>
    public static bool HasObviousBinaryExtension(string sourcePath)
        => IsBinaryByExtension(sourcePath);

    private static bool IsBinaryByExtension(string sourcePath)
    {
        string ext = Path.GetExtension(sourcePath);
        return !string.IsNullOrEmpty(ext) && BinaryExtensions.Contains(ext);
    }

    private static bool ContainsNul(ReadOnlySpan<byte> bytes)
    {
        foreach (byte b in bytes)
            if (b == 0)
                return true;
        return false;
    }

    private static bool IsObviousPlaceholder(string value)
    {
        string trimmed = value.Trim();
        if (trimmed.Length == 0)
            return true;

        string lower = trimmed.ToLowerInvariant();
        if (lower is "null" or "changeme" or "change-me" or "replace-me" or "placeholder" or "todo")
            return true;
        if (trimmed.StartsWith('<') && trimmed.EndsWith('>'))
            return true;
        if (lower.StartsWith("your-", StringComparison.Ordinal) || lower.StartsWith("your_", StringComparison.Ordinal))
            return true;
        if (lower.Contains("example", StringComparison.Ordinal))
            return true;
        return lower.All(c => c == 'x' || c == '*');
    }

    private static bool IsHex(string value)
        => value.All(c => char.IsDigit(c) || c is >= 'a' and <= 'f' || c is >= 'A' and <= 'F');

    private static double ShannonEntropy(string value)
    {
        if (value.Length == 0)
            return 0;

        var counts = new Dictionary<char, int>();
        foreach (char c in value)
            counts[c] = counts.TryGetValue(c, out int count) ? count + 1 : 1;

        double entropy = 0;
        foreach (int count in counts.Values)
        {
            double p = (double)count / value.Length;
            entropy -= p * Math.Log(p, 2);
        }

        return entropy;
    }
}

/// <summary>The result of scanning one bounded file prefix for embedded credentials.</summary>
/// <param name="ContainsSecret">True when the prefix contains an embedded token/credential signal.</param>
/// <param name="Reason">Stable human-readable reason surfaced in skip reports.</param>
public sealed record EmbeddedSecretScanResult(bool ContainsSecret, string Reason)
{
    /// <summary>No embedded secret was detected.</summary>
    public static EmbeddedSecretScanResult Clean { get; } = new(false, string.Empty);
}
