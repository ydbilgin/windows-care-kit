using System.Globalization;
using System.Text;

namespace WindowsCareKit.App.Modules;

/// <summary>
/// Makes a component directory name safe to render. A directory under <c>Modules\</c> is user-writable, so its
/// name is untrusted text: bidi overrides can visually reorder a sentence, control characters can break the
/// line, and an over-long name can push real UI off screen. Writing there already requires install-directory
/// write access (the ratified M4 trust policy, <see cref="DirectoryModuleCatalog"/>), so this is defence in
/// depth against UI spoofing, not a privilege boundary.
/// </summary>
internal static class ModuleDirectoryLabel
{
    /// <summary>Longest name rendered verbatim. Longer names are truncated with an ellipsis.</summary>
    internal const int MaxLength = 64;

    /// <summary>Stands in for a character that must not reach the UI, and for a name that sanitizes to nothing.</summary>
    internal const char Replacement = '\uFFFD';

    /// <summary>
    /// Replaces every Unicode control, format (this is what covers the bidi overrides U+202A-202E and the
    /// isolates U+2066-2069), line-separator and paragraph-separator character with
    /// <see cref="Replacement"/>, then truncates to <see cref="MaxLength"/>. Never returns null, empty, or
    /// whitespace-only.
    /// </summary>
    internal static string ForDisplay(string directoryName)
    {
        if (string.IsNullOrWhiteSpace(directoryName))
            return Replacement.ToString();

        var sanitized = new StringBuilder(directoryName.Length);
        foreach (char character in directoryName)
        {
            UnicodeCategory category = char.GetUnicodeCategory(character);
            sanitized.Append(category is UnicodeCategory.Control
                or UnicodeCategory.Format
                or UnicodeCategory.LineSeparator
                or UnicodeCategory.ParagraphSeparator
                    ? Replacement
                    : character);
        }

        if (sanitized.Length <= MaxLength)
            return sanitized.ToString();

        return sanitized.ToString(0, MaxLength - 1) + '\u2026';
    }
}
