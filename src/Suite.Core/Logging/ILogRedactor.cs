namespace WindowsCareKit.Core.Logging;

/// <summary>
/// Scrubs sensitive substrings (the user name, the profile path, WiFi <c>key=…</c> plaintext) out of
/// log text before it is written, so a shared diagnostic log doesn't leak personal data (spec §3).
/// </summary>
public interface ILogRedactor
{
    string Redact(string? text);
}
