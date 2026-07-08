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
        { "uninstall", "clean", "backup", "migration", "restore", "install", "settings" };

    private static readonly string[] FullGlyphs =
        { "", "", "", "", "", "", "" };

    private static string TestBinModulesRoot => Path.Combine(AppContext.BaseDirectory, "Modules");

    [Fact] // t1 — full-set pin (replaces the old StaticModuleCatalog pin): default-rooted discovery.
    public void Default_rooted_catalog_yields_pinned_ids_in_order_with_existing_glyphs()
    {
        var catalog = new DirectoryModuleCatalog();

        IReadOnlyList<IWckModule> modules = catalog.LoadModules();

        Assert.Equal(FullOrderedIds, modules.Select(m => m.Id).ToArray());
        Assert.Equal(FullGlyphs, modules.Select(m => m.IconKey).ToArray());
        Assert.Empty(catalog.Diagnostics); // a clean install skips nothing
    }

    [Fact] // t2 — a module whose folder is absent is simply not present, no throw.
    public void Missing_module_folder_drops_only_that_module_and_never_deep_links_to_it()
    {
        using var ws = new TempWorkspace("wck-modcat-");
        string root = BuildFixture(ws, omitFolders: "migration");

        IReadOnlyList<IWckModule> modules = new DirectoryModuleCatalog(root).LoadModules();

        Assert.Equal(
            new[] { "uninstall", "clean", "backup", "restore", "install", "settings" },
            modules.Select(m => m.Id).ToArray());
        Assert.DoesNotContain(modules, m => m.Id == "migration");

        using ServiceProvider provider = BuildProvider(modules);
        var vm = provider.GetRequiredService<MainViewModel>();
        Assert.False(vm.SelectNavByKey("migration"));
        Assert.DoesNotContain(vm.Nav, item => item.Id == "migration");
    }

    [Fact] // t3 — structural floor: the catalog never returns empty; the shell stays safe.
    public void Missing_or_empty_root_still_yields_settings_only_and_a_safe_shell()
    {
        string nonexistent = Path.Combine(Path.GetTempPath(), "wck-modcat-nonexistent-" + Guid.NewGuid().ToString("N"));
        IReadOnlyList<IWckModule> fromMissing = new DirectoryModuleCatalog(nonexistent).LoadModules();
        Assert.Equal(new[] { "settings" }, fromMissing.Select(m => m.Id).ToArray());

        using var ws = new TempWorkspace("wck-modcat-");
        string emptyRoot = ws.Combine("Modules");
        Directory.CreateDirectory(emptyRoot);
        IReadOnlyList<IWckModule> fromEmpty = new DirectoryModuleCatalog(emptyRoot).LoadModules();
        Assert.Equal(new[] { "settings" }, fromEmpty.Select(m => m.Id).ToArray());

        using ServiceProvider provider = BuildProvider(fromEmpty);
        var vm = provider.GetRequiredService<MainViewModel>();
        NavItem onlyNav = Assert.Single(vm.Nav);
        Assert.Equal("settings", onlyNav.Id);
        Assert.NotNull(vm.CurrentContent);
        Assert.Null(Record.Exception(() => vm.OnShellStartup()));
    }

    [Fact] // t4 — every kind of bad folder is skipped with a diagnostic; valid modules still load.
    public void Corrupt_id_mismatched_and_non_module_folders_are_skipped_with_diagnostics()
    {
        using var ws = new TempWorkspace("wck-modcat-");
        string root = BuildFixture(ws);

        // (a) garbage bytes named like a module — BadImageFormat on load.
        string junkDir = Directory.CreateDirectory(Path.Combine(root, "junk")).FullName;
        File.WriteAllBytes(Path.Combine(junkDir, "Suite.Module.Junk.dll"), new byte[] { 0x4D, 0x5A, 0x00, 0x01, 0x02, 0x03 });

        // (b) a REAL module DLL whose id ("uninstall") does not match its folder ("uninstall2") — an
        //     impersonation attempt at another nav slot.
        string u2Dir = Directory.CreateDirectory(Path.Combine(root, "uninstall2")).FullName;
        File.Copy(Path.Combine(TestBinModulesRoot, "uninstall", "Suite.Module.Uninstall.dll"),
                  Path.Combine(u2Dir, "Suite.Module.Uninstall2.dll"));

        // (c) a valid assembly whose matching-name DLL contains NO IWckModule (the Recipes private dep).
        string recipeDir = Directory.CreateDirectory(Path.Combine(root, "recipesonly")).FullName;
        File.Copy(Path.Combine(TestBinModulesRoot, "migration", "Suite.Module.Migration.Recipes.dll"),
                  Path.Combine(recipeDir, "Suite.Module.Recipesonly.dll"));

        var catalog = new DirectoryModuleCatalog(root);
        IReadOnlyList<IWckModule> modules = catalog.LoadModules();

        Assert.Equal(FullOrderedIds, modules.Select(m => m.Id).ToArray()); // the six real modules + settings
        Assert.Contains(catalog.Diagnostics, d => d.Contains("junk"));
        Assert.Contains(catalog.Diagnostics, d => d.Contains("uninstall2"));
        Assert.Contains(catalog.Diagnostics, d => d.Contains("recipesonly"));
    }

    [Fact] // t5 — output is (Order, Id)-sorted, independent of filesystem enumeration order.
    public void Discovered_modules_are_sorted_by_order_then_id_not_by_folder_name()
    {
        using var ws = new TempWorkspace("wck-modcat-");
        string root = BuildFixture(ws);

        string[] ids = new DirectoryModuleCatalog(root).LoadModules().Select(m => m.Id).ToArray();

        Assert.Equal(FullOrderedIds, ids);
        // Alphabetical (the usual directory-enumeration order) would be a DIFFERENT sequence, proving the
        // catalog re-sorts by Order rather than passing through filesystem order.
        string[] alphabetical = new[] { "backup", "clean", "install", "migration", "restore", "uninstall", "settings" };
        Assert.NotEqual(alphabetical, ids);
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
        WpfApp.AddBaseServices(services, Array.Empty<string>());
        foreach (IWckModule module in modules)
            module.RegisterServices(services);
        services.AddSingleton(modules);
        return services.BuildServiceProvider();
    }
}
