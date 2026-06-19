using System.Text.RegularExpressions;

namespace WindowsCareKit.Core.Logging;

/// <summary>
/// Default redactor. Replaces the profile path and user name with placeholders and masks WiFi
/// <c>key=…</c> values. Constructed with explicit values so it can be unit-tested deterministically;
/// use <see cref="ForCurrentUser"/> in production.
/// </summary>
public sealed partial class LogRedactor : ILogRedactor
{
    private readonly string? _userName;
    private readonly string? _profilePath;

    public LogRedactor(string? userName, string? profilePath)
    {
        _userName = string.IsNullOrWhiteSpace(userName) ? null : userName;
        _profilePath = string.IsNullOrWhiteSpace(profilePath) ? null : profilePath;
    }

    public static LogRedactor ForCurrentUser()
        => new(Environment.UserName, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));

    public string Redact(string? text)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        string s = text;

        // Profile path first (it contains the user name), then any other mention of the name.
        if (_profilePath is not null)
            s = s.Replace(_profilePath, "%USERPROFILE%", StringComparison.OrdinalIgnoreCase);

        if (_userName is not null)
            s = WordBoundary(_userName).Replace(s, "%USER%");

        // Mask secrets: WiFi PSK ("Key Content : …" / "key=…" / Turkish "Anahtar İçeriği : …"), bearer tokens,
        // passwords, API keys, connection strings — the value (to end of line), keeping the label.
        s = SecretRegex().Replace(s, m => m.Groups[1].Value + "[REDACTED]");

        return s;
    }

    private static Regex WordBoundary(string word)
        => new(@"(?<![A-Za-z0-9])" + Regex.Escape(word) + @"(?![A-Za-z0-9])",
               RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    [GeneratedRegex(
        @"((?i)\b(?:key\s*content|key|anahtar\s*içeriği|anahtar|psk|passphrase|password|pwd|token|bearer|secret(?:key)?|access[_-]?key|api[_-]?key|client_secret|authorization|connectionstring)\b\s*[:=]?\s*)\S[^\r\n]*",
        RegexOptions.CultureInvariant)]
    private static partial Regex SecretRegex();
}
