using System.IO;
using System.Text.Json;
using WindowsCareKit.App.Mvvm;

namespace WindowsCareKit.App.Localization;

/// <summary>A selectable UI language: a stable <paramref name="Code"/> plus a human <paramref name="DisplayName"/>.</summary>
public sealed record LanguageOption(string Code, string DisplayName);

/// <summary>
/// JSON-backed string table with live language switching. XAML binds the indexer
/// (<c>{Binding I18n[some.key]}</c>); raising <c>Item[]</c> on switch refreshes every bound string.
/// <para>
/// Languages are discovered from the <c>lang/</c> folder, so adding a new one is just dropping a
/// translated <c>&lt;code&gt;.json</c> that carries a <c>meta.languageName</c> entry — no code change.
/// The brand-strip selector binds <see cref="AvailableLanguages"/> and <see cref="SelectedCulture"/>.
/// </para>
/// </summary>
public sealed class I18n : ObservableObject
{
    /// <summary>Flat key whose value is the language's own display name (e.g. "English", "Türkçe").</summary>
    private const string DisplayNameKey = "meta.languageName";

    private readonly string _langDir;
    private Dictionary<string, string> _map = new();
    private string _culture = "en";

    public I18n() : this(DefaultLangDir)
    {
    }

    internal I18n(string langDir)
    {
        _langDir = langDir;
        AvailableLanguages = EnumerateLanguages(_langDir);
    }

    /// <summary>Languages found under <c>lang/</c>; English first, then alphabetical by display name.</summary>
    public IReadOnlyList<LanguageOption> AvailableLanguages { get; }

    public string Culture
    {
        get => _culture;
        private set => SetField(ref _culture, value);
    }

    /// <summary>
    /// Two-way bindable selected language code. Setting it to a known, different code switches the
    /// live string table; the brand-strip <c>ComboBox</c> binds here (SelectedValuePath="Code").
    /// </summary>
    public string SelectedCulture
    {
        get => _culture;
        set
        {
            if (!string.IsNullOrWhiteSpace(value) &&
                !value.Equals(_culture, StringComparison.OrdinalIgnoreCase))
            {
                Load(value);
            }
        }
    }

    /// <summary>Look up a key; returns the key itself when missing so gaps are visible, not blank.</summary>
    public string this[string key]
        => _map.TryGetValue(key, out string? value) ? value : key;

    public string Format(string key, params object[] args)
        => string.Format(this[key], args);

    public void Load(string culture)
    {
        culture = string.IsNullOrWhiteSpace(culture) ? "en" : culture.Trim().ToLowerInvariant();
        Dictionary<string, string> merged = ReadMap(Path.Combine(_langDir, "en.json"));

        if (culture != "en")
        {
            string overlayPath = Path.Combine(_langDir, culture + ".json");
            if (File.Exists(overlayPath))
            {
                foreach ((string key, string value) in ReadMap(overlayPath))
                    merged[key] = value;
            }
            else
            {
                culture = "en";
            }
        }

        // meta.* entries (e.g. the language's own display name) describe the file, not the UI —
        // keep the live string table to bindable UI strings only.
        foreach (string metaKey in merged.Keys.Where(k => k.StartsWith("meta.", StringComparison.Ordinal)).ToList())
            merged.Remove(metaKey);

        _map = merged;

        Culture = culture;
        Raise(nameof(SelectedCulture)); // keep the selector in sync after programmatic loads
        Raise("Item[]");                // refresh every indexer binding
    }

    private static string DefaultLangDir => Path.Combine(AppContext.BaseDirectory, "lang");

    private static Dictionary<string, string> ReadMap(string path)
    {
        try
        {
            string json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch (Exception)
        {
            return new();
        }
    }

    /// <summary>
    /// Discovers the languages available in <paramref name="langDir"/>. Each <c>*.json</c> file is one
    /// language; its display name comes from the <c>meta.languageName</c> entry, falling back to the
    /// file's culture code when that entry is missing or the file is unreadable. English sorts first,
    /// then by display name. Always returns at least one entry so the selector is never empty.
    /// </summary>
    internal static IReadOnlyList<LanguageOption> EnumerateLanguages(string langDir)
    {
        var found = new List<LanguageOption>();
        if (Directory.Exists(langDir))
        {
            foreach (string file in Directory.EnumerateFiles(langDir, "*.json"))
            {
                string code = Path.GetFileNameWithoutExtension(file);
                found.Add(new LanguageOption(code, ReadDisplayName(file) ?? code));
            }
        }

        if (found.Count == 0)
            found.Add(new LanguageOption("en", "English"));

        return found
            .OrderByDescending(l => l.Code.Equals("en", StringComparison.OrdinalIgnoreCase))
            .ThenBy(l => l.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string? ReadDisplayName(string file)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(file));
            if (doc.RootElement.TryGetProperty(DisplayNameKey, out JsonElement name) &&
                name.ValueKind == JsonValueKind.String)
            {
                string? text = name.GetString();
                return string.IsNullOrWhiteSpace(text) ? null : text;
            }
        }
        catch (Exception)
        {
            // Unreadable/malformed file → caller falls back to the bare code.
        }

        return null;
    }
}
