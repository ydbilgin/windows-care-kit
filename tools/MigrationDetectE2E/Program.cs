using System.Security.Principal;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Win32;

namespace WindowsCareKit.Tools.MigrationDetectE2E;

internal static class Program
{
    private const int ExitOk = 0;
    private const int ExitFalseGreen = 1;

    internal static int Main()
    {
        try
        {
            Console.WriteLine("===== Migration Detect E2E: real-machine read-only validation =====");
            Console.WriteLine("Mode: read-only inventory/report only; no install, no restore, no backup, no registry writes.");
            Console.WriteLine();

            DetectionResult detection = RunBroadDetection();
            Console.WriteLine();
            return RunRecipeValidation();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"UNHANDLED EXCEPTION: {ex.GetType().Name}: {ex.Message}");
            return ExitFalseGreen;
        }
    }

    private static DetectionResult RunBroadDetection()
    {
        Console.WriteLine("===== Section A - Broad detection =====");

        Win32PathCanonicalizer canon = new();
        Win32RegistryProbe registry = new();
        string? currentUserSid = CurrentUserSid();

        ProgramDetector detector = new(
        [
            new RegistryUninstallSource(new Win32InstalledAppReader(registry), canon),
            new MsiProductSource(new Win32MsiCatalog(), canon, currentUserSid),
            new AppxProgramSource(new Win32AppxReader(), canon),
            new AppPathsSource(registry, canon),
            new StartMenuSource(new Win32StartMenuShortcutReader(), canon),
        ]);

        DetectionResult result = detector.Detect();
        Console.WriteLine($"DedupedProgramCount: {result.Programs.Count}");
        Console.WriteLine("PerSource:");
        foreach (ProgramSourceReport report in result.SourceReports)
            Console.WriteLine($"  {report.Kind} / {report.Status} / Count={report.Count}");

        Console.WriteLine($"B-5 LaunchableButNoInstallRecord: {result.LaunchableWithoutInstallRecordCount}");
        Console.WriteLine("FirstPrograms:");
        foreach (DiscoveredProgram program in result.Programs.Take(30))
        {
            string publisher = string.IsNullOrWhiteSpace(program.Publisher) ? "(none)" : program.Publisher;
            string sources = string.Join("+", program.Sources);
            Console.WriteLine($"  {program.DisplayName} / {publisher} / {program.Scope} / {sources}");
        }

        return result;
    }

    private static int RunRecipeValidation()
    {
        Console.WriteLine("===== Section B - Recipe applicability + honesty =====");

        IReadOnlyList<MigrationRecipe> recipes = BuiltinRecipeSource.LoadAll();
        ProfileRoots roots = ProfileRoots.ForCurrentUser();
        RecipePathResolver pathResolver = new(roots);
        Win32RecipeFileSystem fs = new();
        RecipeResolver recipeResolver = new(pathResolver, fs);
        Win32ContentSignatureProbe contentProbe = new();

        int presentCount = 0;
        int falseGreenCount = 0;

        Console.WriteLine($"BuiltinRecipeCount: {recipes.Count}");

        foreach (MigrationRecipe recipe in recipes)
        {
            DetectPresence presence = DetectPresent(recipe, pathResolver);
            if (!presence.Present)
                continue;

            presentCount++;
            RecipeHonesty honesty = BuildHonesty(recipe, recipeResolver, contentProbe);

            if (honesty.FalseGreen)
            {
                falseGreenCount++;
                Console.WriteLine($"*** FALSE-GREEN VIOLATION: {recipe.Id} ***");
            }

            Console.WriteLine(
                $"  id={recipe.Id} present=true detect={presence.Kind} " +
                $"portabilityClass={recipe.PortabilityClass} restoreTier={recipe.RestoreTier} " +
                $"badge-glyph={honesty.Glyph} MayClaimWorks={honesty.MayClaimWorks} " +
                $"machineLocked={honesty.MachineLocked} secret={honesty.Secret}");
        }

        Console.WriteLine($"Summary: {presentCount} present of {recipes.Count} builtin recipes on this machine.");
        if (falseGreenCount > 0)
            return ExitFalseGreen;

        Console.WriteLine($"HONESTY-FLOOR-OK: {presentCount} present recipe, 0 false-green");
        return ExitOk;
    }

    private static DetectPresence DetectPresent(MigrationRecipe recipe, RecipePathResolver resolver)
    {
        try
        {
            string detectPath = resolver.Resolve(recipe.Detect.KnownFolder, recipe.Detect.Path);
            if (Directory.Exists(detectPath))
                return new DetectPresence(true, "directory");
            if (File.Exists(detectPath))
                return new DetectPresence(true, "file");
            return new DetectPresence(false, "absent");
        }
        catch (RecipePathException)
        {
            return new DetectPresence(false, "resolve-failed");
        }
    }

    private static RecipeHonesty BuildHonesty(
        MigrationRecipe recipe,
        RecipeResolver resolver,
        IContentSignatureProbe contentProbe)
    {
        List<MigrationItemMeta> metas = [];
        try
        {
            ResolvedRecipe resolved = resolver.Resolve(recipe);
            metas.AddRange(RecipeToBackupEntry.Bridge(resolved, contentProbe).Select(x => x.Meta));
        }
        catch
        {
            // A present recipe that cannot be resolved must fail closed for badge purposes.
            metas.Add(FallbackMeta(recipe) with { HasMachineBoundContent = true });
        }

        if (metas.Count == 0)
            metas.Add(FallbackMeta(recipe));

        IReadOnlyList<PortabilityBadgeResult> badges = metas.Select(PortabilityBadge.Compute).ToArray();
        bool mayClaimWorks = badges.Count > 0 && badges.All(b => b.MayClaimWorks);
        bool secret = metas.Any(m => m.HasExcludedSecret);
        bool machineLocked = recipe.PortabilityClass == PortabilityClass.MachineLocked
            || metas.Any(m => m.PortabilityClass == PortabilityClass.MachineLocked || m.HasMachineBoundContent);
        bool partial = recipe.PortabilityClass == PortabilityClass.Partial
            || metas.Any(m => m.PortabilityClass == PortabilityClass.Partial);
        bool falseGreen = mayClaimWorks && (machineLocked || partial || secret);
        string glyph = WorstGlyph(badges);

        return new RecipeHonesty(glyph, mayClaimWorks, machineLocked, secret, falseGreen);
    }

    private static MigrationItemMeta FallbackMeta(MigrationRecipe recipe)
    {
        IReadOnlyList<string> preconditions = recipe.Items
            .SelectMany(i => i.RequiresClosedProcesses.Select(p => $"process-closed:{p}"))
            .Concat(recipe.Restore.Preconditions)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        return new MigrationItemMeta(
            RecipeId: recipe.Id,
            EntryId: $"{recipe.Id}#present",
            PortabilityClass: recipe.PortabilityClass,
            RestoreStrategy: recipe.Restore.Strategy,
            RestorePhase: recipe.Restore.Phase,
            Preconditions: preconditions)
        {
            HasExcludedSecret = RecipeDeclaresSecret(recipe),
        };
    }

    private static bool RecipeDeclaresSecret(MigrationRecipe recipe)
    {
        foreach (RecipeItem item in recipe.Items)
        {
            if (MigrationSecretFilter.IsSecretLeafName(LeafOf(item.Path)))
                return true;
            if (item.Include.Any(include => MigrationSecretFilter.IsSecretLeafName(LeafOf(include))))
                return true;
        }

        return false;
    }

    private static string LeafOf(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        string normalized = path.Replace('\\', '/').TrimEnd('/');
        int slash = normalized.LastIndexOf('/');
        return slash >= 0 ? normalized[(slash + 1)..] : normalized;
    }

    private static string WorstGlyph(IReadOnlyList<PortabilityBadgeResult> badges)
    {
        if (badges.Count == 0)
            return "❌";
        if (badges.Any(b => b.Kind == BadgeKind.MachineLocked))
            return "❌";
        if (badges.Any(b => b.Kind == BadgeKind.Partial))
            return "⚠️";
        if (badges.Any(b => b.Kind == BadgeKind.PortableWithStep))
            return "🔁";
        return "✅";
    }

    private static string? CurrentUserSid()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch
        {
            return null;
        }
    }

    private sealed record DetectPresence(bool Present, string Kind);

    private sealed record RecipeHonesty(
        string Glyph,
        bool MayClaimWorks,
        bool MachineLocked,
        bool Secret,
        bool FalseGreen);
}
