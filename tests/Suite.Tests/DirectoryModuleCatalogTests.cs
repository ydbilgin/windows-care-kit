using System.IO;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Tests.TestInfra;
using Xunit;
using WpfApp = WindowsCareKit.App.App;

namespace WindowsCareKit.Tests;

/// <summary>
/// Exercises the real M4 runtime discovery path (<see cref="DirectoryModuleCatalog"/>) against fixtures
/// built from the actual module DLLs the build lays out under <c>bin\...\Modules\&lt;id&gt;\</c> (B9). The
/// test bin holds those same DLLs at its root too (compile references), and because a module's root copy
/// and its <c>Modules\</c> copy share an MVID, default-ALC <c>LoadFromAssemblyPath</c> dedupes them — so
/// discovery loads the identical assembly with no identity split.
/// </summary>
public sealed class DirectoryModuleCatalogTests
{
    private static readonly string[] FullOrderedIds =
        { "uninstall", "clean", "backup", "migration", "install", "restore", "settings" };

    private static readonly string[] FullGlyphs =
        { "", "", "", "", "", "", "" };

    private static string TestBinModulesRoot => Path.Combine(AppContext.BaseDirectory, "Modules");

    [Fact] // t1 — full-set pin (replaces the old StaticModuleCatalog pin): default-rooted discovery.
    public void Default_rooted_catalog_yields_pinned_ids_in_order_with_existing_glyphs()
    {
        var catalog = new DirectoryModuleCatalog();

        ModuleCatalogResult result = catalog.LoadModules();

        Assert.Equal(FullOrderedIds, result.Modules.Select(m => m.Id).ToArray());
        Assert.Equal(FullGlyphs, result.Modules.Select(m => m.IconKey).ToArray());
        Assert.Equal(ModuleInventoryStatus.Complete, result.Health.Status);
        Assert.All(
            result.Health.Components,
            component => Assert.Equal(ModuleComponentStatus.Loaded, component.Status));
    }

    [Fact] // t2 — a module whose folder is absent is simply not present, no throw.
    public void Missing_module_folder_drops_only_that_module_and_never_deep_links_to_it()
    {
        using var ws = new TempWorkspace("wck-modcat-");
        string root = BuildFixture(ws, omitFolders: "migration");

        ModuleCatalogResult result = new DirectoryModuleCatalog(root).LoadModules();
        IReadOnlyList<IWckModule> modules = result.Modules;

        Assert.Equal(
            new[] { "uninstall", "clean", "backup", "install", "restore", "settings" },
            modules.Select(m => m.Id).ToArray());
        Assert.DoesNotContain(modules, m => m.Id == "migration");

        using ServiceProvider provider = BuildProvider(modules);
        var vm = provider.GetRequiredService<MainViewModel>();
        Assert.False(vm.SelectNavByKey("migration"));
        Assert.DoesNotContain(vm.Nav, item => item.Id == "migration");
    }

    [Fact] // t3 — structural floor: the catalog never returns empty; the shell stays available.
    public async Task Missing_or_empty_root_still_yields_settings_only_and_a_safe_shell()
    {
        string nonexistent = Path.Combine(
            Path.GetTempPath(),
            "wck-modcat-nonexistent-" + Guid.NewGuid().ToString("N"));
        ModuleCatalogResult fromMissing = new DirectoryModuleCatalog(nonexistent).LoadModules();
        Assert.Equal(new[] { "settings" }, fromMissing.Modules.Select(m => m.Id).ToArray());

        using var ws = new TempWorkspace("wck-modcat-");
        string emptyRoot = ws.Combine("Modules");
        Directory.CreateDirectory(emptyRoot);
        ModuleCatalogResult fromEmpty = new DirectoryModuleCatalog(emptyRoot).LoadModules();
        Assert.Equal(new[] { "settings" }, fromEmpty.Modules.Select(m => m.Id).ToArray());

        using ServiceProvider provider = BuildProvider(fromEmpty.Modules);
        var vm = provider.GetRequiredService<MainViewModel>();
        NavItem onlyNav = Assert.Single(vm.Nav);
        Assert.Equal("settings", onlyNav.Id);
        Assert.NotNull(vm.CurrentContent);
        Assert.Null(await Record.ExceptionAsync(vm.OnShellStartupAsync));
    }

    /// <summary>
    /// t3b — absent and present-but-empty roots are the same user fact: no component is installed. The
    /// install-shape distinction remains owned by <c>--verify-layout</c>, whose Modules token reports
    /// <c>OPTIONAL-ABSENT</c> or <c>OPTIONAL-PRESENT</c>.
    /// </summary>
    [Fact]
    public void Absent_and_empty_module_roots_are_both_not_installed()
    {
        string nonexistent = Path.Combine(
            Path.GetTempPath(),
            "wck-modcat-nonexistent-" + Guid.NewGuid().ToString("N"));
        ModuleCatalogResult absent = new DirectoryModuleCatalog(nonexistent).LoadModules();

        using var ws = new TempWorkspace("wck-modcat-");
        string emptyRoot = ws.Combine("Modules");
        Directory.CreateDirectory(emptyRoot);
        ModuleCatalogResult empty = new DirectoryModuleCatalog(emptyRoot).LoadModules();

        Assert.Equal(ModuleInventoryStatus.NotInstalled, absent.Health.Status);
        Assert.Empty(absent.Health.Components);
        Assert.Equal(Path.GetFullPath(nonexistent), absent.Health.ModulesRoot);
        Assert.Equal(ModuleInventoryStatus.NotInstalled, empty.Health.Status);
        Assert.Empty(empty.Health.Components);
        Assert.Equal(Path.GetFullPath(emptyRoot), empty.Health.ModulesRoot);
    }

    [Fact] // t4 — every bad folder is typed precisely; valid modules still load.
    public void Corrupt_id_mismatched_and_non_module_folders_are_typed_and_do_not_hide_valid_modules()
    {
        using var ws = new TempWorkspace("wck-modcat-");
        string root = BuildFixture(ws);

        // (a) garbage bytes named like a module — BadImageFormat on load.
        string junkDir = Directory.CreateDirectory(Path.Combine(root, "junk")).FullName;
        File.WriteAllBytes(
            Path.Combine(junkDir, "Suite.Module.Junk.dll"),
            new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02, 0x03 });

        // (b) a REAL module DLL whose id ("uninstall") does not match its folder ("uninstall2").
        string u2Dir = Directory.CreateDirectory(Path.Combine(root, "uninstall2")).FullName;
        File.Copy(
            Path.Combine(TestBinModulesRoot, "uninstall", "Suite.Module.Uninstall.dll"),
            Path.Combine(u2Dir, "Suite.Module.Uninstall2.dll"));

        // (c) a valid assembly whose matching-name DLL contains NO IWckModule (the Recipes private dep).
        string recipeDir = Directory.CreateDirectory(Path.Combine(root, "recipesonly")).FullName;
        File.Copy(
            Path.Combine(TestBinModulesRoot, "migration", "Suite.Module.Migration.Recipes.dll"),
            Path.Combine(recipeDir, "Suite.Module.Recipesonly.dll"));

        ModuleCatalogResult result = new DirectoryModuleCatalog(root).LoadModules();

        Assert.Equal(FullOrderedIds, result.Modules.Select(m => m.Id).ToArray());
        Assert.Equal(ModuleInventoryStatus.Degraded, result.Health.Status);
        Assert.Contains(
            new ModuleComponentRecord(
                "junk",
                ModuleComponentStatus.Malformed,
                nameof(BadImageFormatException)),
            result.Health.Components);
        Assert.Contains(
            new ModuleComponentRecord(
                "uninstall2",
                ModuleComponentStatus.Malformed,
                ModuleCatalogHealth.CategoryIdMismatch),
            result.Health.Components);
        Assert.Contains(
            new ModuleComponentRecord(
                "recipesonly",
                ModuleComponentStatus.Malformed,
                ModuleCatalogHealth.CategoryNoModuleType),
            result.Health.Components);
    }

    [Fact]
    public void A_directory_without_its_module_dll_is_incomplete_not_malformed()
    {
        using var ws = new TempWorkspace("wck-modcat-incomplete-");
        string root = ws.Combine("Modules");
        Directory.CreateDirectory(Path.Combine(root, "ghost"));

        ModuleCatalogResult result = new DirectoryModuleCatalog(root).LoadModules();

        ModuleComponentRecord record = Assert.Single(result.Health.Components);
        Assert.Equal(
            new ModuleComponentRecord("ghost", ModuleComponentStatus.Incomplete, null),
            record);
        Assert.Equal(ModuleInventoryStatus.Degraded, result.Health.Status);
    }

    [Fact]
    public void A_locked_module_file_is_reported_unreadable_not_malformed()
    {
        using var ws = new TempWorkspace("wck-modcat-locked-");
        string root = ws.Combine("Modules");
        string moduleDirectory = Directory.CreateDirectory(Path.Combine(root, "uninstall")).FullName;
        string modulePath = Path.Combine(moduleDirectory, "Suite.Module.uninstall.dll");
        File.Copy(
            Path.Combine(TestBinModulesRoot, "uninstall", "Suite.Module.Uninstall.dll"),
            modulePath);
        using var held = new FileStream(modulePath, FileMode.Open, FileAccess.Read, FileShare.None);

        ModuleCatalogResult result = new DirectoryModuleCatalog(root).LoadModules();

        ModuleComponentRecord record = Assert.Single(result.Health.Components);
        Assert.Equal(ModuleComponentStatus.Unreadable, record.Status);
        Type? failureType = typeof(IOException).Assembly.GetType($"System.IO.{record.FailureCategory}");
        Assert.NotNull(failureType);
        Assert.True(typeof(IOException).IsAssignableFrom(failureType), record.FailureCategory);
        Assert.Equal(new[] { "settings" }, result.Modules.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void An_unenumerable_root_is_unavailable_not_notInstalled()
    {
        using var ws = new TempWorkspace("wck-modcat-unavailable-");
        string root = ws.Combine("Modules");
        Directory.CreateDirectory(root);
        var catalog = new DirectoryModuleCatalog(
            root,
            _ => throw new UnauthorizedAccessException("synthetic"));

        ModuleCatalogResult result = catalog.LoadModules();

        Assert.Equal(ModuleInventoryStatus.Unavailable, result.Health.Status);
        Assert.Empty(result.Health.Components);
        Assert.Equal(nameof(UnauthorizedAccessException), result.Health.FailureCategory);
        Assert.Equal(new[] { "settings" }, result.Modules.Select(m => m.Id).ToArray());
    }

    [Fact]
    public void A_failing_directory_does_not_stop_the_remaining_directories()
    {
        using var ws = new TempWorkspace("wck-modcat-continue-");
        string root = ws.Combine("Modules");
        string junkDirectory = Directory.CreateDirectory(Path.Combine(root, "junk")).FullName;
        File.WriteAllBytes(
            Path.Combine(junkDirectory, "Suite.Module.junk.dll"),
            new byte[] { 0x4D, 0x5A, 0x00 });
        string validDirectory = Directory.CreateDirectory(Path.Combine(root, "uninstall")).FullName;
        File.Copy(
            Path.Combine(TestBinModulesRoot, "uninstall", "Suite.Module.Uninstall.dll"),
            Path.Combine(validDirectory, "Suite.Module.uninstall.dll"));
        var catalog = new DirectoryModuleCatalog(root, _ => [junkDirectory, validDirectory]);

        ModuleCatalogResult result = catalog.LoadModules();

        Assert.Contains(result.Modules, module => module.Id == "uninstall");
        Assert.Collection(
            result.Health.Components,
            failed => Assert.Equal(ModuleComponentStatus.Malformed, failed.Status),
            loaded => Assert.Equal(ModuleComponentStatus.Loaded, loaded.Status));
    }

    [Fact] // t5 — output is (Order, Id)-sorted, independent of filesystem enumeration order.
    public void Discovered_modules_are_sorted_by_order_then_id_not_by_folder_name()
    {
        using var ws = new TempWorkspace("wck-modcat-");
        string root = BuildFixture(ws);

        string[] ids = new DirectoryModuleCatalog(root)
            .LoadModules()
            .Modules
            .Select(m => m.Id)
            .ToArray();

        Assert.Equal(FullOrderedIds, ids);
        // Alphabetical (the usual directory-enumeration order) would be a DIFFERENT sequence, proving the
        // catalog re-sorts by Order rather than passing through filesystem order.
        string[] alphabetical =
            { "backup", "clean", "install", "migration", "restore", "uninstall", "settings" };
        Assert.NotEqual(alphabetical, ids);
    }

    [Fact]
    public void Catalog_records_only_sanitized_directory_labels()
    {
        using var ws = new TempWorkspace("wck-modcat-label-");
        string root = ws.Combine("Modules");
        string rawName = "spoof\u0085\u202Ename";
        Directory.CreateDirectory(Path.Combine(root, rawName));

        ModuleCatalogResult result = new DirectoryModuleCatalog(root).LoadModules();

        ModuleComponentRecord record = Assert.Single(result.Health.Components);
        Assert.Equal("spoof\uFFFD\uFFFDname", record.DirectoryName);
        Assert.DoesNotContain('\u202E', record.DirectoryName);
        Assert.All(
            result.Health.Components,
            item => Assert.DoesNotContain(item.DirectoryName, char.IsControl));
    }

    private static string BuildFixture(TempWorkspace ws, params string[] omitFolders)
    {
        string root = ws.Combine("Modules");
        CopyDirectory(TestBinModulesRoot, root);
        foreach (string omit in omitFolders)
        {
            string dir = Path.Combine(root, omit);
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
        return root;
    }

    private static void CopyDirectory(string src, string dest)
    {
        Directory.CreateDirectory(dest);
        foreach (string file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(dest, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static ServiceProvider BuildProvider(IReadOnlyList<IWckModule> modules)
    {
        var services = new ServiceCollection();
        WpfApp.AddBaseServices(services, Array.Empty<string>(), TestHelpers.NoComponentsDiscovered());
        foreach (IWckModule module in modules)
            module.RegisterServices(services);
        services.AddSingleton(modules);
        return services.BuildServiceProvider();
    }
}

public sealed class ModuleDirectoryLabelTests
{
    [Fact]
    public void Ordinary_names_are_unchanged()
        => Assert.Equal("migration", ModuleDirectoryLabel.ForDisplay("migration"));

    [Fact]
    public void Control_format_and_separator_characters_are_replaced()
        => Assert.Equal(
            "a\uFFFDb\uFFFDc\uFFFDd\uFFFDe\uFFFDf\uFFFDg",
            ModuleDirectoryLabel.ForDisplay("a\u202Eb\rc\nd\te\u0000f\u2028g"));

    [Fact]
    public void Overlong_names_are_truncated_to_the_owned_limit_with_an_ellipsis()
    {
        string result = ModuleDirectoryLabel.ForDisplay(new string('a', 200));

        Assert.Equal(ModuleDirectoryLabel.MaxLength, result.Length);
        Assert.Equal('\u2026', result[^1]);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Missing_or_blank_names_become_one_replacement_character(string? value)
        => Assert.Equal(
            ModuleDirectoryLabel.Replacement.ToString(),
            ModuleDirectoryLabel.ForDisplay(value!));
}
