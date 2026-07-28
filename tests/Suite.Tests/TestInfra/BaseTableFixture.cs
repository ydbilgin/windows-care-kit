using System.Text.Json;
using WindowsCareKit.App.Localization;

namespace WindowsCareKit.Tests.TestInfra;

/// <summary>
/// Base string tables for tests that need a <em>usable</em> one — i.e. one that satisfies the production
/// floor in <see cref="BaseStringTable.RequiredKeys"/>, not merely a one-key stand-in.
/// <para>
/// Before the floor existed, a fixture could write <c>{ "app.title": "…" }</c> and call it a shipped table.
/// That is exactly the assumption that let <c>{}</c> through: a fixture weaker than the rule proves nothing
/// about the rule. Building fixtures from <see cref="BaseStringTable.RequiredKeys"/> keeps them honest as
/// the floor grows, and the deliberate-defect builders below make the defect the only difference.
/// </para>
/// </summary>
internal static class BaseTableFixture
{
    /// <summary>The one key tests assert a real value for; every other entry gets a visibly synthetic one.</summary>
    public const string AppTitleKey = "app.title";

    public const string AppTitle = "Windows Care Kit";

    /// <summary>Filler that can never be mistaken for a real string or for a raw-key fall-through.</summary>
    private static string Filler(string key) => "[" + key + "]";

    /// <summary>A complete, usable base table, with <paramref name="overrides"/> applied on top.</summary>
    public static string Json(params (string Key, string Value)[] overrides)
        => Serialize(Entries(overrides));

    /// <summary>A complete table titled the way most tests want to read it back.</summary>
    public static string JsonTitled(string title = AppTitle) => Json((AppTitleKey, title));

    /// <summary>A table that is complete except for one required key, which is absent.</summary>
    public static string JsonWithout(string requiredKey)
    {
        Dictionary<string, string> entries = Entries([]);
        entries.Remove(RequireIsRequired(requiredKey));
        return Serialize(entries);
    }

    /// <summary>A table that is complete except for one required key, which is present but blank.</summary>
    public static string JsonWithBlank(string requiredKey)
        => Json((RequireIsRequired(requiredKey), "   "));

    private static Dictionary<string, string> Entries((string Key, string Value)[] overrides)
    {
        var entries = new Dictionary<string, string>(StringComparer.Ordinal);
        entries["meta.languageName"] = "English"; // the shipped table carries it; I18n strips it before binding
        foreach (string key in BaseStringTable.RequiredKeys)
            entries[key] = Filler(key);

        foreach ((string key, string value) in overrides)
            entries[key] = value;

        return entries;
    }

    /// <summary>A "deliberately broken" fixture keyed on a non-required key would break nothing — and a test
    /// built on it would pass for the wrong reason. Refuse rather than fabricate the defect.</summary>
    private static string RequireIsRequired(string key)
        => BaseStringTable.RequiredKeys.Contains(key)
            ? key
            : throw new ArgumentException(
                $"'{key}' is not in BaseStringTable.RequiredKeys, so removing or blanking it proves nothing.",
                nameof(key));

    private static string Serialize(Dictionary<string, string> entries)
        => JsonSerializer.Serialize(entries, new JsonSerializerOptions { WriteIndented = true });
}
