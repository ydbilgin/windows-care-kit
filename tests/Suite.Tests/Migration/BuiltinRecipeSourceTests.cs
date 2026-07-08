using System.Reflection;
using WindowsCareKit.Core.Modules.Migration;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

/// <summary>C: the embedded seed recipes load + strictly validate; the Slice-1 set is exactly the agreed apps.</summary>
public class BuiltinRecipeSourceTests
{
    [Fact]
    public void All_seed_recipes_load_and_validate()
    {
        IReadOnlyList<MigrationRecipe> recipes = BuiltinRecipeSource.LoadAll();
        Assert.Equal(40, recipes.Count);
        Assert.All(recipes, r =>
        {
            Assert.Equal(3, r.SchemaVersion);
            Assert.NotEmpty(r.Id);
            Assert.NotEmpty(r.Items);
            Assert.NotNull(r.MigrationMeta?.UiWarning);
            Assert.Equal(UpstreamDataLicense.None, r.UpstreamDataLicense);
        });
    }

    [Fact]
    public void Slice1_seed_set_contains_required_common_apps()
    {
        var ids = BuiltinRecipeSource.LoadAll().Select(r => r.Id).ToHashSet();
        Assert.Contains("anthropic.claude-code", ids);
        Assert.Contains("discord", ids);
        Assert.Contains("microsoft.vscode", ids);
        Assert.Contains("git.config", ids);
        Assert.Contains("google.chrome", ids);
        Assert.Contains("microsoft.edge", ids);
        Assert.Contains("valve.steam", ids);
        Assert.Contains("7zip.7zip", ids);
        Assert.Contains("notepadplusplus.notepadplusplus", ids);
        Assert.Contains("telegram.telegramdesktop", ids);
        Assert.Contains("qbittorrent.qbittorrent", ids);
        Assert.Contains("obsproject.obsstudio", ids);
        Assert.Contains("spotify.spotify", ids);
        Assert.Contains("libreoffice.libreoffice", ids);
        Assert.Contains("mozilla.firefox", ids);
        Assert.Contains("jetbrains.rider", ids);
        Assert.Contains("python.python.3.14", ids);
        Assert.Contains("openjs.nodejs.lts", ids);
        Assert.Contains("videolan.vlc", ids);
        Assert.Contains("slacktechnologies.slack", ids);
        Assert.Contains("zoom.zoom", ids);
        Assert.Contains("microsoft.powertoys", ids);
        Assert.Contains("postman.postman", ids);
        Assert.Contains("whatsapp.whatsapp", ids);
    }

    [Fact]
    public void Ssh_recipe_is_deferred_not_shipped()
    {
        var ids = BuiltinRecipeSource.LoadAll().Select(r => r.Id).ToList();
        Assert.DoesNotContain(ids, id => id.Contains("ssh", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Claude_recipe_excludes_cache_shell_snapshots_and_todos()
    {
        MigrationRecipe claude = BuiltinRecipeSource.LoadAll().Single(r => r.Id == "anthropic.claude-code");
        Assert.Contains(claude.Exclude, e => e.Contains("shell-snapshots"));
        Assert.Contains(claude.Exclude, e => e.Contains("todos"));
        Assert.Contains(claude.Exclude, e => e.Contains("cache", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Discord_excludes_cache_and_blob_storage()
    {
        MigrationRecipe discord = BuiltinRecipeSource.LoadAll().Single(r => r.Id == "discord");
        Assert.Contains(discord.Exclude, e => e.Contains("Cache", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(discord.Exclude, e => e.Contains("blob_storage"));
    }

    [Fact]
    public void Loading_is_deterministically_ordered_by_id()
    {
        var ids = BuiltinRecipeSource.LoadAll().Select(r => r.Id).ToList();
        var sorted = ids.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(sorted, ids);
    }

    [Fact]
    public void Machine_locked_builtin_recipes_are_inventory_only_and_never_claim_works()
    {
        var recipes = BuiltinRecipeSource.LoadAll()
            .Where(r => r.PortabilityClass == PortabilityClass.MachineLocked)
            .ToList();

        Assert.NotEmpty(recipes);
        Assert.All(recipes, r =>
        {
            Assert.Equal(RestoreTier.InventoryOnly, r.RestoreTier);
            Assert.False(PortabilityBadge.Compute(r.PortabilityClass, hasPreconditions: false).MayClaimWorks);
        });
    }

    [Fact]
    public void Steam_machine_root_is_inventory_only()
    {
        MigrationRecipe steam = BuiltinRecipeSource.LoadAll().Single(r => r.Id == "valve.steam");
        Assert.Equal(RestoreTier.InventoryOnly, steam.RestoreTier);
        RecipeItem item = Assert.Single(steam.Items);
        Assert.Equal(RecipeItemKind.MachineRoot, item.Kind);
        Assert.Equal("steam", item.LibraryDetector);
    }

    [Fact]
    public void Added_catalog_recipes_are_trusted_profile_rooted_and_have_automatic_reinstall_mappings()
    {
        string[] addedIds =
        [
            "mozilla.firefox",
            "jetbrains.rider",
            "python.python.3.14",
            "openjs.nodejs.lts",
            "videolan.vlc",
            "slacktechnologies.slack",
            "zoom.zoom",
            "microsoft.powertoys",
            "postman.postman",
            "whatsapp.whatsapp",
        ];
        var added = BuiltinRecipeSource.LoadAll().Where(r => addedIds.Contains(r.Id)).ToList();

        Assert.Equal(addedIds.Length, added.Count);
        Assert.All(added, recipe =>
        {
            Assert.Equal(CatalogTier.Trusted, recipe.CatalogTier);
            Assert.Contains(recipe.Detect.KnownFolder,
                new[] { KnownFolder.UserProfile, KnownFolder.AppData, KnownFolder.LocalAppData });
            Assert.False(Path.IsPathRooted(recipe.Detect.Path));
            Assert.NotEmpty(recipe.InstallPathHint);
            Assert.NotNull(recipe.Install);
            Assert.Contains(recipe.Install!.Method,
                new[] { RecipeInstallMethod.Winget, RecipeInstallMethod.Npm });
            Assert.All(recipe.Items, item => Assert.False(Path.IsPathRooted(item.Path)));
        });
    }

    [Fact]
    public void Added_secret_bearing_apps_have_honest_non_green_warnings_and_manual_steps()
    {
        string[] secretBearingIds =
        [
            "mozilla.firefox",
            "slacktechnologies.slack",
            "zoom.zoom",
            "postman.postman",
            "whatsapp.whatsapp",
        ];
        var recipes = BuiltinRecipeSource.LoadAll().Where(r => secretBearingIds.Contains(r.Id)).ToList();

        Assert.Equal(secretBearingIds.Length, recipes.Count);
        Assert.All(recipes, recipe =>
        {
            Assert.False(PortabilityBadge.Compute(recipe.PortabilityClass, hasPreconditions: false).MayClaimWorks);
            Assert.NotEmpty(recipe.MigrationMeta!.ManualTodo);
            Assert.False(string.IsNullOrWhiteSpace(recipe.MigrationMeta.UiWarning!.En));
            Assert.False(string.IsNullOrWhiteSpace(recipe.MigrationMeta.UiWarning.Tr));
        });
    }

    [Fact]
    public void Firefox_excludes_password_store_and_whatsapp_is_inventory_only()
    {
        IReadOnlyList<MigrationRecipe> recipes = BuiltinRecipeSource.LoadAll();
        MigrationRecipe firefox = recipes.Single(r => r.Id == "mozilla.firefox");
        MigrationRecipe whatsapp = recipes.Single(r => r.Id == "whatsapp.whatsapp");

        Assert.Contains(firefox.Exclude, value => value.Contains("key4.db", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(firefox.Exclude, value => value.Contains("logins.json", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(PortabilityClass.Partial, firefox.PortabilityClass);

        Assert.Equal(PortabilityClass.MachineLocked, whatsapp.PortabilityClass);
        Assert.Equal(RestoreTier.InventoryOnly, whatsapp.RestoreTier);
    }

    [Fact]
    public void Python_recipe_keeps_per_user_install_non_elevated()
    {
        MigrationRecipe python = BuiltinRecipeSource.LoadAll().Single(r => r.Id == "python.python.3.14");

        Assert.Equal("Python.Python.3.14", python.Install!.WingetId);
        Assert.False(python.Install.RequiresAdmin);
        Assert.Equal(KnownFolder.AppData, python.Detect.KnownFolder);
    }

    [Fact]
    public void M25_catalog_expansion_contains_all_sixteen_apps_with_reinstall_mappings()
    {
        string[] ids =
        [
            "agilebits.1password",
            "anydesk.anydesk",
            "audacity.audacity",
            "bitwarden.bitwarden",
            "blenderfoundation.blender",
            "brave.brave",
            "gimp.gimp.3",
            "handbrake.handbrake",
            "inkscape.inkscape",
            "insomnia.insomnia",
            "keepassxcteam.keepassxc",
            "mozilla.thunderbird",
            "opera.opera",
            "putty.putty",
            "teamviewer.teamviewer",
            "winscp.winscp",
        ];

        var recipes = BuiltinRecipeSource.LoadAll().Where(r => ids.Contains(r.Id)).ToList();

        Assert.Equal(ids.Length, recipes.Count);
        Assert.All(recipes, recipe =>
        {
            Assert.Equal(CatalogTier.Trusted, recipe.CatalogTier);
            Assert.NotEmpty(recipe.InstallPathHint);
            Assert.NotNull(recipe.Install);
            Assert.Equal(RecipeInstallMethod.Winget, recipe.Install!.Method);
            Assert.False(string.IsNullOrWhiteSpace(recipe.WingetId));
            Assert.False(Path.IsPathRooted(recipe.Detect.Path));
            Assert.All(recipe.Items, item => Assert.False(Path.IsPathRooted(item.Path)));
            Assert.False(string.IsNullOrWhiteSpace(recipe.MigrationMeta!.UiWarning!.En));
            Assert.False(string.IsNullOrWhiteSpace(recipe.MigrationMeta.UiWarning.Tr));
        });
    }

    [Fact]
    public void Credential_and_remote_access_recipes_are_non_green_with_bilingual_manual_todos()
    {
        string[] ids =
        [
            "agilebits.1password",
            "anydesk.anydesk",
            "bitwarden.bitwarden",
            "brave.brave",
            "insomnia.insomnia",
            "keepassxcteam.keepassxc",
            "mozilla.thunderbird",
            "opera.opera",
            "putty.putty",
            "teamviewer.teamviewer",
            "winscp.winscp",
        ];

        var recipes = BuiltinRecipeSource.LoadAll().Where(r => ids.Contains(r.Id)).ToList();

        Assert.Equal(ids.Length, recipes.Count);
        Assert.All(recipes, recipe =>
        {
            Assert.False(PortabilityBadge.Compute(recipe.PortabilityClass, false).MayClaimWorks);
            Assert.Contains(recipe.MigrationMeta!.ManualTodo, value => value.StartsWith("EN:", StringComparison.Ordinal));
            Assert.Contains(recipe.MigrationMeta.ManualTodo, value => value.StartsWith("TR:", StringComparison.Ordinal));
        });
    }

    [Fact]
    public void Password_manager_and_session_recipes_exclude_secret_leaf_types()
    {
        IReadOnlyList<MigrationRecipe> recipes = BuiltinRecipeSource.LoadAll();
        MigrationRecipe keepass = recipes.Single(r => r.Id == "keepassxcteam.keepassxc");
        MigrationRecipe winscp = recipes.Single(r => r.Id == "winscp.winscp");
        MigrationRecipe putty = recipes.Single(r => r.Id == "putty.putty");

        Assert.Contains(keepass.Exclude, value => value.Contains("*.kdbx", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(keepass.Exclude, value => value.Contains("*.keyx", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(winscp.Exclude, value => value.Contains("*.ppk", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(putty.Exclude, value => value.Contains("*.ppk", StringComparison.OrdinalIgnoreCase));
        Assert.All(new[] { winscp, putty }, recipe => Assert.Equal(RestoreTier.InventoryOnly, recipe.RestoreTier));
    }

    [Fact]
    public void Opera_recipe_does_not_copy_machine_bound_secure_preferences_or_extensions()
    {
        MigrationRecipe opera = BuiltinRecipeSource.LoadAll().Single(r => r.Id == "opera.opera");
        RecipeItem item = Assert.Single(opera.Items);

        Assert.DoesNotContain(item.Include, value => value.Equals("Secure Preferences", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(item.Include, value => value.Equals("Extensions/**", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(RestoreTier.InventoryOnly, opera.RestoreTier);
    }

    [Fact]
    public void Recipes_are_module_owned_and_core_is_recipe_free()
    {
        Assembly recipesAsm = typeof(BuiltinRecipeSource).Assembly;
        Assert.Equal("Suite.Module.Migration.Recipes", recipesAsm.GetName().Name);

        string[] names = recipesAsm.GetManifestResourceNames();
        Assert.Equal(40, names.Length);
        Assert.All(names, n => Assert.StartsWith("WindowsCareKit.Module.Migration.Recipes.", n, StringComparison.Ordinal));

        // Base must carry zero embedded payloads after M2.
        Assert.Empty(typeof(MigrationRecipeLoader).Assembly.GetManifestResourceNames());
    }
}
