using System.IO;
using System.Reflection;
using System.Text.Json;

namespace WindowsCareKit.App.Localization;

/// <summary>
/// Reads a module's embedded <c>lang.&lt;culture&gt;.json</c> fragment (modular M2b). Modules ship their
/// UI strings baked into their own assembly instead of a loose file beside the exe, so the fragment can
/// never desync from the module DLL (a copied-file scheme could go missing while the DLL stays present).
/// </summary>
public static class LangFragments
{
    /// <summary>
    /// Reads the <paramref name="culture"/> fragment embedded in <paramref name="assembly"/> under the
    /// pinned logical name <c>lang.&lt;culture&gt;.json</c>. Returns an empty map when the assembly embeds
    /// no fragment for that culture (a module with no strings, or a test-fake assembly). A present-but-
    /// malformed resource throws — an embedded resource is a build artifact, so corruption is a build bug
    /// that must fail loud, unlike the forgiving on-disk read in <see cref="I18n"/>.
    /// </summary>
    public static IReadOnlyDictionary<string, string> ReadEmbedded(Assembly assembly, string culture)
    {
        culture = string.IsNullOrWhiteSpace(culture) ? "en" : culture.Trim().ToLowerInvariant();
        string resourceName = "lang." + culture + ".json";

        using Stream? stream = assembly.GetManifestResourceStream(resourceName);
        if (stream is null)
            return new Dictionary<string, string>();

        Dictionary<string, string>? map = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
        return map ?? new Dictionary<string, string>();
    }
}
