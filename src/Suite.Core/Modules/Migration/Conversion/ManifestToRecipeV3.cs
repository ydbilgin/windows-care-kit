using System.Text.RegularExpressions;
using WindowsCareKit.Core.Modules.Backup;

namespace WindowsCareKit.Core.Modules.Migration.Conversion;

public abstract record RecipeConversionResult
{
    private RecipeConversionResult() { }

    public sealed record Converted(MigrationRecipe Recipe) : RecipeConversionResult;

    public sealed record Rejected(string Reason) : RecipeConversionResult;
}

public sealed record LegacyManifestFile(IReadOnlyList<LegacyManifestEntry> Entries);

public sealed record LegacyManifestEntry
{
    public string? Id { get; init; }
    public bool Enabled { get; init; } = true;
    public IReadOnlyList<string> Profile { get; init; } = Array.Empty<string>();
    public string? Phase { get; init; }
    public string? Category { get; init; }
    public string? Tier { get; init; }
    public string? Method { get; init; }
    public string? Source { get; init; }
    public string? Target { get; init; }
    public IReadOnlyList<string> Include { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> Exclude { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> RequiresClosedProcesses { get; init; } = Array.Empty<string>();
    public string? SecretHandling { get; init; }
    public LegacyVerify? Verify { get; init; }
    public LegacyRestore? Restore { get; init; }
    public string? Description { get; init; }
    public string? UiWarning { get; init; }
}

public sealed record LegacyVerify(IReadOnlyList<string> Exists, int? MaxSizeMB);

public sealed record LegacyRestore(int? Order, string? Mode, string? Notes);

public static partial class ManifestToRecipeV3
{
    public static RecipeConversionResult Convert(LegacyManifestEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        if (string.IsNullOrWhiteSpace(entry.Id))
            return Reject("missing id");
        if (!IdGrammar().IsMatch(entry.Id))
            return Reject($"id '{entry.Id}' is not a valid v3 recipe id");
        if (!string.Equals(entry.Method, BackupMethod.Copy, StringComparison.OrdinalIgnoreCase))
            return Reject($"method '{entry.Method ?? "(missing)"}' is not convertible to a path recipe");

        SourceParseResult parsedSource = ParseSource(entry.Source);
        if (parsedSource.Reason is not null)
            return Reject(parsedSource.Reason);

        RestoreParseResult restore = ParseRestore(entry.Restore?.Mode);
        if (restore.Reason is not null)
            return Reject(restore.Reason);

        if (restore.Strategy == RestoreStrategy.Replace)
            return Reject("restore mode 'replace' is not executable by the migration restore runner");

        string relativePath = parsedSource.RelativePath!;
        if (HasUnsafeRelativePath(relativePath))
            return Reject("source path must be relative below a supported environment token");

        string category = string.IsNullOrWhiteSpace(entry.Category) ? "legacy" : entry.Category.Trim();
        var exclude = new List<string>(entry.Exclude.Where(s => !string.IsNullOrWhiteSpace(s)));
        bool hasSecretSignal = HasSecretSignal(entry, relativePath);
        if (hasSecretSignal)
            AddMissing(exclude, SecretGlobOverlay.Globs);

        PortabilityClass portability = IsProfileFolder(parsedSource.KnownFolder!.Value) && !hasSecretSignal
            ? PortabilityClass.ProfileRelative
            : PortabilityClass.MachineLocked;
        RestoreTier restoreTier = MapTier(entry.Tier);
        if (portability == PortabilityClass.MachineLocked || !IsProfileFolder(parsedSource.KnownFolder.Value))
            restoreTier = RestoreTier.InventoryOnly;

        var preconditions = entry.RequiresClosedProcesses
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => $"process-closed:{p.Trim()}")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var item = new RecipeItem(
            relativePath,
            CleanList(entry.Include),
            exclude)
        {
            RequiresClosedProcesses = CleanList(entry.RequiresClosedProcesses),
            Verify = entry.Verify is null
                ? null
                : new RecipeItemVerify(CleanList(entry.Verify.Exists), entry.Verify.MaxSizeMB),
        };

        string? warning = FirstNonWhiteSpace(entry.UiWarning, entry.Restore?.Notes);
        var manualTodo = new List<string>();
        if (!entry.Enabled)
            manualTodo.Add("Legacy manifest entry is disabled by default; keep opt-in.");
        if (hasSecretSignal)
            manualTodo.Add("Review secret-bearing content manually; global secret excludes apply.");
        if (portability == PortabilityClass.MachineLocked)
            manualTodo.Add("Reinstall or sign in again on the target machine.");

        var recipe = new MigrationRecipe(
            SchemaVersion: 3,
            Id: entry.Id.Trim(),
            DisplayName: string.IsNullOrWhiteSpace(entry.Description) ? entry.Id.Trim() : entry.Description.Trim(),
            Category: category,
            Detect: new RecipeDetect(parsedSource.KnownFolder.Value, relativePath, entry.Enabled),
            Items: [item],
            Exclude: exclude,
            SecretRule: "global",
            PortabilityClass: portability,
            Restore: new RecipeRestore(restore.Strategy!.Value, restore.Phase!.Value, preconditions))
        {
            RestoreTier = restoreTier,
            CatalogTier = CatalogTier.Community,
            MigrationMeta = new MigrationRecipeMeta(
                UiWarning: warning is null ? null : new LocalizedText(warning, warning),
                ManualSteps: Array.Empty<string>(),
                ManualTodo: manualTodo,
                InstallerSource: null,
                LicenseSource: null,
                RequiresRelogin: portability == PortabilityClass.MachineLocked,
                BackedUpButNotRestored: portability == PortabilityClass.MachineLocked || restoreTier == RestoreTier.InventoryOnly,
                SurvivesOnOtherDrive: false),
        };

        return new RecipeConversionResult.Converted(recipe);
    }

    private static RecipeConversionResult.Rejected Reject(string reason)
        => new(string.IsNullOrWhiteSpace(reason) ? "conversion rejected" : reason);

    private static SourceParseResult ParseSource(string? source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return new SourceParseResult(null, null, "missing source");

        string s = source.Trim();
        if (IsRootedOrDrivePath(s))
            return new SourceParseResult(null, null, "absolute source paths are not convertible");
        if (!s.StartsWith('%'))
            return new SourceParseResult(null, null, "source must begin with a supported environment token");

        int end = s.IndexOf('%', 1);
        if (end <= 1)
            return new SourceParseResult(null, null, "source environment token is malformed");

        string token = s[..(end + 1)].ToUpperInvariant();
        KnownFolder? knownFolder = token switch
        {
            "%USERPROFILE%" => KnownFolder.UserProfile,
            "%APPDATA%" => KnownFolder.AppData,
            "%LOCALAPPDATA%" => KnownFolder.LocalAppData,
            "%PROGRAMDATA%" => KnownFolder.ProgramData,
            "%PROGRAMFILES%" => KnownFolder.ProgramFiles,
            "%PROGRAMFILES(X86)%" => KnownFolder.ProgramFilesX86,
            _ => null,
        };
        if (knownFolder is null)
            return new SourceParseResult(null, null, $"unknown source environment token '{s[..(end + 1)]}'");

        string relative = NormalizeRelative(s[(end + 1)..]);
        if (HasUnsafeRelativePath(relative))
            return new SourceParseResult(null, null, "source path must not contain a root, environment token, parent traversal, or control character");

        return new SourceParseResult(knownFolder.Value, relative, null);
    }

    private static RestoreParseResult ParseRestore(string? mode)
    {
        if (string.IsNullOrWhiteSpace(mode))
            return new RestoreParseResult(null, null, "missing restore mode");

        return mode.Trim().ToLowerInvariant() switch
        {
            "config-write" or "configwrite" or "copy" => new RestoreParseResult(RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, null),
            "merge-after-install" => new RestoreParseResult(RestoreStrategy.MergeAfterInstall, RestorePhase.ConfigWrite, null),
            "replace" => new RestoreParseResult(RestoreStrategy.Replace, RestorePhase.ConfigWrite, null),
            _ => new RestoreParseResult(null, null, $"unknown restore mode '{mode}'"),
        };
    }

    private static RestoreTier MapTier(string? tier)
        => tier?.Trim().ToUpperInvariant() switch
        {
            "T2" => RestoreTier.ConfigCopy,
            "T3" => RestoreTier.MergeAfterInstall,
            _ => RestoreTier.InventoryOnly,
        };

    private static bool HasSecretSignal(LegacyManifestEntry entry, string relativePath)
    {
        string? handling = entry.SecretHandling;
        if (!string.IsNullOrWhiteSpace(handling)
            && !string.Equals(handling, SecretHandling.Normal, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (MigrationSecretFilter.IsSecretLeafName(LeafOf(relativePath)))
            return true;

        return entry.Include.Any(p => MigrationSecretFilter.IsSecretLeafName(LeafOf(p)))
            || entry.Exclude.Any(p => MigrationSecretFilter.IsSecretLeafName(LeafOf(p)));
    }

    private static string NormalizeRelative(string value)
    {
        string normalized = value.Replace('\\', '/').TrimStart('/');
        return string.IsNullOrWhiteSpace(normalized) ? "." : normalized;
    }

    private static bool HasUnsafeRelativePath(string value)
    {
        string normalized = value.Replace('\\', '/');
        return string.IsNullOrWhiteSpace(normalized)
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.StartsWith("//", StringComparison.Ordinal)
            || IsRootedOrDrivePath(normalized)
            || normalized.Contains('%', StringComparison.Ordinal)
            || normalized.Split('/', StringSplitOptions.None).Any(segment => segment == "..")
            || normalized.Any(char.IsControl);
    }

    private static bool IsRootedOrDrivePath(string value)
        => value.StartsWith(@"\\", StringComparison.Ordinal)
           || value.StartsWith("/", StringComparison.Ordinal)
           || (value.Length >= 2 && char.IsAsciiLetter(value[0]) && value[1] == ':');

    private static IReadOnlyList<string> CleanList(IEnumerable<string>? values)
        => values?.Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s.Trim()).ToArray()
           ?? Array.Empty<string>();

    private static void AddMissing(List<string> target, IEnumerable<string> values)
    {
        foreach (string value in values)
            if (!target.Contains(value, StringComparer.OrdinalIgnoreCase))
                target.Add(value);
    }

    private static string? FirstNonWhiteSpace(params string?[] values)
        => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim();

    private static string LeafOf(string path)
    {
        string s = path.Replace('\\', '/').TrimEnd('/');
        int slash = s.LastIndexOf('/');
        return slash >= 0 ? s[(slash + 1)..] : s;
    }

    private static bool IsProfileFolder(KnownFolder folder)
        => folder is KnownFolder.UserProfile or KnownFolder.AppData or KnownFolder.LocalAppData;

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdGrammar();

    private sealed record SourceParseResult(KnownFolder? KnownFolder, string? RelativePath, string? Reason);

    private sealed record RestoreParseResult(RestoreStrategy? Strategy, RestorePhase? Phase, string? Reason);
}
