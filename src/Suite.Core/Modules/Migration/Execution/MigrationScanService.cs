using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Selection;

namespace WindowsCareKit.Core.Modules.Migration.Execution;

public sealed record MigrationScanResult(
    DetectionResult Detection,
    string ProfileRoot,
    IReadOnlyList<MigrationSelectionCandidate> Candidates);

public interface ICloudBackupSignal
{
    bool HasCloudBackup(string? sourcePath);
}

public sealed class OneDriveKnownFolderContainmentSignal : ICloudBackupSignal
{
    private readonly IReadOnlyList<string> _oneDriveUserFolders;

    public OneDriveKnownFolderContainmentSignal(IEnumerable<string> oneDriveUserFolders)
    {
        ArgumentNullException.ThrowIfNull(oneDriveUserFolders);
        _oneDriveUserFolders = oneDriveUserFolders
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public bool HasCloudBackup(string? sourcePath)
    {
        if (string.IsNullOrWhiteSpace(sourcePath))
            return false;

        string fullPath;
        try
        {
            fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourcePath));
        }
        catch
        {
            return false;
        }

        return _oneDriveUserFolders.Any(root => IsContained(root, fullPath));
    }

    private static bool IsContained(string root, string candidate)
        => string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
           || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

public sealed class NoCloudBackupSignal : ICloudBackupSignal
{
    public static NoCloudBackupSignal Instance { get; } = new();

    private NoCloudBackupSignal() { }

    // TODO(P4): replace the constant fallback with a real OneDrive account reader
    // (HKCU\Software\Microsoft\OneDrive\Accounts\*\UserFolder) when that read-only port is added.
    public bool HasCloudBackup(string? sourcePath) => false;
}

/// <summary>Read-only, on-demand program and recipe scan seam used by the Migration UI.</summary>
public interface IMigrationScanService
{
    MigrationScanResult Scan(CancellationToken cancellationToken = default);
}

/// <summary>
/// Bridges the canonical program detector and built-in recipe resolver into presentation-safe selection
/// candidates. Every dependency is read-only and injectable so tests never touch the host registry or disk.
/// </summary>
public sealed class MigrationScanService : IMigrationScanService
{
    private readonly IReadOnlyList<IProgramSource> _programSources;
    private readonly Func<ProfileRoots> _profileRoots;
    private readonly IRecipeFileSystem _fileSystem;
    private readonly IContentSignatureProbe _contentProbe;
    private readonly Func<IReadOnlyList<MigrationRecipe>> _recipeSource;
    private readonly ICloudBackupSignal _cloudBackupSignal;

    public MigrationScanService(
        IEnumerable<IProgramSource> programSources,
        Func<ProfileRoots> profileRoots,
        IRecipeFileSystem fileSystem,
        IContentSignatureProbe contentProbe,
        Func<IReadOnlyList<MigrationRecipe>>? recipeSource = null,
        ICloudBackupSignal? cloudBackupSignal = null)
    {
        ArgumentNullException.ThrowIfNull(programSources);
        _programSources = [.. programSources];
        _profileRoots = profileRoots ?? throw new ArgumentNullException(nameof(profileRoots));
        _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
        _contentProbe = contentProbe ?? throw new ArgumentNullException(nameof(contentProbe));
        _recipeSource = recipeSource ?? BuiltinRecipeSource.LoadAll;
        _cloudBackupSignal = cloudBackupSignal ?? NoCloudBackupSignal.Instance;
    }

    public MigrationScanResult Scan(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ProfileRoots roots = _profileRoots();
        RecipePathResolver paths = new(roots);
        RecipeResolver resolver = new(paths, _fileSystem);
        DetectionResult detection = new ProgramDetector(_programSources).Detect();
        var candidates = new List<MigrationSelectionCandidate>();

        foreach (MigrationRecipe recipe in _recipeSource())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!recipe.Detect.Exists && !HasPositivePresence(recipe, paths, detection.Programs))
                continue;

            ResolvedRecipe resolved;
            try
            {
                resolved = resolver.Resolve(recipe);
            }
            catch
            {
                // A recipe that cannot be resolved must fail closed and must not become a green candidate.
                if (IsPresent(recipe, paths))
                    candidates.Add(BuildFallbackCandidate(recipe, paths, resolutionFailed: true));
                continue;
            }

            if (!resolved.DetectMatched)
                continue;

            IReadOnlyList<BridgedMigrationItem> bridged;
            try
            {
                bridged = RecipeToBackupEntry.Bridge(resolved, _contentProbe);
            }
            catch
            {
                bridged = Array.Empty<BridgedMigrationItem>();
            }

            if (bridged.Count == 0)
            {
                candidates.Add(BuildFallbackCandidate(
                    recipe,
                    paths,
                    resolutionFailed: resolved.Skipped.Count > 0));
                continue;
            }

            foreach (BridgedMigrationItem item in bridged)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string source = item.Entry.Source;
                candidates.Add(BuildCandidate(
                    recipe,
                    item.Meta,
                    item.Entry.Id,
                    source,
                    item.Entry.Target,
                    SourceKind(source),
                    isRegenerable: recipe.Install is not null));
            }
        }

        return new MigrationScanResult(detection, roots.UserProfile, candidates);
    }

    private MigrationSelectionCandidate BuildFallbackCandidate(
        MigrationRecipe recipe,
        RecipePathResolver paths,
        bool resolutionFailed)
    {
        string? source = null;
        try
        {
            source = paths.Resolve(recipe.Detect.KnownFolder, recipe.Detect.Path);
        }
        catch (RecipePathException)
        {
            resolutionFailed = true;
        }

        MigrationItemMeta meta = FallbackMeta(recipe) with
        {
            HasMachineBoundContent = resolutionFailed || recipe.PortabilityClass != PortabilityClass.ProfileRelative,
        };
        return BuildCandidate(
            recipe,
            meta,
            $"{recipe.Id}#present",
            source,
            null,
            source is null ? MigrationSourceKind.None : SourceKind(source),
            isRegenerable: recipe.Install is not null);
    }

    private MigrationSelectionCandidate BuildCandidate(
        MigrationRecipe recipe,
        MigrationItemMeta meta,
        string id,
        string? source,
        string? destination,
        MigrationSourceKind sourceKind,
        bool isRegenerable)
    {
        MigrationRecipeMeta? migration = recipe.MigrationMeta;
        return new MigrationSelectionCandidate
        {
            Id = id,
            DisplayName = recipe.DisplayName,
            RecipeCategory = recipe.Category,
            Meta = meta,
            RestoreTier = recipe.RestoreTier,
            SourcePath = source,
            DestinationPath = destination,
            SourceKind = sourceKind,
            WhatHappens = migration?.UiWarning?.En ?? string.Empty,
            WhatHappensTr = migration?.UiWarning?.Tr,
            WhatHappensEn = migration?.UiWarning?.En,
            HasCloudBackup = _cloudBackupSignal.HasCloudBackup(source),
            IsOnSystemDrive = IsOnSystemDrive(source),
            IsUnique = !isRegenerable,
            IsRegenerable = isRegenerable,
            IsRecognized = true,
            HasInstallRecord = recipe.Install is not null,
            InstallMethod = recipe.Install?.Method,
            BackedUpButNotRestored = migration?.BackedUpButNotRestored == true,
            RequiresRelogin = migration?.RequiresRelogin == true,
            ManualTodo = MergeManualTodo(recipe),
        };
    }

    private bool IsPresent(MigrationRecipe recipe, RecipePathResolver paths)
    {
        try
        {
            string path = paths.Resolve(recipe.Detect.KnownFolder, recipe.Detect.Path);
            return _fileSystem.DirectoryExists(path) || _fileSystem.FileExists(path);
        }
        catch (RecipePathException)
        {
            return false;
        }
    }

    private MigrationSourceKind SourceKind(string path)
        => _fileSystem.DirectoryExists(path)
            ? MigrationSourceKind.Directory
            : _fileSystem.FileExists(path)
                ? MigrationSourceKind.File
            : MigrationSourceKind.None;

    private bool HasPositivePresence(
        MigrationRecipe recipe,
        RecipePathResolver paths,
        IReadOnlyList<DiscoveredProgram> programs)
    {
        if (HasDetectedProgram(recipe, programs))
            return true;

        if (DetectPathIsConcrete(recipe.Detect) && IsPresent(recipe, paths))
            return true;

        foreach (RecipeItem item in recipe.Items)
        {
            if (item.Kind != RecipeItemKind.ProfilePath)
                continue;
            try
            {
                string path = paths.Resolve(recipe.Detect.KnownFolder, item.Path);
                if (_fileSystem.DirectoryExists(path) || _fileSystem.FileExists(path))
                    return true;
            }
            catch (RecipePathException)
            {
                continue;
            }
        }

        return false;
    }

    private static bool DetectPathIsConcrete(RecipeDetect detect)
    {
        string path = detect.Path.Replace('\\', '/').Trim('/');
        return path.Length > 0 && path != ".";
    }

    private static bool HasDetectedProgram(
        MigrationRecipe recipe,
        IReadOnlyList<DiscoveredProgram> programs)
    {
        string displayName = ProgramJoinKeys.NormalizeName(recipe.DisplayName);
        var installHints = recipe.InstallPathHint
            .Select(ProgramJoinKeys.NormalizeName)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToArray();

        foreach (DiscoveredProgram program in programs)
        {
            if (!string.IsNullOrWhiteSpace(recipe.WingetId)
                && (string.Equals(program.ReinstallId, recipe.WingetId, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(program.ReinstallId, "winget:" + recipe.WingetId, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (recipe.ProductCode.Any(pc => string.Equals(pc, program.ProductCode, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (recipe.PackageFamilyName.Any(pfn => string.Equals(pfn, program.PackageFamilyName, StringComparison.OrdinalIgnoreCase)))
                return true;
            if (!string.IsNullOrWhiteSpace(displayName)
                && string.Equals(displayName, program.NormalizedName, StringComparison.OrdinalIgnoreCase))
                return true;
            if (installHints.Any(hint => string.Equals(hint, program.InstallPathLeaf, StringComparison.OrdinalIgnoreCase)
                                         || string.Equals(hint, program.NormalizedName, StringComparison.OrdinalIgnoreCase)))
                return true;
        }

        return false;
    }

    private static MigrationItemMeta FallbackMeta(MigrationRecipe recipe)
    {
        IReadOnlyList<string> preconditions = recipe.Items
            .SelectMany(item => item.RequiresClosedProcesses.Select(process => $"process-closed:{process}"))
            .Concat(recipe.Restore.Preconditions)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MigrationItemMeta(
            recipe.Id,
            $"{recipe.Id}#present",
            recipe.PortabilityClass,
            recipe.Restore.Strategy,
            recipe.Restore.Phase,
            preconditions)
        {
            HasExcludedSecret = recipe.Items.Any(ItemDeclaresSecret),
        };
    }

    private static bool ItemDeclaresSecret(RecipeItem item)
        => MigrationSecretFilter.IsSecretLeafName(LeafOf(item.Path))
           || item.Include.Any(include => MigrationSecretFilter.IsSecretLeafName(LeafOf(include)));

    private static string LeafOf(string path)
    {
        string normalized = path.Replace('\\', '/').TrimEnd('/');
        int slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static IReadOnlyList<string> MergeManualTodo(MigrationRecipe recipe)
        => recipe.Items
            .SelectMany(item => item.ManualTodo)
            .Concat(recipe.MigrationMeta?.ManualTodo ?? Array.Empty<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

    private static bool IsOnSystemDrive(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;
        string? sourceRoot = Path.GetPathRoot(path);
        string? systemRoot = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows));
        return !string.IsNullOrWhiteSpace(sourceRoot)
               && string.Equals(sourceRoot, systemRoot, StringComparison.OrdinalIgnoreCase);
    }
}
