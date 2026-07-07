using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using WindowsCareKit.App;
using WindowsCareKit.App.Execution;
using WindowsCareKit.App.Controls;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.App.Theming;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.App.Views;
using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Modules.Clean;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Tests.Execution;
using WindowsCareKit.Tests.MigrationRestore;
using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

public sealed class ViewRenderSmokeTests
{
    private static readonly Regex UnsafeI18nIndexerMode =
        new(@"\bMode\s*=\s*(TwoWay|OneWayToSource)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ExplicitOneWayMode =
        new(@"\bMode\s*=\s*OneWay\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    [Fact]
    public void SettingsView_renders_without_binding_errors()
    {
        RunOnStaThread(() =>
        {
            Assert.Equal(ApartmentState.STA, Thread.CurrentThread.GetApartmentState());

            bool createdApplication = Application.Current is null;
            Application application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var theme = LoadStrongboxTheme();
            application.Resources.MergedDictionaries.Add(theme);

            try
            {
                var i18n = new I18n(Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "lang"));
                i18n.Load("en");

                var view = new SettingsView
                {
                    DataContext = new SettingsViewModel(i18n, new FakeThemeService())
                };

                var host = new ContentControl
                {
                    Content = view,
                    Width = 1000,
                    Height = 800
                };
                host.Resources.MergedDictionaries.Add(LoadStrongboxTheme());

                var size = new Size(1000, 800);
                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                view.Measure(size);
                view.Arrange(new Rect(size));
                view.UpdateLayout();
            }
            finally
            {
                application.Resources.MergedDictionaries.Remove(theme);
                if (createdApplication)
                    application.Shutdown();
            }
        });
    }

    /// <summary>UI rollout (2026-07): Settings was reskinned to the emerald sectioned-card language. The Fact
    /// above only ever rendered Strongbox — this closes the Daylight gap for the same view/VM pairing.</summary>
    [Fact]
    public void SettingsView_renders_without_binding_errors_in_daylight()
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources("Daylight", out ResourceDictionary theme);
            try
            {
                var i18n = new I18n(Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "lang"));
                i18n.Load("en");

                var view = new SettingsView { DataContext = new SettingsViewModel(i18n, new FakeThemeService()) };
                var host = new ContentControl { Content = view, Width = 1000, Height = 800 };
                var size = new Size(1000, 800);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>UI rollout (2026-07): Clean's four section cards + the shared PlanRowTemplate were reskinned to
    /// the emerald evidence-row language. Seed one junk candidate + one startup entry so BOTH the empty-state
    /// AND the populated PlanRow branch render (the honesty-critical "undo: None" elevation lives in the row
    /// template, so an empty list alone would not exercise it).</summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void CleanView_renders_junk_and_startup_rows_in_theme(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                var i18n = new I18n(Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "lang"));
                i18n.Load("en");
                using var fx = new ExecutorFixture();
                var vm = new CleanViewModel(
                    i18n,
                    new RenderFakeJunkProbe(new JunkCandidate(@"C:\Users\alice\AppData\Local\Temp", 1024, "Temp files")),
                    new RenderFakeStartupProbe(new StartupEntry("Updater", @"C:\Program Files\App\updater.exe", StartupSource.HkcuRun, null)),
                    new RenderFakeBrowserExtensionInventory(),
                    new RenderFakeRecycleBinService(new RecycleBinStats(3, 2048)),
                    new RenderFakeFolderOpener(),
                    fx.Gate,
                    new RenderPlanExecutor(fx.Executor));
                vm.ScanJunkCommand.Execute(null);
                vm.LoadStartupCommand.Execute(null);

                var view = new CleanView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1100, Height = 900 };
                var size = new Size(1100, 900);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>UI rollout (2026-07): Install's shared PlanRowTemplate + Sign-in-status rows were reskinned.
    /// Seed one entry through LoadManifest+BuildPlan so the populated dry-run row renders, not just the
    /// empty-state.</summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void InstallView_renders_plan_rows_in_theme(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                var i18n = new I18n(Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "lang"));
                i18n.Load("en");
                using var fx = new ExecutorFixture();
                var entries = new[]
                {
                    new InstallEntry("git", "install", "dev", InstallMethod.Winget, "Git.Git", null, false, false, 100, "Install git"),
                };
                var loader = new RenderFakeManifestLoader(entries);
                var planner = new InstallPlanner(fx.Gate, new RenderAllNetDriverGuard());
                var runner = new InstallRunner(new RenderThrowingPlanWriter(), new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));
                var vm = new InstallViewModel(
                    i18n, loader, planner, new RenderFakeAuthProbe(), new RenderRecordingStateStore(), fx.Gate, new RenderPlanExecutor(fx.Executor), runner);
                vm.LoadManifestCommand.Execute(null);
                vm.BuildPlanCommand.Execute(null);

                var view = new InstallView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1100, Height = 900 };
                var size = new Size(1100, 900);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>UI rollout (2026-07): Migration's promise cards, scan card, legend pills, and capture
    /// PlanRowTemplate were reskinned. Constructs the VM with read-only fakes and renders the empty (pre-scan)
    /// state in both themes — no scan is triggered, so no real registry/profile/disk is touched.</summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void MigrationView_renders_empty_state_in_theme(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                var i18n = new I18n(Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "lang"));
                i18n.Load("en");
                var vm = new MigrationViewModel(
                    i18n,
                    new RenderFakeMigrationScanService(),
                    new RenderFakeMigrationBackupRunner(),
                    () => Array.Empty<MigrationRecipe>());

                var view = new MigrationView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1100, Height = 900 };
                var size = new Size(1100, 900);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>UI rollout (2026-07): Restore's shared PlanRowTemplate + dispositions/undo cards were reskinned.
    /// Constructs the VM over a real <see cref="MigrationRestoreService"/> (host-safe fakes/temp gate from the
    /// Slice-2 restore fixtures) and renders the empty (pre-load) state in both themes.</summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void RestoreView_renders_empty_state_in_theme(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                var i18n = new I18n(Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "lang"));
                i18n.Load("en");
                const string profile = @"C:\Users\render-smoke";
                const string usersRoot = @"C:\Users";
                var gate = MigrationRestoreTestData.GateForProfile(profile, usersRoot);
                var runner = new MigrationRestoreRunner(
                    new RecipePathResolver(new ProfileRoots(
                        profile,
                        profile + @"\AppData\Roaming",
                        profile + @"\AppData\Local")),
                    gate);
                var service = new MigrationRestoreService(runner, MigrationRestoreTestData.Executor(gate), new RestoreStateStore());
                var vm = new RestoreViewModel(i18n, service, new MigrationRestoreManifestStore(), new RestoreStateStore());

                var view = new RestoreView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1100, Height = 900 };
                var size = new Size(1100, 900);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>UI rollout (2026-07): the shared <see cref="ConfirmGate"/> overlay (used by Uninstall today, and
    /// the same control every module's future gated flow reuses) was reskinned to the emerald tier-banner
    /// language. Opens the Irreversible tier — the most visually complex branch: loud-red banner, the
    /// type-to-confirm ceremony box, AND a populated evidence row — so all three render in both themes.</summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void ConfirmGate_renders_irreversible_tier_in_theme(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                var i18n = new I18n(Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "lang"));
                i18n.Load("en");
                var gate = new ConfirmGateViewModel(i18n, onApprove: () => { }, onCancel: () => { }, isBusy: () => false);
                var row = new PlanRow
                {
                    Text = "Delete: C:\\Users\\alice\\AppData\\Roaming\\Tool\\cache",
                    RiskText = "Critical",
                    RiskBrush = RiskVisuals.For(RiskLevel.Critical),
                    Undo = "undo: None",
                    Detail = "leftover cache, program-owned",
                };
                gate.Open(ConfirmTier.Irreversible, "Confirm — this will make changes", "Review the exact actions below.", [row]);

                var view = new ConfirmGate { DataContext = gate };
                var host = new ContentControl { Content = view, Width = 900, Height = 800 };
                var size = new Size(900, 800);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                Assert.True(gate.IsOpen);
                Assert.True(gate.IsIrreversibleTier);
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    [Fact]
    public void Main_shell_nav_keeps_settings_and_restore_descriptor_inside_1030_width()
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(out ResourceDictionary theme);
            try
            {
                var i18n = new I18n(Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "lang"));
                i18n.Load("en");
                var vm = new ShellProbeViewModel(i18n);
                var window = new MainWindow { DataContext = vm, Width = 1030, Height = 720 };
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                root.DataContext = vm;
                var size = new Size(1030, 720);

                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();

                ListBox nav = Descendants<ListBox>(root).Single(lb => lb.Items.Count == vm.Nav.Count);
                nav.UpdateLayout();
                var settingsItem = Assert.IsType<ListBoxItem>(nav.ItemContainerGenerator.ContainerFromItem(vm.Nav.Last()));
                TextBlock settingsText = Descendants<TextBlock>(settingsItem)
                    .Single(t => t.Text == i18n["nav.settings"]);
                TextBlock restoreDescriptor = Descendants<TextBlock>(nav)
                    .Single(t => t.Text == i18n["nav.restore.desc"]);

                AssertInside(root, settingsItem, size.Width, "Settings nav item");
                AssertInside(root, settingsText, size.Width, "Settings label");
                AssertInside(root, restoreDescriptor, size.Width, "Restore descriptor");
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    [Fact]
    public void UninstallView_search_and_column_headers_render_from_i18n()
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(out ResourceDictionary theme);
            try
            {
                var i18n = new I18n(Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "lang"));
                i18n.Load("en");
                var vm = new UninstallViewModel(
                    i18n,
                    new FakeInstalledAppReader(),
                    new FakeAppxReader(),
                    TestData.Gate(),
                    new FakeLeftoverProbe(),
                    new FakeExecutor(),
                    new FakeAppxRemover(),
                    new FakeFolderOpener());
                var view = new UninstallView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1000, Height = 720 };
                var size = new Size(1000, 720);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                TextBox search = Descendants<TextBox>(view)
                    .Single(tb => Equals(tb.Tag, i18n["common.search"]));
                Assert.Equal(HorizontalAlignment.Stretch, search.HorizontalAlignment);
                Assert.True(search.ActualWidth > 200, $"search width was {search.ActualWidth}");

                DataGrid grid = Descendants<DataGrid>(view).Single(g => g.Name == "AppsGrid");
                Assert.Equal(
                    ["Name", "Publisher", "Size", "Installed", "Version", "Status"],
                    grid.Columns.Skip(1).Select(c => c.Header?.ToString() ?? string.Empty).ToArray());

                i18n.Load("tr");
                host.UpdateLayout();
                Assert.Equal(
                    ["Ad", "Yayıncı", "Boyut", "Yükleme", "Sürüm", "Durum"],
                    grid.Columns.Skip(1).Select(c => c.Header?.ToString() ?? string.Empty).ToArray());
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>UI rollout (2026-07): the Uninstall grid + right-rail detail pane were reskinned to the emerald
    /// evidence-row language (Backup.* tokens). Render-gate BOTH themes with a real selection so the detail
    /// pane's populated branch (not just the empty prompt) is measured/arranged without a binding crash.</summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void UninstallView_renders_grid_and_detail_pane_in_theme(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                var i18n = new I18n(Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "lang"));
                i18n.Load("en");
                var vm = new UninstallViewModel(
                    i18n,
                    new FakeInstalledAppReader(TestData.App("Sample App")),
                    new FakeAppxReader(),
                    TestData.Gate(),
                    new FakeLeftoverProbe(),
                    new FakeExecutor(),
                    new FakeAppxRemover(),
                    new FakeFolderOpener());
                var view = new UninstallView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1000, Height = 720 };
                var size = new Size(1000, 720);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                vm.SelectedRow = vm.AppsView.Cast<AppRow>().FirstOrDefault();
                host.UpdateLayout();
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>UI rollout (2026-07): the desktop-app wizard overlay was reskinned to the same Backup.*
    /// evidence-row language as Uninstall/Backup. Seed a ProgramOwned leftover so the populated row template,
    /// sub-tab button state, and centered wizard chrome render in both themes.</summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void UninstallWizardView_renders_seeded_leftover_rows_in_theme(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                var i18n = new I18n(Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "lang"));
                i18n.Load("en");
                var probe = new FakeLeftoverProbe();
                probe.RegistryKeys.Add(new LeftoverRegistryKey(
                    RegistryHive.LocalMachine,
                    @"SOFTWARE\SomeVendor\SomeApp",
                    RegistryView.Registry64,
                    "render-owned registry key"));
                var vm = new UninstallWizardViewModel(
                    i18n,
                    TestData.Gate(),
                    probe,
                    new FakeExecutor(),
                    () => new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
                vm.Open(TestData.App(
                    displayName: "SomeApp",
                    publisher: "SomeVendor",
                    source: InstalledAppSource.MachineWide64,
                    uninstall: "\"C:\\Program Files\\SomeApp\\uninst.exe\" /S",
                    installLocation: @"C:\Program Files\SomeApp"));
                vm.SkipToScanCommand.Execute(null);
                Assert.True(SpinWait.SpinUntil(() => vm.IsLeftoversBeat, TimeSpan.FromSeconds(5)));

                var view = new UninstallWizardView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1000, Height = 760 };
                var size = new Size(1000, 760);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                Assert.True(vm.IsOpen);
                Assert.Single(vm.RegistryNodes);
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void BackupView_renders_plan_surface_in_theme(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                var i18n = new I18n(Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "lang"));
                i18n.Load("en");
                BackupViewModel vm = BuildBackupRenderViewModel(i18n);
                vm.BuildPlanAsync().GetAwaiter().GetResult();

                var view = new BackupView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1000, Height = 720 };
                var size = new Size(1000, 720);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                Assert.True(vm.HasPlan);
                Assert.Single(vm.PlanRows);
                Assert.Single(vm.ManualRows);
                Assert.Single(vm.SkippedRows);
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    [Fact]
    public void No_view_binds_TwoWay_to_the_I18n_indexer()
    {
        string[] xamlFiles = Directory.EnumerateFiles(ViewsPath, "*.xaml", SearchOption.TopDirectoryOnly)
            .Concat(Directory.EnumerateFiles(CleanModuleViewsPath, "*.xaml", SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(InstallModuleViewsPath, "*.xaml", SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(RestoreModuleViewsPath, "*.xaml", SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(BackupModuleViewsPath, "*.xaml", SearchOption.TopDirectoryOnly))
            .Concat(Directory.EnumerateFiles(UninstallModuleViewsPath, "*.xaml", SearchOption.TopDirectoryOnly))
            .Append(MainWindowPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals("SettingsView.xaml", StringComparison.Ordinal));
        Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals("CleanView.xaml", StringComparison.Ordinal));
        Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals("InstallView.xaml", StringComparison.Ordinal));
        Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals("RestoreView.xaml", StringComparison.Ordinal));
        Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals("BackupView.xaml", StringComparison.Ordinal));
        Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals("UninstallView.xaml", StringComparison.Ordinal));
        Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals("UninstallWizardView.xaml", StringComparison.Ordinal));
        Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals("MainWindow.xaml", StringComparison.Ordinal));

        var failures = new List<string>();
        foreach (string file in xamlFiles)
            CollectI18nIndexerBindingFailures(file, failures);

        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }

    private static void CollectI18nIndexerBindingFailures(string file, List<string> failures)
    {
        XDocument document = XDocument.Load(file, LoadOptions.SetLineInfo);
        foreach (XElement element in document.Descendants())
        {
            foreach (XAttribute attribute in element.Attributes())
            {
                if (!BindsI18nIndexer(attribute.Value))
                    continue;

                string location = FormatLocation(file, attribute);
                if (UnsafeI18nIndexerMode.IsMatch(attribute.Value))
                    failures.Add($"{location}: I18n indexer binding must not use TwoWay or OneWayToSource.");

                if (element.Name.LocalName == "Run" &&
                    attribute.Name.LocalName == "Text" &&
                    !ExplicitOneWayMode.IsMatch(attribute.Value))
                {
                    failures.Add($"{location}: Run.Text binding to the I18n indexer must specify Mode=OneWay.");
                }
            }
        }
    }

    private static bool BindsI18nIndexer(string value)
        => value.Contains("{Binding", StringComparison.Ordinal) &&
           value.Contains("I18n[", StringComparison.Ordinal);

    private static string FormatLocation(string file, XAttribute attribute)
    {
        string relative = Path.GetRelativePath(RepoRoot, file);
        var lineInfo = (IXmlLineInfo)attribute;
        return lineInfo.HasLineInfo()
            ? $"{relative}:{lineInfo.LineNumber}"
            : relative;
    }

    private static ResourceDictionary LoadStrongboxTheme()
        => LoadTheme("Strongbox");

    private static ResourceDictionary LoadTheme(string themeName)
        => new()
        {
            Source = new Uri(
                $"pack://application:,,,/WindowsCareKit;component/Themes/{themeName}.xaml",
                UriKind.Absolute)
        };

    private static bool EnsureApplicationResources(out ResourceDictionary theme)
        => EnsureApplicationResources("Strongbox", out theme);

    private static bool EnsureApplicationResources(string themeName, out ResourceDictionary theme)
    {
        bool createdApplication = Application.Current is null;
        Application application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        theme = LoadTheme(themeName);
        application.Resources.MergedDictionaries.Add(theme);
        application.Resources["BoolToVis"] = new BooleanToVisibilityConverter();
        application.Resources["ZeroToVis"] = new ZeroToVisibleConverter();
        application.Resources["PositiveToVis"] = new PositiveToVisibleConverter();
        application.Resources["InverseBoolToVis"] = new InverseBoolToVisibilityConverter();
        return createdApplication;
    }

    private static BackupViewModel BuildBackupRenderViewModel(I18n i18n)
    {
        var gate = TestData.Gate();
        var planner = new BackupPlanner(gate, new FakeEnvironmentExpander());
        var runner = new BackupRunner(
            new NoOpBackupExecutor(),
            new BackupIntegrityWriter(),
            new BackupReportWriter(new LogRedactor(null, null)),
            gate,
            new FakeFileSystem(),
            new FakeHasher(),
            new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        return new BackupViewModel(i18n, new BackupRenderManifestLoader(), planner, runner)
        {
            PayloadDir = @"D:\WCK-BackupOut"
        };
    }

    private static BackupEntry BackupCopyEntry(string id, string source, string target)
        => new(id, true, BackupMethod.Copy, "test", source, target,
            Array.Empty<string>(), SecretHandling.Normal, 50, "merge-after-install", $"{id}: copy configured files", null);

    private sealed class BackupRenderManifestLoader : IManifestLoader
    {
        public BackupManifest LoadFromDirectory(string manifestsDirectory) => Load();
        public BackupManifest LoadFromJson(IEnumerable<string> jsonDocuments) => Load();

        private static BackupManifest Load() => new(new[]
        {
            BackupCopyEntry("copy-one", @"C:\Users\alice\AppData\Roaming\Tool\settings.json", "tool/settings.json"),
            new BackupEntry("manual-one", true, BackupMethod.Copy, "browser", @"C:\Users\alice\AppData\Local\Browser\Login Data",
                "browser/login-data", Array.Empty<string>(), SecretHandling.NeverRead, 70, "manual",
                "Browser passwords and sign-in", "DPAPI machine-bound; export or sign in again before formatting."),
            new BackupEntry("skipped-one", false, BackupMethod.Copy, "cache", @"C:\Users\alice\AppData\Local\Tool\Cache",
                "tool/cache", Array.Empty<string>(), SecretHandling.Normal, 80, "skip",
                "Cache and tokens", null),
        });
    }

    private sealed class NoOpBackupExecutor : IBackupExecutor
    {
        public BackupExecutionReport Execute(OperationPlan plan, string approvedPlanHash)
            => new(false, Array.Empty<BackupActionResult>());
    }

    private static void CleanupApplicationResources(bool shutdownApplication, ResourceDictionary theme)
    {
        Application.Current?.Resources.MergedDictionaries.Remove(theme);
        if (shutdownApplication)
            Application.Current?.Shutdown();
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, i);
            if (child is T typed)
                yield return typed;
            foreach (T descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private static void AssertInside(FrameworkElement ancestor, FrameworkElement element, double maxWidth, string label)
    {
        Rect bounds = element.TransformToAncestor(ancestor)
            .TransformBounds(new Rect(new Point(0, 0), element.RenderSize));
        Assert.True(bounds.Left >= -0.5, $"{label} left edge was clipped: {bounds}");
        Assert.True(bounds.Right <= maxWidth + 0.5, $"{label} right edge was clipped: {bounds}");
        Assert.True(element.ActualWidth > 0, $"{label} had no rendered width.");
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static string ViewsPath => Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "Views");
    private static string CleanModuleViewsPath => Path.Combine(RepoRoot, "src", "Suite.Module.Clean", "Views");
    private static string InstallModuleViewsPath => Path.Combine(RepoRoot, "src", "Suite.Module.Install", "Views");
    private static string RestoreModuleViewsPath => Path.Combine(RepoRoot, "src", "Suite.Module.Restore", "Views");
    private static string BackupModuleViewsPath => Path.Combine(RepoRoot, "src", "Suite.Module.Backup", "Views");
    private static string UninstallModuleViewsPath => Path.Combine(RepoRoot, "src", "Suite.Module.Uninstall", "Views");
    private static string MainWindowPath => Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "MainWindow.xaml");

    private static string RepoRoot
    {
        get
        {
            DirectoryInfo? dir = new(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WindowsCareKit.slnx")))
                dir = dir.Parent;

            return dir?.FullName ?? throw new InvalidOperationException("Could not locate repository root.");
        }
    }

    private sealed class FakeThemeService : IThemeService
    {
        public IReadOnlyList<AppTheme> AvailableThemes { get; } = new[] { AppTheme.Dark, AppTheme.Light };
        public AppTheme SelectedTheme { get; private set; } = AppTheme.Dark;
        public AppTheme AppliedTheme { get; } = AppTheme.Dark;
        public bool RestartRequired => SelectedTheme != AppliedTheme;

        public bool TrySelectTheme(AppTheme theme)
        {
            SelectedTheme = theme;
            return true;
        }
    }

    private sealed class ShellProbeViewModel
    {
        public ShellProbeViewModel(I18n i18n)
        {
            I18n = i18n;
            Nav =
            [
                new(i18n, "nav.uninstall", "", new object(), "nav.uninstall.desc"),
                new(i18n, "nav.clean", "", new object(), "nav.clean.desc"),
                new(i18n, "nav.backup", "", new object(), "nav.backup.desc"),
                new(i18n, "nav.migration", "", new object(), "nav.migration.desc"),
                new(i18n, "nav.restore", "", new object(), "nav.restore.desc"),
                new(i18n, "nav.install", "", new object(), "nav.install.desc"),
                new(i18n, "nav.settings", "", new object(), "nav.settings.desc", isSettings: true),
            ];
            SelectedNav = Nav[0];
            DismissFirstRunCommand = new RelayCommand(() => ShowFirstRun = false);
        }

        public I18n I18n { get; }
        public IReadOnlyList<NavItem> Nav { get; }
        public NavItem SelectedNav { get; set; }
        public object CurrentContent => SelectedNav.Content;
        public bool ShowFirstRun { get; set; }
        public ICommand DismissFirstRunCommand { get; }
    }

    private sealed class FakeInstalledAppReader(params InstalledApp[] apps) : IInstalledAppReader
    {
        public IReadOnlyList<InstalledApp> ReadAll() => apps;
    }

    private sealed class FakeAppxReader : IAppxReader
    {
        public IReadOnlyList<InstalledAppx> ReadCurrentUserPackages() => Array.Empty<InstalledAppx>();
    }

    private sealed class FakeExecutor : IExecutor
    {
        public ExecutionOutcome Execute(OperationPlan plan, string approvedPlanHash) => new(true, "not used");
    }

    private sealed class FakeAppxRemover : IAppxRemover
    {
        public Task<AppxRemovalResult> RemoveCurrentUserAsync(InstalledAppx package, CancellationToken ct = default)
            => Task.FromResult(new AppxRemovalResult(true, "not used"));
    }

    private sealed class FakeFolderOpener : IFolderOpener
    {
        public void OpenFolder(string path) { }
    }

    // ===== Render-only fakes for the CleanView / InstallView / MigrationView render-smoke cases. None of these
    // are wired to any real filesystem/registry/process — they exist purely so the corresponding ViewModel can
    // be constructed and its dry-run PlanRows populated for a render pass. =====

    private sealed class RenderFakeJunkProbe(params JunkCandidate[] candidates) : IJunkProbe
    {
        public IReadOnlyList<JunkCandidate> FindJunk() => candidates;
    }

    private sealed class RenderFakeStartupProbe(params StartupEntry[] entries) : IStartupProbe
    {
        public IReadOnlyList<StartupEntry> ReadAll() => entries;
    }

    private sealed class RenderFakeBrowserExtensionInventory : IBrowserExtensionInventory
    {
        public IReadOnlyList<BrowserExtension> ReadAll() => Array.Empty<BrowserExtension>();
    }

    private sealed class RenderFakeRecycleBinService(RecycleBinStats stats) : IRecycleBinService
    {
        public RecycleBinStats Query() => stats;
    }

    private sealed class RenderFakeFolderOpener : IFolderOpener
    {
        public void OpenFolder(string path) { }
    }

    private sealed class RenderPlanExecutor(GatedExecutor executor) : IPlanExecutor
    {
        public PlanExecutionReport ExecuteWithReport(OperationPlan plan, string approvedPlanHash)
        {
            ExecutionReport report = executor.ExecuteWithReport(plan, approvedPlanHash);
            return new PlanExecutionReport(
                report.Authorized,
                report.PlanHash,
                report.Results
                    .Select(r => new PlanActionResult(r.ActionId, r.Kind, MapStatus(r.Status), r.Detail))
                    .ToArray());
        }

        private static PlanActionStatus MapStatus(ActionStatus status) => status switch
        {
            ActionStatus.Done => PlanActionStatus.Done,
            ActionStatus.Skipped => PlanActionStatus.Skipped,
            ActionStatus.Blocked => PlanActionStatus.Blocked,
            ActionStatus.Failed => PlanActionStatus.Failed,
            ActionStatus.NotRun => PlanActionStatus.NotRun,
            _ => PlanActionStatus.Failed,
        };
    }

    private sealed class RenderFakeManifestLoader(params InstallEntry[] entries) : IInstallManifestLoader
    {
        public InstallManifest Load(string manifestPath) => new(entries);
        public InstallManifest Parse(string json) => new(entries);
    }

    private sealed class RenderAllNetDriverGuard : IDriverGuard
    {
        public bool IsNetClass(string driverIdentifier) => true;
    }

    /// <summary>Never called by LoadManifest/BuildPlan — only guards that ExportPlan (unused by this render test)
    /// would be the sole caller in production.</summary>
    private sealed class RenderThrowingPlanWriter : IInstallPlanWriter
    {
        public string WriteExport(InstallPlanExportDoc doc, string payloadRoot, ISafetyGate gate)
            => throw new InvalidOperationException("ExportPlan must not be invoked by the render-smoke test.");
    }

    private sealed class RenderFakeAuthProbe : IAuthProbe
    {
        public bool Exists(string path) => false;
    }

    private sealed class RenderRecordingStateStore : IRestoreStateStore
    {
        private readonly Dictionary<string, RestoreState> _byDir = new(StringComparer.OrdinalIgnoreCase);

        public RestoreState Load(string stateDirectory)
            => _byDir.TryGetValue(stateDirectory, out RestoreState? s) ? s : RestoreState.Empty;

        public void Save(string stateDirectory, RestoreState state) => _byDir[stateDirectory] = state;

        public string PathFor(string stateDirectory) => Path.Combine(stateDirectory, ".kurulum_state.json");
    }

    /// <summary>Never invoked by the render test (no scan is triggered) — exists only so
    /// <see cref="MigrationViewModel"/> can be constructed.</summary>
    private sealed class RenderFakeMigrationScanService : IMigrationScanService
    {
        public MigrationScanResult Scan(CancellationToken cancellationToken = default)
            => throw new InvalidOperationException("Scan must not be invoked by the render-smoke test.");
    }

    /// <summary>Never invoked by the render test (no capture is triggered) — exists only so
    /// <see cref="MigrationViewModel"/> can be constructed.</summary>
    private sealed class RenderFakeMigrationBackupRunner : IMigrationBackupRunner
    {
        public MigrationBackupPlanResult BuildPlan(IEnumerable<MigrationRecipe> recipes, string packageDir, DateTime utc)
            => throw new InvalidOperationException("BuildPlan must not be invoked by the render-smoke test.");

        public MigrationBackupRunResult Run(MigrationBackupPlanResult plan, string approvedPlanHash, string packageDir)
            => throw new InvalidOperationException("Run must not be invoked by the render-smoke test.");
    }
}
