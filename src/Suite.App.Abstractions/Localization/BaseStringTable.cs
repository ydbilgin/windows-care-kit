using System.IO;
using System.Text.Json;

namespace WindowsCareKit.App.Localization;

/// <summary>How an attempt to obtain the mandatory English base string table ended.</summary>
public enum BaseStringTableStatus
{
    /// <summary>The file was read and is exactly the required shape; entries are available.</summary>
    Loaded,

    /// <summary>The file is not there.</summary>
    Missing,

    /// <summary>The file is there but could not be read, is not a flat JSON object of string values, or does
    /// not carry a usable value for every key the shell must render (<see cref="BaseStringTable.RequiredKeys"/>).
    /// "Present but broken" is a different statement from "absent" (P20), and neither of them is
    /// "an empty table" — that fabrication is the defect this whole type exists to prevent.</summary>
    Unreadable,
}

/// <summary>
/// The outcome of one <see cref="BaseStringTable.Load(string)"/>: a status, the file it is about, and either
/// the entries or the preserved reason there are none.
/// </summary>
public sealed class BaseStringTableResult
{
    private readonly IReadOnlyDictionary<string, string>? _entries;

    private BaseStringTableResult(
        BaseStringTableStatus status,
        string filePath,
        IReadOnlyDictionary<string, string>? entries,
        string? failureDetail)
    {
        Status = status;
        FilePath = filePath;
        _entries = entries;
        FailureDetail = failureDetail;
    }

    public BaseStringTableStatus Status { get; }

    /// <summary>The file this result is about — so a failure can name it without the caller re-deriving it.</summary>
    public string FilePath { get; }

    public bool IsLoaded => Status == BaseStringTableStatus.Loaded;

    /// <summary>Why there are no entries. Non-null whenever <see cref="IsLoaded"/> is false, null otherwise.</summary>
    public string? FailureDetail { get; }

    /// <summary>
    /// The table's entries. Reading this when the load did not succeed throws rather than yielding an empty
    /// dictionary: an unavailable table must be impossible to consume as an empty one, because that is exactly
    /// how a broken install turned into a UI whose every label was a raw i18n key.
    /// </summary>
    /// <exception cref="InvalidOperationException">The load did not succeed.</exception>
    public IReadOnlyDictionary<string, string> Entries => _entries ?? throw new InvalidOperationException(
        $"The base string table '{FilePath}' was not loaded ({Status}), so it has no entries. {FailureDetail}");

    internal static BaseStringTableResult Loaded(string filePath, IReadOnlyDictionary<string, string> entries) =>
        new(BaseStringTableStatus.Loaded, filePath, entries, null);

    internal static BaseStringTableResult Missing(string filePath, string detail) =>
        new(BaseStringTableStatus.Missing, filePath, null, detail);

    internal static BaseStringTableResult Unreadable(string filePath, string detail) =>
        new(BaseStringTableStatus.Unreadable, filePath, null, detail);
}

/// <summary>
/// The single authoritative owner (P22) of the mandatory English base string table: what it is called, where
/// it sits relative to the app root, and — the load-bearing part — what counts as a usable one.
/// <para>
/// <b>Why one loader.</b> The shell verifies this file before startup and <see cref="I18n"/> reads it on every
/// language load. When those two applied their own rules they disagreed: a startup check that only proved
/// "parses as JSON with an object root" passed a file whose values were not strings, and the loader — which
/// really needs <c>Dictionary&lt;string,string&gt;</c> — then swallowed the deserialization failure into an
/// empty table and rendered every label as its raw key. Both sides now call <see cref="Load(string)"/>, so
/// there is exactly one definition of "usable" and it cannot drift again.
/// </para>
/// <para>
/// <b>Why a required-key floor.</b> "Parses into <c>Dictionary&lt;string,string&gt;</c>" is still not
/// "usable": <c>{}</c> satisfies it and produces the same all-raw-key UI, only with no error anywhere to
/// explain it. Nor is a non-empty count enough — a metadata-only table is stripped back to empty by
/// <see cref="I18n"/>, and one surviving entry still leaves the rest of the shell rendering its own keys.
/// The rule is therefore <see cref="RequiredKeys"/>: every key the shell's own table owns must be present
/// and non-blank. The <c>tr.json</c>-style overlays are deliberately NOT held to it — they have English
/// underneath to fall back to, and this table is what they fall back to.
/// </para>
/// </summary>
public static class BaseStringTable
{
    /// <summary>The folder the shipped string tables live in, relative to the app root.</summary>
    public const string LangFolderName = "lang";

    /// <summary>The base culture. Every other culture is an overlay on top of this one.</summary>
    public const string CultureCode = "en";

    /// <summary>The base table's file name inside <see cref="LangFolderName"/>.</summary>
    public const string FileName = CultureCode + ".json";

    /// <summary>The base table's path relative to the app root — written down here and nowhere else.</summary>
    public static readonly string RelativePath = Path.Combine(LangFolderName, FileName);

    /// <summary>The one sentence that says what a usable base table is; every rejection reason starts with it.</summary>
    private const string ShapeRequirement =
        "the string table must be a JSON object whose every value is a string.";

    /// <summary>The second half of that sentence: shape alone never made a table usable.</summary>
    private const string ContentRequirement =
        "the string table must carry a non-blank value for every string the shell itself renders.";

    /// <summary>How many offending keys a rejection names before it summarises the rest — enough to act on,
    /// short enough for the one-line machine-readable report.</summary>
    private const int MaxNamedUnusableKeys = 5;

    /// <summary>
    /// The floor a shipped base table has to clear: every key the shell's own table owns. Missing or blank,
    /// each of them costs something real. Most render as their own raw i18n key — the shipped v0.1.2-beta
    /// defect. The plan vocabulary is the exception: <c>PlanRow</c> guards it with hardcoded English
    /// fallbacks, so its absence silently drops the user's chosen language instead. Neither belongs in a
    /// release, and neither is distinguishable from a healthy install by a check that only asks whether the
    /// file parses.
    /// <para>
    /// Scope is the shell's own vocabulary: chrome and the language selector, the first-run notice, the
    /// confirm gate, Settings (the one screen that renders with no module installed at all), and the
    /// plan-row vocabulary the shell owns on every module's behalf. Strings a module ships in its own
    /// fragment (<c>IWckModule.GetLangFragment</c>) are NOT part of the floor — a base table is complete
    /// without them, and a supported install may carry no modules.
    /// </para>
    /// <para>
    /// Written down here rather than derived from the shipped file, because the shipped file is the thing
    /// being judged — a rule read out of the artifact it validates proves nothing. The cost is a list that
    /// could fall behind the shell, so two fitness checks hold it level: it must cover every key the shell's
    /// source references, and it must equal the shipped shell table's own key set. The second is what
    /// catches the keys composed at runtime (<c>"plan.risk." + the enum name</c>), which no source scan sees.
    /// </para>
    /// <para>Order is the order rejections name keys in, so a failure message is stable.</para>
    /// </summary>
    public static readonly IReadOnlyList<string> RequiredKeys =
    [
        // Chrome, the nav rail's own entry, and the language selector.
        "app.title",
        "app.tagline",
        "nav.settings",
        "nav.settings.desc",
        "common.language",

        // Shared vocabulary the shell owns and module views bind to — one authoritative copy, not six.
        "common.refresh",
        "common.search",
        "common.dryRun.badge",
        "common.dryRun.caption",

        // The confirm gate: the last thing a user reads before anything destructive runs.
        "confirm.tier.reversible",
        "confirm.tier.medium",
        "confirm.tier.irreversible",
        "confirm.type.prompt",
        "confirm.type.word",
        "confirm.approve",
        "confirm.cancel",

        // The first-run notice.
        "firstrun.title",
        "firstrun.body",
        "firstrun.ok",

        // Settings — the one screen that renders with no module installed at all.
        "settings.eyebrow",
        "settings.title",
        "settings.subtitle",
        "settings.language.title",
        "settings.language.body",
        "settings.theme.title",
        "settings.theme.body",
        "settings.theme.current",
        "settings.theme.restartRequired",
        "settings.theme.saveFailed",
        "theme.dark",
        "theme.light",
        "settings.about.title",
        "settings.about.body",
        "settings.about.app",
        "settings.about.version",
        "settings.about.license",
        "settings.about.repository",
        "settings.about.repositoryLink",
        "settings.about.releases",
        "settings.about.releasesLink",

        // Plan-row vocabulary: the shell renders every action row, whichever module planned it. PlanRow
        // falls back to hardcoded English per key, so absence here costs the chosen language rather than
        // legibility — a quieter failure than a raw key, and still not something to ship.
        "plan.verb.run",
        "plan.verb.runElevated",
        "plan.verb.delete",
        "plan.verb.registryKey",
        "plan.verb.registryValue",
        "plan.verb.service",
        "plan.verb.task",
        "plan.verb.copy",
        "plan.verb.restore",
        "plan.args",
        "plan.wholeTree",
        "plan.blocked",
        "plan.undo.label",
        "plan.undo.None",
        "plan.undo.Partial",
        "plan.undo.Full",
        "plan.risk.Info",
        "plan.risk.Low",
        "plan.risk.Medium",
        "plan.risk.High",
        "plan.risk.Critical",
    ];

    /// <summary>Production entry point: read and validate the base table at <paramref name="path"/>.</summary>
    public static BaseStringTableResult Load(string path) => Load(path, File.ReadAllText);

    /// <summary>
    /// The same rule over a caller-supplied read — the seam the startup preflight and its tests use so the
    /// shape contract is exercised without a real file. <paramref name="read"/> is the only filesystem access;
    /// it is expected to throw the way <see cref="File.ReadAllText(string)"/> does, and the cause is preserved
    /// rather than flattened (P26). It supplies content only: it cannot change what counts as usable.
    /// </summary>
    public static BaseStringTableResult Load(string path, Func<string, string> read)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(read);

        string json;
        try
        {
            json = read(path);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // Absent is its own answer, not a read failure: it names a different repair (reinstall/re-extract).
            return BaseStringTableResult.Missing(path, $"{ex.GetType().Name}: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return BaseStringTableResult.Unreadable(path, $"{ex.GetType().Name}: {ex.Message}");
        }

        return Parse(path, json);
    }

    /// <summary>
    /// The shape and content rules, over content alone. Deserialization targets the exact runtime shape the
    /// live table needs, so "valid JSON" is never mistaken for "usable string table"; the required-key floor
    /// then keeps "a string table" from being mistaken for "the shell's string table".
    /// </summary>
    internal static BaseStringTableResult Parse(string path, string json)
    {
        // The nullable value type is deliberate: nullable annotations are erased at runtime, so
        // Dictionary<string,string> would silently accept `"key": null` and hand the UI a null string.
        // Deserializing as string? makes that case visible and it is rejected explicitly below.
        Dictionary<string, string?>? entries;
        try
        {
            entries = JsonSerializer.Deserialize<Dictionary<string, string?>>(json);
        }
        catch (JsonException ex)
        {
            return BaseStringTableResult.Unreadable(path, ShapeRequirement + " " + ex.Message);
        }

        if (entries is null)
            return BaseStringTableResult.Unreadable(path, ShapeRequirement + " Its content is the JSON literal null.");

        var table = new Dictionary<string, string>(entries.Count, StringComparer.Ordinal);
        foreach ((string key, string? value) in entries)
        {
            if (value is null)
                return BaseStringTableResult.Unreadable(path, ShapeRequirement + $" The value of '{key}' is null.");

            table[key] = value;
        }

        // A table the shell cannot speak from is not a loaded table. Blank counts as absent on purpose: a
        // present-but-empty value renders as nothing at all, which is worse than the raw key it replaced.
        string[] unusable =
        [
            .. RequiredKeys.Where(key =>
                !table.TryGetValue(key, out string? value) || string.IsNullOrWhiteSpace(value)),
        ];

        return unusable.Length == 0
            ? BaseStringTableResult.Loaded(path, table)
            : BaseStringTableResult.Unreadable(path, ContentRequirement + " " + DescribeUnusable(unusable));
    }

    /// <summary>Names the offending keys — a rejection the reader cannot act on is only half a report (P26).</summary>
    private static string DescribeUnusable(IReadOnlyList<string> unusable)
    {
        string named = string.Join(", ", unusable.Take(MaxNamedUnusableKeys));
        string rest = unusable.Count > MaxNamedUnusableKeys
            ? $", and {unusable.Count - MaxNamedUnusableKeys} more"
            : string.Empty;

        return $"{unusable.Count} of the {RequiredKeys.Count} required entries are missing or blank: {named}{rest}.";
    }
}
