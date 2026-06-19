using System.IO;
using System.Text.Json;
using WindowsCareKit.App.Mvvm;

namespace WindowsCareKit.App.Localization;

/// <summary>
/// JSON-backed string table with live language switching. XAML binds the indexer
/// (<c>{Binding I18n[some.key]}</c>); raising <c>Item[]</c> on switch refreshes every bound string.
/// </summary>
public sealed class I18n : ObservableObject
{
    private Dictionary<string, string> _map = new();
    private string _culture = "en";

    public string Culture
    {
        get => _culture;
        private set => SetField(ref _culture, value);
    }

    /// <summary>Look up a key; returns the key itself when missing so gaps are visible, not blank.</summary>
    public string this[string key]
        => _map.TryGetValue(key, out string? value) ? value : key;

    public string Format(string key, params object[] args)
        => string.Format(this[key], args);

    public void Load(string culture)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "lang");
        string path = Path.Combine(dir, culture + ".json");
        if (!File.Exists(path))
        {
            culture = "en";
            path = Path.Combine(dir, "en.json");
        }

        try
        {
            string json = File.ReadAllText(path);
            _map = JsonSerializer.Deserialize<Dictionary<string, string>>(json) ?? new();
        }
        catch (Exception)
        {
            _map = new();
        }

        Culture = culture;
        Raise("Item[]"); // refresh every indexer binding
    }

    public void Toggle() => Load(Culture == "en" ? "tr" : "en");
}
