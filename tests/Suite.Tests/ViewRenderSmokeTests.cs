using System.Runtime.ExceptionServices;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App;
using WindowsCareKit.App.Execution;
using WindowsCareKit.App.Controls;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;
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
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Execution;
using WindowsCareKit.Core.Modules.Migration.Selection;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Module.Backup.ViewModels;
using WindowsCareKit.Module.Backup.Views;
using WindowsCareKit.Module.Clean.ViewModels;
using WindowsCareKit.Module.Clean.Views;
using WindowsCareKit.Module.Install.ViewModels;
using WindowsCareKit.Module.Install.Views;
using WindowsCareKit.Module.Migration.ViewModels;
using WindowsCareKit.Module.Migration.Views;
using WindowsCareKit.Module.Restore.ViewModels;
using WindowsCareKit.Module.Restore.Views;
using WindowsCareKit.Module.Uninstall.ViewModels;
using WindowsCareKit.Module.Uninstall.Views;
using WindowsCareKit.Tests.Execution;
using WindowsCareKit.Tests.MigrationRestore;
using WindowsCareKit.Tests.TestInfra;
using Xunit;
using Xunit.Abstractions;

namespace WindowsCareKit.Tests;

[Collection(WpfResourceCollection.Name)]
public sealed class ViewRenderSmokeTests(ITestOutputHelper output)
{
    private static readonly object BindingTraceLock = new();
    private static int BindingTraceScopes;
    private static SourceLevels BindingTracePreviousLevel;
    private static readonly Lazy<Dispatcher> RenderDispatcher = new(CreateRenderDispatcher);

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
                I18n i18n = TestI18n.Full("en");

                var view = new SettingsView
                {
                    DataContext = new SettingsViewModel(
                        i18n,
                        new FakeThemeService(),
                        new RecordingUrlOpener(),
                        TestHelpers.NoComponentsDiscovered())
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
                _ = createdApplication;
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
                I18n i18n = TestI18n.Full("en");

                var view = new SettingsView
                {
                    DataContext = new SettingsViewModel(
                        i18n,
                        new FakeThemeService(),
                        new RecordingUrlOpener(),
                        TestHelpers.NoComponentsDiscovered())
                };
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

    [Fact]
    public void SettingsView_binds_and_invokes_repository_link_command()
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources("Strongbox", out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                var opener = new RecordingUrlOpener();
                var viewModel = new SettingsViewModel(
                    i18n,
                    new FakeThemeService(),
                    opener,
                    TestHelpers.NoComponentsDiscovered());
                var view = new SettingsView { DataContext = viewModel };
                var host = new ContentControl { Content = view, Width = 1000, Height = 800 };
                var size = new Size(1000, 800);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();
                view.Measure(size);
                view.Arrange(new Rect(size));
                view.UpdateLayout();

                var link = view.FindName("RepositoryLink") as System.Windows.Documents.Hyperlink;
                Assert.NotNull(link);
                Assert.Same(viewModel.OpenExternalLinkCommand, link!.Command);
                Assert.Equal(SettingsViewModel.ProjectRepositoryUrl, link.CommandParameter);

                // The two links are symmetric in XAML, so a typo in the second one would otherwise ship unseen.
                var releasesLink = view.FindName("ReleasesLink") as System.Windows.Documents.Hyperlink;
                Assert.NotNull(releasesLink);
                Assert.Same(viewModel.OpenExternalLinkCommand, releasesLink!.Command);
                Assert.Equal(SettingsViewModel.ProjectReleasesUrl, releasesLink.CommandParameter);

                var peer = new System.Windows.Automation.Peers.HyperlinkAutomationPeer(link);
                var invokeProvider = (System.Windows.Automation.Provider.IInvokeProvider)
                    peer.GetPattern(System.Windows.Automation.Peers.PatternInterface.Invoke)!;
                invokeProvider.Invoke();

                Assert.Equal(new Uri(SettingsViewModel.ProjectRepositoryUrl, UriKind.Absolute),
                    Assert.Single(opener.Opened));
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
    /// template, so an empty list alone would not exercise it).
    /// NEW-07 MAJOR-02 fix (2026-07-23): also drives the Recycle and Extensions Complete-source commands (not
    /// just Startup) and asserts the three health-note TextBlocks are actually <see cref="Visibility.Collapsed"/>
    /// when every source is Complete — the note-free-healthy-render half of the render-boundary proof.</summary>
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
                I18n i18n = TestI18n.Full("en");
                using var fx = new ExecutorFixture();
                var completeExtensions = new BrowserExtensionListing(
                    new[] { new BrowserExtension("Edge", "Default", "abc123", "Test Extension", @"C:\ext\abc123") },
                    SourceHealth.Complete,
                    Array.Empty<InventorySourceFault>());
                var vm = new CleanViewModel(
                    i18n,
                    new RenderFakeJunkProbe(new JunkCandidate(@"C:\Users\alice\AppData\Local\Temp", 1024, "Temp files")),
                    new RenderFakeStartupProbe(new StartupEntry("Updater", @"C:\Program Files\App\updater.exe", StartupSource.HkcuRun, null)),
                    new RenderFakeBrowserExtensionInventory(completeExtensions),
                    new RenderFakeRecycleBinService(new RecycleBinStats(3, 2048)),
                    new RenderFakeFolderOpener(),
                    fx.Gate,
                    new RenderPlanExecutor(fx.Executor));
                InstallDispatcherSyncContext();
                vm.ScanJunkCommand.Execute(null);
                PumpAsyncWork(() => vm.JunkScanned && !vm.IsBusy, TimeSpan.FromSeconds(5));
                vm.LoadStartupCommand.Execute(null);
                PumpAsyncWork(() => !vm.IsBusy && vm.StartupEntries.Count > 0, TimeSpan.FromSeconds(5));
                vm.RefreshRecycleCommand.Execute(null);
                PumpAsyncWork(() => !vm.IsBusy && vm.RecycleStats.Length > 0, TimeSpan.FromSeconds(5));
                vm.LoadExtensionsCommand.Execute(null);
                PumpAsyncWork(() => !vm.IsBusy && vm.Extensions.Count > 0, TimeSpan.FromSeconds(5));

                var view = new CleanView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1100, Height = 900 };
                var size = new Size(1100, 900);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                AssertHealthNoteCollapsed(view, "RecycleHealthNoteText");
                AssertHealthNoteCollapsed(view, "StartupHealthNoteText");
                AssertHealthNoteCollapsed(view, "ExtensionsHealthNoteText");
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>NEW-07 (2026-07-23): a degraded read source must render a visible, honest, non-green caution note
    /// — "could not inspect" must not render as "found nothing". Seeds an Unavailable recycle query + a Partial
    /// startup read + a Partial extensions read, drives the three load commands, and asserts each localized health
    /// note is actually in the visual tree in BOTH themes (the render-boundary proof that the ViewModel's health
    /// state reaches the screen).
    /// NEW-07 MAJOR-02 fix (2026-07-23): locates each health-note TextBlock BY NAME (not just "any TextBlock with
    /// this text" — a permanently-Collapsed element would still satisfy a text-membership check) and asserts
    /// <see cref="Visibility.Visible"/> plus a non-zero rendered height after layout, so a regression that leaves
    /// the note bound but Collapsed/out-of-layout cannot pass.</summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void CleanView_renders_source_health_notes_when_a_source_is_degraded(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                using var fx = new ExecutorFixture();
                var startup = new StartupInventory(
                    new[] { new StartupEntry("Updater", @"C:\Program Files\App\updater.exe", StartupSource.HkcuRun, null) },
                    SourceHealth.Partial,
                    new[] { new InventorySourceFault("HKLM Run", "SecurityException") });
                var exts = new BrowserExtensionListing(
                    Array.Empty<BrowserExtension>(),
                    SourceHealth.Partial,
                    new[] { new InventorySourceFault("Edge/Default", "UnauthorizedAccessException") });
                var vm = new CleanViewModel(
                    i18n,
                    new RenderFakeJunkProbe(),
                    new RenderFakeStartupProbe(startup),
                    new RenderFakeBrowserExtensionInventory(exts),
                    new RenderFakeRecycleBinService(RecycleBinInventory.Unavailable("HRESULT 0x80004005")),
                    new RenderFakeFolderOpener(),
                    fx.Gate,
                    new RenderPlanExecutor(fx.Executor));
                InstallDispatcherSyncContext();
                vm.RefreshRecycleCommand.Execute(null);
                PumpAsyncWork(() => !vm.IsBusy && vm.RecycleHealthNote.Length > 0, TimeSpan.FromSeconds(5));
                vm.LoadStartupCommand.Execute(null);
                PumpAsyncWork(() => !vm.IsBusy && vm.StartupHealthNote.Length > 0, TimeSpan.FromSeconds(5));
                vm.LoadExtensionsCommand.Execute(null);
                PumpAsyncWork(() => !vm.IsBusy && vm.ExtensionsHealthNote.Length > 0, TimeSpan.FromSeconds(5));

                var view = new CleanView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1100, Height = 900 };
                var size = new Size(1100, 900);
                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                AssertHealthNoteVisible(view, "RecycleHealthNoteText", i18n["clean.recycle.unavailable"]);
                AssertHealthNoteVisible(view, "StartupHealthNoteText", i18n["clean.startup.incomplete"]);
                AssertHealthNoteVisible(view, "ExtensionsHealthNoteText", i18n["clean.ext.incomplete"]);
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
                I18n i18n = TestI18n.Full("en");
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

                // MINOR-02: the healthy path must show NEITHER note. Absence of a warning is a claim too.
                AssertHealthNoteCollapsed(view, "InstallManifestInfoNoteText");
                AssertHealthNoteCollapsed(view, "InstallManifestHealthNoteText");
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>MAJOR-01 (2026-07-29): <c>NotInstalled</c> is the most common production state at this boundary —
    /// <c>installer/WindowsCareKit.iss</c> excludes <c>manifests\*</c> from the base file set, so every compact
    /// and most custom installs produce it — and it was the one state of five with no render proof. Hardcoding
    /// this note's Visibility to Collapsed left the whole suite green: the property, its VM assignment and its
    /// unit assertion all survived, and only the user stopped seeing it. That is the reviewed defect's exact
    /// shape (a typed result nobody renders) reintroduced at the state that matters most.</summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void InstallView_renders_manifest_not_installed_info_note(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                using var fx = new ExecutorFixture();
                var load = new InstallManifestLoadResult(
                    InstallManifest.Empty,
                    InstallManifestLoadStatus.NotInstalled,
                    @"C:\app\manifests\90-install.json",
                    null);
                var vm = new InstallViewModel(
                    i18n,
                    new RenderFakeManifestLoader(load),
                    new InstallPlanner(fx.Gate, new RenderAllNetDriverGuard()),
                    new RenderFakeAuthProbe(),
                    new RenderRecordingStateStore(),
                    fx.Gate,
                    new RenderPlanExecutor(fx.Executor),
                    new InstallRunner(new RenderThrowingPlanWriter(), new FakeClock(DateTime.UtcNow)));
                vm.LoadManifest();

                var view = new InstallView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1100, Height = 900 };
                var size = new Size(1100, 900);
                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                AssertHealthNoteVisible(view, "InstallManifestInfoNoteText", i18n["install.manifest.notInstalled"]);
                // An absent optional component is calm, not breakage — the red danger note must stay hidden.
                AssertHealthNoteCollapsed(view, "InstallManifestHealthNoteText");
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
    public void InstallView_renders_manifest_failure_health_note(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                using var fx = new ExecutorFixture();
                var load = new InstallManifestLoadResult(
                    InstallManifest.Empty,
                    InstallManifestLoadStatus.Malformed,
                    @"C:\app\manifests\90-install.json",
                    "JsonException");
                var vm = new InstallViewModel(
                    i18n,
                    new RenderFakeManifestLoader(load),
                    new InstallPlanner(fx.Gate, new RenderAllNetDriverGuard()),
                    new RenderFakeAuthProbe(),
                    new RenderRecordingStateStore(),
                    fx.Gate,
                    new RenderPlanExecutor(fx.Executor),
                    new InstallRunner(new RenderThrowingPlanWriter(), new FakeClock(DateTime.UtcNow)));
                vm.LoadManifest();

                var view = new InstallView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1100, Height = 900 };
                var size = new Size(1100, 900);
                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                // MINOR-01: source the expectation from the i18n table, NOT from vm.ManifestHealthNote —
                // asserting a TextBlock equals the very property it is bound to only proves the binding
                // exists, and would still pass if the VM selected the wrong key.
                AssertHealthNoteVisible(view, "InstallManifestHealthNoteText",
                    i18n.Format("install.manifest.failed", @"C:\app\manifests\90-install.json",
                        $"{i18n["install.manifest.cause.corrupt"]}, JsonException"));
                AssertHealthNoteCollapsed(view, "InstallManifestInfoNoteText");
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
                I18n i18n = TestI18n.Full("en");
                var vm = new MigrationViewModel(
                    i18n,
                    new RenderFakeMigrationScanService(),
                    new RenderFakeMigrationBackupRunner(),
                    () => Array.Empty<MigrationRecipe>(),
                    TestData.PayloadRoots());

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

    /// <summary>PR-1 grouping (Fable design §A, 2026-07-08): one row per APP, not per file. Seeds a real scan
    /// (via <c>LoadScan</c>, no IO) with a multi-part app (v4-labeled parts, mixed badges so the worst-of pill
    /// AND the "N/M taşınabilir" breakdown both render) plus a single-part app (no expander). Renders once
    /// collapsed, then flips <c>MigrationAppRow.IsExpanded</c> on the multi-part app and renders again so the
    /// reserved-checkbox-slot part list (A4) is actually measured/arranged — the empty-state test above never
    /// instantiates the Apps/Parts DataTemplates at all.</summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void MigrationView_renders_grouped_app_rows_in_theme(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                MigrationRecipe multiRecipe = MultiPartRecipe();
                var vm = new MigrationViewModel(
                    i18n,
                    new RenderFakeMigrationScanService(),
                    new RenderFakeMigrationBackupRunner(),
                    () => [multiRecipe],
                    TestData.PayloadRoots());

                vm.LoadScan(
                    new DetectionResult(Array.Empty<DiscoveredProgram>(), Array.Empty<ProgramSourceReport>()),
                    @"C:\Users\render-smoke",
                    [MultiPartCandidate(0, BadgeCase.Portable), MultiPartCandidate(1, BadgeCase.MachineLocked), SinglePartCandidate()]);

                var view = new MigrationView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1100, Height = 900 };
                var size = new Size(1100, 900);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                MigrationAppRow multiApp = vm.Groups
                    .SelectMany(g => g.Apps)
                    .Single(a => a.HasMultipleParts);
                Assert.Equal(2, multiApp.Parts.Count);
                Assert.Equal("Config", multiApp.Parts[0].PartLabel);
                Assert.Contains("portable", multiApp.BreakdownText); // en culture — badge labels are baked in MigrationBadgePresenter, not i18n
                Assert.Equal("⌄", multiApp.ExpansionGlyph);
                ToggleButton header = Assert.Single(
                    Descendants<ToggleButton>(host),
                    button => button.GetType() == typeof(ToggleButton)
                              && ReferenceEquals(button.DataContext, multiApp));
                Assert.True(header.IsEnabled);
                Assert.True(header.Focusable);
                Assert.Equal(multiApp.Title, AutomationProperties.GetName(header));

                multiApp.IsExpanded = true;
                host.UpdateLayout();
                Assert.Equal("⌃", multiApp.ExpansionGlyph);

                MigrationAppRow singleApp = vm.Groups.SelectMany(g => g.Apps).Single(a => !a.HasMultipleParts);
                Assert.False(singleApp.HasMultipleParts);
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    [Theory]
    [InlineData("en", "secrets excluded")]
    [InlineData("tr", "sırlar hariç tutuldu")]
    public void MigrationView_renders_the_localized_secret_exclusion_text_in_the_visual_tree(string culture, string expectedText)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources("Strongbox", out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full(culture);
                MigrationRecipe multiRecipe = MultiPartRecipe();
                var vm = new MigrationViewModel(
                    i18n,
                    new RenderFakeMigrationScanService(),
                    new RenderFakeMigrationBackupRunner(),
                    () => [multiRecipe],
                    TestData.PayloadRoots());
                MigrationSelectionCandidate secretPart = MultiPartCandidate(1, BadgeCase.MachineLocked) with
                {
                    Meta = MultiPartCandidate(1, BadgeCase.MachineLocked).Meta with { HasExcludedSecret = true },
                };
                vm.LoadScan(
                    new DetectionResult(Array.Empty<DiscoveredProgram>(), Array.Empty<ProgramSourceReport>()),
                    @"C:\Users\render-smoke",
                    [MultiPartCandidate(0, BadgeCase.Portable), secretPart]);

                var view = new MigrationView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1100, Height = 900 };
                var size = new Size(1100, 900);
                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                Assert.Contains(Descendants<TextBlock>(host), block => block.Text == expectedText);
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    private enum BadgeCase { Portable, MachineLocked }

    private static MigrationRecipe MultiPartRecipe() => new(
        4,
        "render.multi",
        "Render Multi App",
        "dev-tools",
        new RecipeDetect(KnownFolder.UserProfile, ".render-multi", true),
        [
            new RecipeItem("config.json", Array.Empty<string>(), Array.Empty<string>()) { Label = new LocalizedText("Config", "Ayar") },
            new RecipeItem("data", Array.Empty<string>(), Array.Empty<string>()) { Label = new LocalizedText("Data", "Veri") },
        ],
        Array.Empty<string>(),
        "global",
        PortabilityClass.ProfileRelative,
        new RecipeRestore(RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, Array.Empty<string>()));

    private static MigrationSelectionCandidate MultiPartCandidate(int index, BadgeCase badgeCase) => new()
    {
        Id = $"render.multi#{index}",
        DisplayName = "Render Multi App",
        RecipeCategory = "dev-tools",
        Meta = new MigrationItemMeta(
            "render.multi", $"render.multi#{index}",
            badgeCase == BadgeCase.MachineLocked ? PortabilityClass.MachineLocked : PortabilityClass.ProfileRelative,
            RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, Array.Empty<string>())
        {
            ItemOrdinal = index,
            PartLabel = index == 0 ? new LocalizedText("Config", "Ayar") : new LocalizedText("Data", "Veri"),
        },
        RestoreTier = RestoreTier.ConfigCopy,
        SourceKind = MigrationSourceKind.File,
        SourcePath = $@"C:\Users\render-smoke\.render-multi\part{index}",
        SizeBytes = 2048,
        IsRecognized = true,
        HasInstallRecord = true,
    };

    private static MigrationSelectionCandidate SinglePartCandidate() => new()
    {
        Id = "render.single#present",
        DisplayName = "Render Single App",
        RecipeCategory = "dev-tools",
        Meta = new MigrationItemMeta(
            "render.single", "render.single#present", PortabilityClass.ProfileRelative,
            RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, Array.Empty<string>()),
        RestoreTier = RestoreTier.ConfigCopy,
        SourceKind = MigrationSourceKind.Directory,
        SourcePath = @"C:\Users\render-smoke\.render-single",
        IsRecognized = true,
        HasInstallRecord = true,
    };

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
                I18n i18n = TestI18n.Full("en");
                const string profile = @"C:\Users\render-smoke";
                const string usersRoot = @"C:\Users";
                var gate = MigrationRestoreTestData.GateForProfile(profile, usersRoot);
                var runner = new MigrationRestoreRunner(
                    new RecipePathResolver(new ProfileRoots(
                        profile,
                        profile + @"\AppData\Roaming",
                        profile + @"\AppData\Local")),
                    gate);
                var service = new MigrationRestoreService(runner, MigrationRestoreTestData.Executor(gate), new RestoreStateStore(new SanctionedFileWriter()));
                var vm = new RestoreViewModel(i18n, new GatedMigrationRestoreService(service), new MigrationRestoreManifestStore(new SanctionedFileWriter()), new RestoreStateStore(new SanctionedFileWriter()));

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

    /// <summary>The shared <see cref="ConfirmGate"/> renders a skipped row through its real XAML seam. The
    /// source action deliberately carries Info risk, so only the IsSkipped-to-IsBlocked binding can make the
    /// resolved chip family Irreversible.</summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void ConfirmGate_skipped_row_renders_in_blocked_family_through_XAML(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                var gate = new ConfirmGateViewModel(i18n, onApprove: () => { }, onCancel: () => { }, isBusy: () => false);
                PlanRow row = PlanRow.FromSkipped(
                    TestData.FileDelete(@"C:\Users\alice\AppData\Roaming\Tool\cache") with { Risk = RiskLevel.Info },
                    "protected location",
                    i18n);
                gate.Open(ConfirmTier.Irreversible, "Confirm — this will make changes", "Review the exact actions below.", [row]);

                var view = new ConfirmGate { DataContext = gate };
                var host = new ContentControl { Content = view, Width = 900, Height = 800 };
                var size = new Size(900, 800);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                RiskChip chip = Assert.Single(Descendants<RiskChip>(host));
                Assert.Same(row, chip.DataContext);
                Assert.Equal(RiskLevel.Info, chip.Risk);
                Assert.True(chip.IsBlocked);
                Assert.Equal(ChipFamily.Irreversible, chip.Family);
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
    public void Main_shell_rail_keeps_settings_and_module_labels_inside_1030_width()
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                var vm = new ShellProbeViewModel(i18n);
                var window = new MainWindow { DataContext = vm, Width = 1030, Height = 720 };
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                root.DataContext = vm;
                var size = new Size(1030, 720);

                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();

                var featureNav = Assert.IsType<ListBox>(window.FindName("FeatureRailNav"));
                var settingsNav = Assert.IsType<ListBox>(window.FindName("SettingsRailNav"));
                featureNav.UpdateLayout();
                settingsNav.UpdateLayout();
                var settingsItem = Assert.IsType<ListBoxItem>(
                    settingsNav.ItemContainerGenerator.ContainerFromItem(vm.Nav.Last()));
                TextBlock settingsText = Descendants<TextBlock>(settingsItem)
                    .Single(t => t.Text == i18n["nav.settings"]);
                var restoreItem = Assert.IsType<ListBoxItem>(
                    featureNav.ItemContainerGenerator.ContainerFromItem(
                        vm.Nav.Single(item => item.Id == "restore")));
                TextBlock restoreLabel = Descendants<TextBlock>(restoreItem)
                    .Single(t => t.Text == i18n["nav.restore"]);

                AssertInside(root, settingsItem, size.Width, "Settings nav item");
                AssertInside(root, settingsText, size.Width, "Settings label");
                AssertInside(root, restoreLabel, size.Width, "Restore label");
                Assert.Equal(i18n["nav.restore.desc"], restoreItem.ToolTip);
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
    public void MainWindow_renders_module_health_notice_when_degraded(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                ModuleCatalogHealth health = ModuleCatalogHealth.FromComponents(
                    @"C:\app\Modules",
                    [new("broken", ModuleComponentStatus.Malformed, nameof(BadImageFormatException))]);
                var vm = new MainViewModel(i18n, [new RenderShellModule()], health)
                {
                    ShowFirstRun = false,
                };
                var window = new MainWindow { DataContext = vm, Width = 1100, Height = 720 };
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                root.DataContext = vm;
                var size = new Size(1100, 720);

                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();

                AssertHealthNoteVisible(
                    window,
                    "ModuleHealthNoticeText",
                    i18n["modules.notice.degraded"]);
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
    public void MainWindow_renders_module_health_notice_when_inventory_unavailable(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                using var workspace = new TempWorkspace("wck-render-modules-unavailable-");
                string modulesRoot = workspace.Combine("Modules");
                Directory.CreateDirectory(modulesRoot);
                ModuleCatalogHealth health = new DirectoryModuleCatalog(
                    modulesRoot,
                    _ => throw new UnauthorizedAccessException("synthetic render failure"))
                    .LoadModules()
                    .Health;
                var vm = new MainViewModel(i18n, [new RenderShellModule()], health)
                {
                    ShowFirstRun = false,
                };
                var window = new MainWindow { DataContext = vm, Width = 1100, Height = 720 };
                var root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                root.DataContext = vm;
                var size = new Size(1100, 720);

                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();

                AssertHealthNoteVisible(
                    window,
                    "ModuleHealthNoticeText",
                    i18n["modules.notice.unavailable"]);
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
    public void MainWindow_renders_no_module_health_notice_when_no_component_is_installed(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                var notInstalledVm = new MainViewModel(
                    i18n,
                    [new RenderShellModule()],
                    ModuleCatalogHealth.FromComponents(@"C:\app\Modules", []))
                {
                    ShowFirstRun = false,
                };
                var notInstalledWindow = new MainWindow
                {
                    DataContext = notInstalledVm,
                    Width = 1100,
                    Height = 720,
                };
                FrameworkElement notInstalledRoot =
                    Assert.IsAssignableFrom<FrameworkElement>(notInstalledWindow.Content);
                notInstalledRoot.DataContext = notInstalledVm;
                var size = new Size(1100, 720);
                notInstalledRoot.Measure(size);
                notInstalledRoot.Arrange(new Rect(size));
                notInstalledRoot.UpdateLayout();

                AssertHealthNoteCollapsed(notInstalledWindow, "ModuleHealthNoticeText");

                var completeVm = new MainViewModel(
                    i18n,
                    [new RenderShellModule()],
                    ModuleCatalogHealth.FromComponents(
                        @"C:\app\Modules",
                        [new("clean", ModuleComponentStatus.Loaded, null)]))
                {
                    ShowFirstRun = false,
                };
                var completeWindow = new MainWindow
                {
                    DataContext = completeVm,
                    Width = 1100,
                    Height = 720,
                };
                FrameworkElement completeRoot =
                    Assert.IsAssignableFrom<FrameworkElement>(completeWindow.Content);
                completeRoot.DataContext = completeVm;
                completeRoot.Measure(size);
                completeRoot.Arrange(new Rect(size));
                completeRoot.UpdateLayout();

                AssertHealthNoteCollapsed(completeWindow, "ModuleHealthNoticeText");
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
    public void SettingsView_renders_failed_component_row_with_its_reason(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                ModuleCatalogHealth health = ModuleCatalogHealth.FromComponents(
                    @"C:\app\Modules",
                    [
                        new("loaded", ModuleComponentStatus.Loaded, null),
                        new("incomplete", ModuleComponentStatus.Incomplete, null),
                        new("malformed", ModuleComponentStatus.Malformed, nameof(BadImageFormatException)),
                        new("unreadable", ModuleComponentStatus.Unreadable, nameof(IOException)),
                    ]);
                var vm = new SettingsViewModel(
                    i18n,
                    new FakeThemeService(),
                    new RecordingUrlOpener(),
                    health);
                var view = new SettingsView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1000, Height = 1000 };
                var size = new Size(1000, 1000);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                AssertHealthNoteCollapsed(view, "SettingsModulesEmptyNoteText");
                AssertHealthNoteCollapsed(view, "SettingsModulesInventoryNoteText");
                var list = Assert.IsType<ItemsControl>(view.FindName("SettingsModulesList"));
                Assert.Equal(4, list.Items.Count);
                TextBlock[] rowText = Descendants<TextBlock>(list).ToArray();

                Assert.Contains(rowText, text => text.Text == "loaded");
                Assert.Contains(rowText, text => text.Text == i18n["modules.status.loaded"]);
                TextBlock loadedReason = Assert.Single(
                    rowText,
                    text => text.Text == string.Empty && text.Visibility == Visibility.Collapsed);
                Assert.Equal(Visibility.Collapsed, loadedReason.Visibility);

                Assert.Contains(rowText, text => text.Text == "incomplete");
                Assert.Contains(rowText, text => text.Text == i18n["modules.status.incomplete"]);
                AssertVisibleRowText(rowText, i18n["modules.reason.incomplete"]);

                Assert.Contains(rowText, text => text.Text == "malformed");
                Assert.Contains(rowText, text => text.Text == i18n["modules.status.malformed"]);
                AssertVisibleRowText(
                    rowText,
                    i18n.Format("modules.reason.malformed", nameof(BadImageFormatException)));

                Assert.Contains(rowText, text => text.Text == "unreadable");
                Assert.Contains(rowText, text => text.Text == i18n["modules.status.unreadable"]);
                AssertVisibleRowText(
                    rowText,
                    i18n.Format("modules.reason.unreadable", nameof(IOException)));
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
    public void SettingsView_renders_calm_empty_note_when_no_component_is_installed(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                var vm = new SettingsViewModel(
                    i18n,
                    new FakeThemeService(),
                    new RecordingUrlOpener(),
                    ModuleCatalogHealth.FromComponents(@"C:\app\Modules", []));
                var view = new SettingsView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1000, Height = 800 };
                var size = new Size(1000, 800);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                AssertHealthNoteVisible(
                    view,
                    "SettingsModulesEmptyNoteText",
                    i18n["modules.none"]);
                AssertHealthNoteCollapsed(view, "SettingsModulesInventoryNoteText");
                // The folder the user must actually look in. §4.4 lists it in EVERY state, so deleting the
                // row must not stay green. The expectation is the literal this test built the health from,
                // not the view-model property under test.
                AssertHealthNoteVisible(view, "SettingsModulesRootText", @"C:\app\Modules");
                var list = Assert.IsType<ItemsControl>(view.FindName("SettingsModulesList"));
                Assert.Empty(list.Items);
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
    public void SettingsView_renders_inventory_unavailable_note_without_claiming_emptiness(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                using var workspace = new TempWorkspace("wck-render-settings-modules-unavailable-");
                string modulesRoot = workspace.Combine("Modules");
                Directory.CreateDirectory(modulesRoot);
                ModuleCatalogHealth health = new DirectoryModuleCatalog(
                    modulesRoot,
                    _ => throw new UnauthorizedAccessException("synthetic render failure"))
                    .LoadModules()
                    .Health;
                var vm = new SettingsViewModel(
                    i18n,
                    new FakeThemeService(),
                    new RecordingUrlOpener(),
                    health);
                var view = new SettingsView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1000, Height = 800 };
                var size = new Size(1000, 800);

                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                AssertHealthNoteVisible(
                    view,
                    "SettingsModulesInventoryNoteText",
                    i18n.Format(
                        "modules.inventory.unavailable",
                        nameof(UnauthorizedAccessException)));
                AssertHealthNoteCollapsed(view, "SettingsModulesEmptyNoteText");
                var list = Assert.IsType<ItemsControl>(view.FindName("SettingsModulesList"));
                Assert.Empty(list.Items);
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
                I18n i18n = TestI18n.Full("en");
                var vm = new UninstallViewModel(
                    i18n,
                    new FakeInstalledAppReader(),
                    new FakeAppxReader(),
                    TestData.Gate(),
                    new FakeLeftoverProbe(),
                    new FakeExecutor(),
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
                I18n i18n = TestI18n.Full("en");
                var vm = new UninstallViewModel(
                    i18n,
                    new FakeInstalledAppReader(TestData.App("Sample App")),
                    new FakeAppxReader(),
                    TestData.Gate(),
                    new FakeLeftoverProbe(),
                    new FakeExecutor(),
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

    // The 4-beat wizard's render smoke retired with the wizard itself (M3): the leftover rows it seeded now
    // render in the Uninstall screen's removal rail, and UninstallScreenTests renders them there — against
    // the real frame, with the layout measured rather than merely pumped.

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
                I18n i18n = TestI18n.Full("en");
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

                // MINOR-02: the healthy path must show NEITHER note. Absence of a warning is a claim too.
                AssertHealthNoteCollapsed(view, "BackupManifestInfoNoteText");
                AssertHealthNoteCollapsed(view, "BackupManifestHealthNoteText");
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>MAJOR-01 (2026-07-29): the Backup twin of the Install NotInstalled render proof — same reasoning,
    /// same installer-component cause. See <see cref="InstallView_renders_manifest_not_installed_info_note"/>.</summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void BackupView_renders_manifest_not_installed_info_note(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                var load = new BackupManifestLoadResult(
                    new BackupManifest([]),
                    BackupManifestLoadStatus.NotInstalled,
                    []);
                BackupViewModel vm = BuildBackupRenderViewModel(i18n, new FixedBackupRenderManifestLoader(load));
                vm.BuildPlanAsync().GetAwaiter().GetResult();

                var view = new BackupView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1000, Height = 720 };
                var size = new Size(1000, 720);
                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                AssertHealthNoteVisible(view, "BackupManifestInfoNoteText", i18n["backup.manifest.notInstalled"]);
                AssertHealthNoteCollapsed(view, "BackupManifestHealthNoteText");
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
    public void BackupView_renders_manifest_failure_health_note(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                var load = new BackupManifestLoadResult(
                    new BackupManifest([]),
                    BackupManifestLoadStatus.Unavailable,
                    [new(@"C:\app\manifests\00-bad.json", BackupManifestFileStatus.Malformed, "JsonException")]);
                BackupViewModel vm = BuildBackupRenderViewModel(i18n, new FixedBackupRenderManifestLoader(load));
                vm.BuildPlanAsync().GetAwaiter().GetResult();

                var view = new BackupView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1000, Height = 720 };
                var size = new Size(1000, 720);
                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                // MINOR-01: independently-sourced expectation (see the Install twin above).
                AssertHealthNoteVisible(view, "BackupManifestHealthNoteText",
                    i18n.Format("backup.manifest.unavailable",
                        $@"C:\app\manifests\00-bad.json ({i18n["backup.manifest.cause.corrupt"]}, JsonException)"));
                AssertHealthNoteCollapsed(view, "BackupManifestInfoNoteText");
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    // ---- B2: a display-only row states its own disposition, proven through the PRODUCTION factory ------------
    //
    // Every proof below drives the REAL view-model through its public API and reads the chip off the row the
    // PRODUCTION factory built. Constructing `new PlanRow { Disposition = ... }` and asserting the chip turns
    // red would prove only that RiskChipFamilies.For works — four tests already prove that — and it is exactly
    // why this defect survived four rounds: the failure was in CONSTRUCTION, never in the mapping.

    /// <summary>
    /// One display-only row, checked at BOTH seams: what the production factory decided, and what the visual
    /// tree actually rendered for that same row instance. The chip is located by DataContext identity, so a
    /// second row's chip can never stand in for a missing one.
    /// </summary>
    private static void AssertDisplayRow(
        DependencyObject host,
        PlanRow row,
        RowDisposition disposition,
        RiskLevel risk,
        ChipFamily family,
        string site)
    {
        Assert.True(
            row.Disposition == disposition,
            $"{site}: the production factory built a row with Disposition={row.Disposition}, expected "
            + $"{disposition}. This is the construction seam the whole round exists to fix.");
        Assert.True(
            row.Risk == risk,
            $"{site}: the production factory built a row with Risk={row.Risk}, expected {risk}. Risk is the "
            + "engine's own level, never a lever chosen to obtain a colour.");
        Assert.Equal(disposition is RowDisposition.WillNotRun, row.IsSkipped);

        RiskChip chip = Assert.Single(
            Descendants<RiskChip>(host),
            candidate => ReferenceEquals(candidate.DataContext, row));

        Assert.True(
            chip.IsBlocked == (disposition is RowDisposition.WillNotRun),
            $"{site}: the row reports IsSkipped={row.IsSkipped} but the rendered chip has "
            + $"IsBlocked={chip.IsBlocked}. The IsSkipped-to-IsBlocked binding is what carries the fact to the "
            + "screen; the family is meaningless without it.");
        Assert.True(
            chip.Family == family,
            $"{site}: the row rendered in the {chip.Family} family, expected {family} "
            + $"(Risk={row.Risk}, Disposition={row.Disposition}).");
    }

    private static ContentControl RenderHost(FrameworkElement view, double width = 1100, double height = 900)
    {
        var host = new ContentControl { Content = view, Width = width, Height = height };
        var size = new Size(width, height);
        host.Measure(size);
        host.Arrange(new Rect(size));
        host.UpdateLayout();
        return host;
    }

    /// <summary>Sites 1-2. Drives the capture flow end to end: the planner refuses one recipe item, the run
    /// copies one file and refuses another.</summary>
    [Fact]
    public void MigrationView_capture_rows_state_their_disposition_and_render_it()
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                var runner = new RenderCaptureBackupRunner
                {
                    PlanSkips = [new RecipeItemSkip("secret.db", "forbidden secret store")],
                    FailedActionCount = 1,
                };
                var vm = new MigrationViewModel(
                    i18n,
                    new RenderFakeMigrationScanService(),
                    runner,
                    () => [CaptureRecipe()],
                    TestData.PayloadRoots());
                vm.LoadScan(
                    new DetectionResult(Array.Empty<DiscoveredProgram>(), Array.Empty<ProgramSourceReport>()),
                    @"C:\Users\render-smoke",
                    [CaptureCandidate()]);
                vm.ConfirmProfileCommand.Execute(null);
                vm.PackageDir = Path.Combine(
                    Path.GetTempPath(), "wck-render-capture-" + Guid.NewGuid().ToString("N"));

                vm.BuildCapturePlanAsync().GetAwaiter().GetResult();
                vm.IsPreviewApproved = true;
                vm.RunCaptureAsync().GetAwaiter().GetResult();

                ContentControl host = RenderHost(new MigrationView { DataContext = vm });

                PlanRow skipped = Assert.Single(vm.CaptureSkippedRows);
                AssertDisplayRow(host, skipped, RowDisposition.WillNotRun, RiskLevel.Info,
                    ChipFamily.Irreversible, "site 1 MigrationViewModel.SkipRow");

                PlanRow copied = Assert.Single(vm.CaptureResultRows, row => row.RiskText == "COPIED");
                PlanRow notCopied = Assert.Single(vm.CaptureResultRows, row => row.RiskText == "SKIPPED");
                AssertDisplayRow(host, copied, RowDisposition.Unstated, RiskLevel.Low,
                    ChipFamily.Reversible, "site 2 MigrationViewModel.ResultRow (copied)");
                AssertDisplayRow(host, notCopied, RowDisposition.WillNotRun, RiskLevel.Info,
                    ChipFamily.Irreversible, "site 2 MigrationViewModel.ResultRow (not copied)");
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>Sites 3-4. The skipped row is the one this round converts from a bespoke grey pill to the
    /// shared chip; the manual row is asserted UNCHANGED, because a manual to-do is a step that needs a
    /// decision, not a refusal — over-reddening it would be the same defect in the other direction.</summary>
    [Fact]
    public void BackupView_skipped_row_is_blocked_and_the_manual_row_stays_amber()
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                BackupViewModel vm = BuildBackupRenderViewModel(i18n);
                vm.BuildPlanAsync().GetAwaiter().GetResult();

                var view = new BackupView { DataContext = vm };
                ContentControl host = RenderHost(view, 1000, 720);

                PlanRow skipped = Assert.Single(vm.SkippedRows);
                AssertDisplayRow(host, skipped, RowDisposition.WillNotRun, RiskLevel.Info,
                    ChipFamily.Irreversible, "site 4 BackupViewModel.SkipRow");

                // The chip keeps the localized word from the I18n indexer, not RiskText: BackupViewModel has no
                // language-changed handler, so a word frozen at construction would survive a language switch.
                RiskChip chip = Assert.Single(
                    Descendants<RiskChip>(host),
                    candidate => ReferenceEquals(candidate.DataContext, skipped));
                Assert.Equal(i18n["backup.row.skipChip"], chip.Text);

                // Site 3, unchanged and deliberately so. It renders through Backup's bespoke MANUAL pill rather
                // than a RiskChip, so there is no rendered family to assert — stated here rather than implied.
                PlanRow manual = Assert.Single(vm.ManualRows);
                Assert.Equal(RowDisposition.Unstated, manual.Disposition);
                Assert.Equal(RiskLevel.High, manual.Risk);
                Assert.False(manual.IsSkipped);
                Assert.DoesNotContain(
                    Descendants<RiskChip>(host),
                    candidate => ReferenceEquals(candidate.DataContext, manual));
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>Site 5. A real authorized run over a temp payload root: one copy lands, one is refused.</summary>
    [Fact]
    public void BackupView_result_rows_state_their_disposition_and_render_it()
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(out ResourceDictionary theme);
            using var workspace = new TempWorkspace("wck-render-backup-result-");
            try
            {
                I18n i18n = TestI18n.Full("en");
                BackupViewModel vm = BuildBackupResultRenderViewModel(i18n, workspace.Root);
                vm.BuildPlanAsync().GetAwaiter().GetResult();
                vm.IsPreviewApproved = true;
                vm.RunAsync().GetAwaiter().GetResult();

                ContentControl host = RenderHost(new BackupView { DataContext = vm }, 1000, 720);

                PlanRow copied = Assert.Single(vm.ResultRows, row => row.RiskText == "COPIED");
                PlanRow notCopied = Assert.Single(vm.ResultRows, row => row.RiskText == "SKIPPED");
                AssertDisplayRow(host, copied, RowDisposition.Unstated, RiskLevel.Low,
                    ChipFamily.Reversible, "site 5 BackupViewModel.ResultRow (copied)");
                AssertDisplayRow(host, notCopied, RowDisposition.WillNotRun, RiskLevel.Info,
                    ChipFamily.Irreversible, "site 5 BackupViewModel.ResultRow (not copied)");
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>
    /// The Backup skipped chip must stay legible in both themes AFTER the row's opacity composites it against
    /// the page behind it. Every input is read off the render — the ink, the wash, the effective opacity of the
    /// whole ancestor chain, and the page colour — so the arithmetic cannot be satisfied by constants this test
    /// also chose. The wash may be a gradient, so the ratio is taken against its WORST stop.
    /// </summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void BackupView_skipped_chip_meets_AA_over_the_composited_row_opacity(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                BackupViewModel vm = BuildBackupRenderViewModel(i18n);
                vm.BuildPlanAsync().GetAwaiter().GetResult();

                var view = new BackupView { DataContext = vm };
                var host = new ContentControl { Content = view, Width = 1000, Height = 720 };

                // The page colour the module view sits on in the shell (MainWindow.xaml:8 binds the window
                // Background to Bg.Window), resolved from the THEME rather than restated as a literal here.
                host.SetResourceReference(Control.BackgroundProperty, "Bg.Window");
                var size = new Size(1000, 720);
                host.Measure(size);
                host.Arrange(new Rect(size));
                host.UpdateLayout();

                PlanRow skipped = Assert.Single(vm.SkippedRows);
                RiskChip chip = Assert.Single(
                    Descendants<RiskChip>(host),
                    candidate => ReferenceEquals(candidate.DataContext, skipped));
                Assert.Equal(ChipFamily.Irreversible, chip.Family);

                Border chipRoot = Assert.Single(Descendants<Border>(chip), border => border.Name == "ChipRoot");
                TextBlock label = Assert.Single(
                    Descendants<TextBlock>(chip), block => block.Name == "ChipText");

                double alpha = EffectiveOpacity(chipRoot, host);
                Color page = Assert.IsType<SolidColorBrush>(host.Background).Color;
                Color ink = Composite(RenderedInk(label), page, alpha);
                Color[] wash = RenderedWash(chipRoot).Select(stop => Composite(stop, page, alpha)).ToArray();
                double ratio = wash.Min(stop => Contrast.Ratio(HexOf(ink), HexOf(stop)));

                output.WriteLine(
                    $"AA  {themeName,-9} Backup skip chip  opacity {alpha:F2} over {HexOf(page)}  ink "
                    + $"{HexOf(ink)} on wash {string.Join('/', wash.Select(HexOf))} = {ratio:F2}:1");

                Assert.True(
                    ratio >= MinimumChipContrastRatio,
                    $"{themeName}: the Backup skipped chip rendered {ratio:F2}:1 after compositing at "
                    + $"opacity {alpha:F2} over {HexOf(page)} (ink {HexOf(ink)} on wash "
                    + $"{string.Join('/', wash.Select(HexOf))}), below the WCAG AA "
                    + $"{MinimumChipContrastRatio:F1}:1 floor for normal-size text.");
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>Sites 6-7 at PREVIEW: a machine-locked target is refused, a restorable one is planned, and the
    /// manual bucket stays amber.</summary>
    [Fact]
    public void RestoreView_preview_rows_state_their_disposition_and_render_it()
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(out ResourceDictionary theme);
            using var fixture = RestoreViewModelTests.Fixture.Create("render-b2-preview");
            try
            {
                fixture.WritePayload("migration/x/settings.json", "NEW");
                fixture.SaveManifest(
                    RestoreViewModelTests.Target("git.config#0", ".gitconfig"),
                    RestoreViewModelTests.Target(
                        "locked#0", "locked.db",
                        recipeId: "locked.app",
                        portability: PortabilityClass.MachineLocked));
                RestoreViewModel vm = fixture.CreateViewModel();
                vm.LoadAndPreviewAsync().GetAwaiter().GetResult();

                ContentControl host = RenderHost(new RestoreView { DataContext = vm });

                PlanRow skipped = Assert.Single(vm.SkippedRows);
                AssertDisplayRow(host, skipped, RowDisposition.WillNotRun, RiskLevel.Info,
                    ChipFamily.Irreversible, "site 6 RestoreViewModel.SkipRow (MachineLocked)");

                PlanRow restored = Assert.Single(vm.RestoredRows);
                AssertDisplayRow(host, restored, RowDisposition.Unstated, RiskLevel.Low,
                    ChipFamily.Reversible, "site 7 RestoreViewModel.ReportRow (Restored)");

                PlanRow manual = vm.ManualRows[0];
                AssertDisplayRow(host, manual, RowDisposition.Unstated, RiskLevel.High,
                    ChipFamily.Attention, "site 7 RestoreViewModel.ReportRow (Manual)");
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>Sites 7-8 after a RUN whose second merge throws: one target restores, one does not. The failure
    /// is injected at the copy ADAPTER, so the plan the runner rebuilds still hashes to the approved value and
    /// the run is genuinely authorized — a refused run reports nothing at all and would prove nothing here.</summary>
    [Fact]
    public void RestoreView_run_rows_state_their_disposition_and_render_it()
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(out ResourceDictionary theme);
            using var workspace = new TempWorkspace("wck-render-restore-run-");
            try
            {
                RestoreViewModel vm = BuildFailingRestoreRenderViewModel(workspace);

                vm.LoadAndPreviewAsync().GetAwaiter().GetResult();
                vm.IsPreviewApproved = true;
                vm.RunRestoreAsync().GetAwaiter().GetResult();

                ContentControl host = RenderHost(new RestoreView { DataContext = vm });

                PlanRow done = Assert.Single(
                    vm.ResultRows, row => row.RiskText == vm.I18n["migration.restore.status.Done"]);
                AssertDisplayRow(host, done, RowDisposition.Unstated, RiskLevel.Low,
                    ChipFamily.Reversible, "site 8 RestoreViewModel.ResultRow (Done)");

                PlanRow notDone = Assert.Single(
                    vm.ResultRows, row => row.RiskText != vm.I18n["migration.restore.status.Done"]);
                AssertDisplayRow(host, notDone, RowDisposition.WillNotRun, RiskLevel.Info,
                    ChipFamily.Irreversible, "site 8 RestoreViewModel.ResultRow (not Done)");

                PlanRow notRestored = Assert.Single(vm.NotRestoredRows);
                AssertDisplayRow(host, notRestored, RowDisposition.WillNotRun, RiskLevel.Info,
                    ChipFamily.Irreversible, "site 7 RestoreViewModel.ReportRow (NotRestored)");
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>
    /// A real restore stack — real runner, real gate, real <see cref="GatedExecutor"/> — over a synthetic
    /// profile inside <paramref name="workspace"/>, whose copy adapter throws on the SECOND merge. That is the
    /// only seam that yields a mixed Done/Failed execution without disturbing the approved plan hash.
    /// </summary>
    private static RestoreViewModel BuildFailingRestoreRenderViewModel(TempWorkspace workspace)
    {
        string package = workspace.Combine("package");
        string stateDir = workspace.Combine("state");
        string usersRoot = workspace.Combine("Users");
        string profile = Path.Combine(usersRoot, "render-user");
        Directory.CreateDirectory(profile);
        Directory.CreateDirectory(stateDir);
        Directory.CreateDirectory(Path.Combine(package, "migration", "render"));
        for (int i = 1; i <= 2; i++)
            File.WriteAllText(Path.Combine(package, "migration", "render", $"source-{i}.json"), $"payload-{i}");

        var manifest = new MigrationRestoreManifest(
            MigrationRestoreManifest.CurrentSchemaVersion,
            Enumerable.Range(1, 2).Select(i => new MigrationRestoreTarget(
                $"render.recipe.{i}",
                $"render-entry-{i}",
                KnownFolder.UserProfile,
                $"target-{i}.json",
                $"migration/render/source-{i}.json",
                RestoreStrategy.ConfigWrite,
                RestorePhase.ConfigWrite,
                Array.Empty<string>(),
                PortabilityClass.ProfileRelative,
                $"render-sha-{i}")
            {
                RestoreTier = RestoreTier.ConfigCopy,
            }).ToArray());

        var manifestStore = new MigrationRestoreManifestStore(new SanctionedFileWriter());
        manifestStore.Save(package, manifest);

        SafetyGate gate = MigrationRestoreTestData.GateForProfile(profile, usersRoot);
        var runner = new MigrationRestoreRunner(
            new RecipePathResolver(new ProfileRoots(
                profile,
                Path.Combine(profile, "AppData", "Roaming"),
                Path.Combine(profile, "AppData", "Local"))),
            gate);
        var unusedAdapters = new RecordingAdapters { ThrowOnAnyCall = true };
        var executor = new GatedExecutor(
            gate,
            new ExecutionLog(workspace.Combine("execution.jsonl"), new LogRedactor(null, null)),
            unusedAdapters.File,
            unusedAdapters.Registry,
            unusedAdapters.Service,
            unusedAdapters.Task,
            unusedAdapters.Process,
            new RenderFailOnSecondMergeAdapter());
        var service = new MigrationRestoreService(
            runner, executor, new RestoreStateStore(new SanctionedFileWriter()));

        return new RestoreViewModel(
            TestI18n.Full("en"),
            new GatedMigrationRestoreService(service),
            manifestStore,
            new RestoreStateStore(new SanctionedFileWriter()))
        {
            PackageDir = package,
            StateDir = stateDir,
        };
    }

    private sealed class RenderFailOnSecondMergeAdapter : ICopyAdapter
    {
        private int _mergeCalls;

        public CopyAdapterResult Copy(CopyAction action)
            => throw new InvalidOperationException("copy actions are not expected in a restore render proof");

        public void Merge(RestoreMergeAction action)
        {
            if (++_mergeCalls == 2)
                throw new IOException("synthetic second-merge failure");
        }
    }

    /// <summary>
    /// Site 6, the OTHER direction. <c>AlreadyDone</c> is the one skip reason that is not a refusal — the work
    /// is finished — so it must stay in the calm family. This is the guard against over-reddening: a fix that
    /// blocks every skip reason passes every other proof in this region and fails here.
    /// </summary>
    [Fact]
    public void RestoreView_an_already_done_skip_stays_in_the_calm_family()
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(out ResourceDictionary theme);
            using var fixture = RestoreViewModelTests.Fixture.Create("render-b2-alreadydone");
            try
            {
                fixture.WritePayload("migration/x/settings.json", "NEW");
                fixture.SaveManifest(RestoreViewModelTests.Target("git.config#0", ".gitconfig"));
                RestoreViewModel vm = fixture.CreateViewModel();

                vm.LoadAndPreviewAsync().GetAwaiter().GetResult();
                vm.IsPreviewApproved = true;
                vm.RunRestoreAsync().GetAwaiter().GetResult();
                vm.LoadAndPreviewAsync().GetAwaiter().GetResult();

                ContentControl host = RenderHost(new RestoreView { DataContext = vm });

                PlanRow skipped = Assert.Single(vm.SkippedRows);
                Assert.Contains(
                    RestoreSkipReason.AlreadyDone.ToString(), skipped.Detail ?? string.Empty,
                    StringComparison.Ordinal);
                AssertDisplayRow(host, skipped, RowDisposition.Unstated, RiskLevel.Info,
                    ChipFamily.Neutral, "site 6 RestoreViewModel.SkipRow (AlreadyDone)");
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>Site 9. A restore that CREATED its destination has nothing to undo, so the undo builder rejects
    /// the step and the row must say so.</summary>
    [Fact]
    public void RestoreView_a_rejected_undo_row_states_its_disposition_and_renders_it()
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(out ResourceDictionary theme);
            using var fixture = RestoreViewModelTests.Fixture.Create("render-b2-undo");
            try
            {
                fixture.WritePayload("migration/x/settings.json", "NEW");
                fixture.SaveManifest(RestoreViewModelTests.Target("git.config#0", ".gitconfig"));
                RestoreViewModel vm = fixture.CreateViewModel();

                vm.LoadAndPreviewAsync().GetAwaiter().GetResult();
                vm.IsPreviewApproved = true;
                vm.RunRestoreAsync().GetAwaiter().GetResult();
                vm.PreviewUndoAsync().GetAwaiter().GetResult();

                ContentControl host = RenderHost(new RestoreView { DataContext = vm });

                PlanRow rejected = Assert.Single(
                    vm.UndoRows, row => row.RiskText == vm.I18n["migration.restore.status.rejected"]);
                AssertDisplayRow(host, rejected, RowDisposition.WillNotRun, RiskLevel.Info,
                    ChipFamily.Irreversible, "site 9 RestoreViewModel.RejectedUndoRow");
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>Site 10. One install action completes, one is skipped and one never runs. All three
    /// not-completed statuses are swept, because a factory that classified only ONE of them would leave the
    /// others rendering a calm chip on a row whose own text says the work did not happen.</summary>
    [Fact]
    public void InstallView_execution_result_rows_state_their_disposition_and_render_it()
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                using var fx = new ExecutorFixture();
                var entries = new[]
                {
                    new InstallEntry("git", "install", "dev", InstallMethod.Winget, "Git.Git", null, false, false, 100, "Install git"),
                    new InstallEntry("node", "install", "dev", InstallMethod.Winget, "OpenJS.NodeJS", null, false, false, 110, "Install node"),
                    new InstallEntry("pwsh", "install", "dev", InstallMethod.Winget, "Microsoft.PowerShell", null, false, false, 120, "Install pwsh"),
                };
                var vm = new InstallViewModel(
                    i18n,
                    new RenderFakeManifestLoader(entries),
                    new InstallPlanner(fx.Gate, new RenderAllNetDriverGuard()),
                    new RenderFakeAuthProbe(),
                    new RenderRecordingStateStore(),
                    fx.Gate,
                    new RenderMixedStatusExecutor(),
                    new InstallRunner(new RenderThrowingPlanWriter(), new FakeClock(DateTime.UtcNow)));
                vm.StateDirectory = Path.Combine(
                    Path.GetTempPath(), "wck-render-install-" + Guid.NewGuid().ToString("N"));

                vm.LoadManifest();
                vm.BuildPlan();
                vm.ApproveCommand.Execute(null);
                vm.RunAsync().GetAwaiter().GetResult();

                ContentControl host = RenderHost(new InstallView { DataContext = vm });

                AssertDisplayRow(
                    host, Assert.Single(vm.ExecutionResults, row => row.RiskText == "DONE"),
                    RowDisposition.Unstated, RiskLevel.Low,
                    ChipFamily.Reversible, "site 10 InstallViewModel.ResultRow (Done)");
                AssertDisplayRow(
                    host, Assert.Single(vm.ExecutionResults, row => row.RiskText == "SKIPPED"),
                    RowDisposition.WillNotRun, RiskLevel.Info,
                    ChipFamily.Irreversible, "site 10 InstallViewModel.ResultRow (Skipped)");
                AssertDisplayRow(
                    host, Assert.Single(vm.ExecutionResults, row => row.RiskText == "NOTRUN"),
                    RowDisposition.WillNotRun, RiskLevel.Info,
                    ChipFamily.Irreversible, "site 10 InstallViewModel.ResultRow (NotRun)");
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    /// <summary>Site 11. The Uninstall screen folds a mixed execution report into its result rows; the counts
    /// it keeps beside them are asserted too, because this round must not move them. The run goes through the
    /// real single door — scan, stage, type the confirm word, approve — so the rows under test are the ones a
    /// real removal produces.</summary>
    [Fact]
    public void UninstallView_result_rows_state_their_disposition_and_render_it()
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(out ResourceDictionary theme);
            try
            {
                I18n i18n = TestI18n.Full("en");
                var probe = new FakeLeftoverProbe();
                probe.RegistryKeys.Add(new LeftoverRegistryKey(
                    RegistryHive.LocalMachine, @"SOFTWARE\SomeVendor\SomeApp", RegistryView.Registry64,
                    "render-owned registry key"));
                InstalledApp app = TestData.App(
                    displayName: "SomeApp",
                    publisher: "SomeVendor",
                    source: InstalledAppSource.MachineWide64,
                    uninstall: "\"C:\\Program Files\\SomeApp\\uninst.exe\" /S",
                    installLocation: @"C:\Program Files\SomeApp");
                var vm = new UninstallViewModel(
                    i18n,
                    new RenderInstalledAppReader(app),
                    new RenderAppxReader(),
                    TestData.Gate(),
                    probe,
                    new RenderFirstDoneThenFailedExecutor(),
                    new RenderFolderOpener());

                // Load BEFORE the dispatcher context is installed. Blocking on a task whose continuation is
                // posted back to a dispatcher this thread is not pumping is a deadlock, not a slow test.
                vm.LoadAsync().GetAwaiter().GetResult();
                InstallDispatcherSyncContext();
                vm.SelectedRow = Assert.Single(vm.AllRows);
                vm.ScanLeftoversCommand.Execute(null);
                PumpAsyncWork(() => vm.HasScanned, TimeSpan.FromSeconds(5));

                vm.UninstallSelectedCommand.Execute(null);
                // Keep the one program-owned leftover, so the report carries a vendor step AND a leftover.
                foreach (PlanRow row in vm.Gate.Rows.Where(row => row.IsVetoable))
                    row.IsIncluded = true;
                vm.Gate.TypedConfirm = vm.Gate.ConfirmWord;
                vm.Gate.ApproveCommand.Execute(null);
                PumpAsyncWork(() => vm.HasResult, TimeSpan.FromSeconds(10));

                ContentControl host = RenderHost(new UninstallView { DataContext = vm }, 1100, 760);

                PlanRow done = Assert.Single(
                    vm.ExecutionResults, row => row.RiskText == i18n["uninstall.result.status.done"]);
                PlanRow failed = Assert.Single(
                    vm.ExecutionResults, row => row.RiskText == i18n["uninstall.result.status.failed"]);
                AssertDisplayRow(host, done, RowDisposition.Unstated, RiskLevel.Low,
                    ChipFamily.Reversible, "site 11 UninstallViewModel.RunRemovalAsync (Done)");
                AssertDisplayRow(host, failed, RowDisposition.WillNotRun, RiskLevel.Info,
                    ChipFamily.Irreversible, "site 11 UninstallViewModel.RunRemovalAsync (Failed)");

                // The bookkeeping beside the rows is a different fact and must not have moved.
                Assert.StartsWith("1 done", vm.ResultSummary, StringComparison.Ordinal);
                Assert.Contains("1 failed", vm.ResultSummary, StringComparison.Ordinal);
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    private sealed class RenderInstalledAppReader(params InstalledApp[] apps) : IInstalledAppReader
    {
        public IReadOnlyList<InstalledApp> ReadAll() => apps;
    }

    private sealed class RenderAppxReader : IAppxReader
    {
        public IReadOnlyList<InstalledAppx> ReadCurrentUserPackages() => Array.Empty<InstalledAppx>();
    }

    private sealed class RenderFolderOpener : IFolderOpener
    {
        public void OpenFolder(string path) { }
    }

    /// <summary>WCAG AA for normal-size text; the chip label is 10-11.5pt, so the large-text exemption never
    /// applies to it.</summary>
    private const double MinimumChipContrastRatio = 4.5;

    /// <summary>The product of every <see cref="UIElement.Opacity"/> between an element and an ancestor — the
    /// alpha the whole subtree is actually composited with. Read from the render, so a theme that changes the
    /// row opacity moves this number without the test being edited.</summary>
    private static double EffectiveOpacity(DependencyObject from, DependencyObject ancestor)
    {
        double alpha = 1.0;
        DependencyObject? current = from;
        while (current is not null && !ReferenceEquals(current, ancestor))
        {
            if (current is UIElement element)
                alpha *= element.Opacity;
            current = VisualTreeHelper.GetParent(current);
        }

        return alpha;
    }

    private static Color Composite(Color over, Color under, double alpha) => Color.FromRgb(
        (byte)Math.Round((over.R * alpha) + (under.R * (1 - alpha))),
        (byte)Math.Round((over.G * alpha) + (under.G * (1 - alpha))),
        (byte)Math.Round((over.B * alpha) + (under.B * (1 - alpha))));

    private static Color RenderedInk(DependencyObject element)
        => Assert.IsType<SolidColorBrush>(element.GetValue(TextElement.ForegroundProperty)).Color;

    private static string HexOf(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private static Color[] RenderedWash(Border border) => border.Background switch
    {
        SolidColorBrush solid => [solid.Color],
        GradientBrush gradient => gradient.GradientStops.Select(stop => stop.Color).Distinct().ToArray(),
        _ => throw new InvalidOperationException(
            $"A chip wash must be a solid or gradient brush to be measurable; got "
            + $"{border.Background?.GetType().Name ?? "null"}."),
    };

    private static MigrationRecipe CaptureRecipe() => new(
        1,
        "render.capture",
        "Render Capture App",
        "projects",
        new RecipeDetect(KnownFolder.UserProfile, "first.json", true),
        [
            new RecipeItem("first.json", Array.Empty<string>(), Array.Empty<string>()),
            new RecipeItem("second.json", Array.Empty<string>(), Array.Empty<string>()),
        ],
        Array.Empty<string>(),
        "global",
        PortabilityClass.ProfileRelative,
        new RecipeRestore(RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, Array.Empty<string>()));

    private static MigrationSelectionCandidate CaptureCandidate() => new()
    {
        Id = "capture",
        DisplayName = "capture",
        RecipeCategory = "projects",
        Meta = new MigrationItemMeta(
            "render.capture", "capture", PortabilityClass.ProfileRelative,
            RestoreStrategy.ConfigWrite, RestorePhase.ConfigWrite, Array.Empty<string>()),
        RestoreTier = RestoreTier.ConfigCopy,
        SourceKind = MigrationSourceKind.Directory,
        SourcePath = @"C:\Users\render-smoke\capture",
        DestinationPath = @"E:\WCK\capture",
        CloudBackup = CloudBackupStatus.NotBackedUp,
        IsOnSystemDrive = true,
        IsUnique = true,
        IsRegenerable = false,
        IsRecognized = true,
        HasInstallRecord = true,
    };

    private static LeftoverCandidate SecondOwnedRegistryCandidate() => new()
    {
        Action = new RegistryDeleteAction
        {
            Hive = RegistryHive.LocalMachine,
            SubKeyPath = @"SOFTWARE\SomeVendor\SomeApp\Second",
            View = RegistryView.Registry64,
            Description = "second vendor leaf (ProgramOwned)",
            Reason = "owned",
            Risk = RiskLevel.Medium,
            Undo = UndoCapability.Partial,
        },
        Classification = LeftoverClassification.ProgramOwned,
        Selected = true,
        GateReason = string.Empty,
    };

    /// <summary>The Backup VM used by the result-row proof: two copy entries so the mixed executor can land one
    /// and refuse the other, over a temp payload root so every write stays inside the workspace.</summary>
    private static BackupViewModel BuildBackupResultRenderViewModel(I18n i18n, string payloadRoot)
    {
        var gate = TestData.Gate();
        var planner = new BackupPlanner(gate, new FakeEnvironmentExpander(), TestData.PayloadRoots());
        var runner = new BackupRunner(
            new RenderMixedBackupExecutor(),
            new BackupIntegrityWriter(new SanctionedFileWriter()),
            new BackupReportWriter(new LogRedactor(null, null), new SanctionedFileWriter()),
            gate,
            new FakeFileSystem(),
            new FakeHasher(),
            new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        return new BackupViewModel(i18n, new BackupResultRenderManifestLoader(), planner, runner)
        {
            PayloadDir = payloadRoot,
        };
    }

    private sealed class BackupResultRenderManifestLoader : IManifestLoader
    {
        public BackupManifestLoadResult LoadFromDirectory(string manifestsDirectory)
            => BackupManifestLoadResult.Complete(Load());

        public BackupManifestLoadResult LoadFromJson(IEnumerable<string> jsonDocuments)
            => BackupManifestLoadResult.Complete(Load());

        private static BackupManifest Load() => new(new[]
        {
            BackupCopyEntry("landed", @"C:\Users\alice\AppData\Roaming\Tool\landed.json", "tool/landed.json"),
            BackupCopyEntry("refused", @"C:\Users\alice\AppData\Roaming\Tool\refused.json", "tool/refused.json"),
        });
    }

    /// <summary>One copy lands, one fails — the shape the ResultRow factory has to tell apart.</summary>
    private sealed class RenderMixedBackupExecutor : IBackupExecutor
    {
        public BackupExecutionReport Execute(OperationPlan plan, string approvedPlanHash)
        {
            CopyAction[] actions = plan.Actions.OfType<CopyAction>().ToArray();
            Assert.Equal(2, actions.Length);
            return new BackupExecutionReport(true,
            [
                new BackupActionResult(actions[0].Id, BackupActionStatus.Done, "done"),
                new BackupActionResult(actions[1].Id, BackupActionStatus.Failed, "IOException: synthetic failure"),
            ]);
        }
    }

    /// <summary>Reports Done, then Skipped, then NotRun — every status the Install result factory has to tell
    /// apart, so a classifier that handles one not-completed status and misses another cannot pass.</summary>
    private sealed class RenderMixedStatusExecutor : IPlanExecutor
    {
        private static readonly PlanActionStatus[] Sequence =
            [PlanActionStatus.Done, PlanActionStatus.Skipped, PlanActionStatus.NotRun];

        public PlanExecutionReport ExecuteWithReport(OperationPlan plan, string approvedPlanHash)
            => new(true, approvedPlanHash, plan.Actions
                .Select((action, index) => new PlanActionResult(
                    action.Id, action.Kind, Sequence[index % Sequence.Length], "render-mixed"))
                .ToArray());
    }

    /// <summary>The wizard twin of <see cref="RenderMixedStatusExecutor"/>, reporting a failure rather
    /// than a skip so the Failed branch is exercised too.</summary>
    private sealed class RenderFirstDoneThenFailedExecutor : IPlanExecutor
    {
        public PlanExecutionReport ExecuteWithReport(OperationPlan plan, string approvedPlanHash)
            => new(true, approvedPlanHash, plan.Actions
                .Select((action, index) => new PlanActionResult(
                    action.Id, action.Kind,
                    index == 0 ? PlanActionStatus.Done : PlanActionStatus.Failed, "render-mixed"))
                .ToArray());
    }

    /// <summary>A capture runner that plans one action per recipe item and refuses the first
    /// <see cref="FailedActionCount"/> of them at run time.</summary>
    private sealed class RenderCaptureBackupRunner : IMigrationBackupRunner
    {
        public IReadOnlyList<RecipeItemSkip> PlanSkips { get; init; } = Array.Empty<RecipeItemSkip>();
        public int FailedActionCount { get; init; }

        public MigrationBackupPlanResult BuildPlan(
            IEnumerable<MigrationRecipe> recipes, string packageDir, DateTime utc)
        {
            PlannedAction[] actions = recipes
                .SelectMany(recipe => recipe.Items.Select(item => (PlannedAction)new CopyAction
                {
                    Source = Path.Combine(@"C:\Users\render-smoke", item.Path),
                    Destination = Path.Combine(packageDir, recipe.Id, item.Path),
                    Description = recipe.DisplayName,
                    Reason = "migration backup",
                    Risk = RiskLevel.Low,
                    Undo = UndoCapability.None,
                }))
                .ToArray();
            return new MigrationBackupPlanResult(
                new OperationPlan("Migration backup", "migration-backup", actions, utc), PlanSkips);
        }

        public MigrationBackupRunResult Run(
            MigrationBackupPlanResult plan, string approvedPlanHash, string packageDir)
        {
            CopyFileOutcome[] outcomes = plan.Plan.Actions.OfType<CopyAction>()
                .Select((action, index) => new CopyFileOutcome(
                    action.Id, action.Source, action.Destination,
                    index >= FailedActionCount,
                    index >= FailedActionCount ? null : CopySkipReason.Blocked,
                    index >= FailedActionCount ? "done" : "synthetic failure"))
                .ToArray();
            return new MigrationBackupRunResult(
                true,
                new CopySkipReport(outcomes),
                new MigrationRestoreManifest(
                    MigrationRestoreManifest.CurrentSchemaVersion, Array.Empty<MigrationRestoreTarget>()),
                plan.SkippedItems,
                Array.Empty<RecipeItemSkip>());
        }
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
            .Concat(Directory.EnumerateFiles(MigrationModuleViewsPath, "*.xaml", SearchOption.TopDirectoryOnly))
            .Append(MainWindowPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals("SettingsView.xaml", StringComparison.Ordinal));
        Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals("CleanView.xaml", StringComparison.Ordinal));
        Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals("InstallView.xaml", StringComparison.Ordinal));
        Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals("RestoreView.xaml", StringComparison.Ordinal));
        Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals("BackupView.xaml", StringComparison.Ordinal));
        Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals("UninstallView.xaml", StringComparison.Ordinal));
        Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals("MigrationView.xaml", StringComparison.Ordinal));
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
        application.Resources["NonEmptyToVis"] = new NonEmptyToVisibleConverter();
        application.Resources["InverseBoolToVis"] = new InverseBoolToVisibilityConverter();
        return createdApplication;
    }

    private static BackupViewModel BuildBackupRenderViewModel(I18n i18n, IManifestLoader? manifestLoader = null)
    {
        var gate = TestData.Gate();
        var planner = new BackupPlanner(gate, new FakeEnvironmentExpander(), TestData.PayloadRoots());
        var runner = new BackupRunner(
            new NoOpBackupExecutor(),
            new BackupIntegrityWriter(new SanctionedFileWriter()),
            new BackupReportWriter(new LogRedactor(null, null), new SanctionedFileWriter()),
            gate,
            new FakeFileSystem(),
            new FakeHasher(),
            new FakeClock(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)));

        return new BackupViewModel(i18n, manifestLoader ?? new BackupRenderManifestLoader(), planner, runner)
        {
            PayloadDir = @"D:\WCK-BackupOut"
        };
    }

    private static BackupEntry BackupCopyEntry(string id, string source, string target)
        => new(id, true, BackupMethod.Copy, "test", source, target,
            Array.Empty<string>(), SecretHandling.Normal, 50, "merge-after-install", $"{id}: copy configured files", null);

    private sealed class BackupRenderManifestLoader : IManifestLoader
    {
        public BackupManifestLoadResult LoadFromDirectory(string manifestsDirectory)
            => BackupManifestLoadResult.Complete(Load());
        public BackupManifestLoadResult LoadFromJson(IEnumerable<string> jsonDocuments)
            => BackupManifestLoadResult.Complete(Load());

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

    private sealed class FixedBackupRenderManifestLoader(BackupManifestLoadResult result) : IManifestLoader
    {
        public BackupManifestLoadResult LoadFromDirectory(string manifestsDirectory) => result;
        public BackupManifestLoadResult LoadFromJson(IEnumerable<string> jsonDocuments) => result;
    }

    private sealed class NoOpBackupExecutor : IBackupExecutor
    {
        public BackupExecutionReport Execute(OperationPlan plan, string approvedPlanHash)
            => new(false, Array.Empty<BackupActionResult>());
    }

    private static void CleanupApplicationResources(bool shutdownApplication, ResourceDictionary theme)
    {
        Application.Current?.Resources.MergedDictionaries.Remove(theme);
        _ = shutdownApplication;
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

    /// <summary>NEW-07 MAJOR-02 fix: locate a health-note TextBlock BY NAME on a rendered <see cref="CleanView"/>
    /// (not by scanning for matching text, which a Collapsed duplicate could also satisfy) and assert it is
    /// genuinely visible: <see cref="Visibility.Visible"/> AND a non-zero rendered height/bounds after
    /// <c>UpdateLayout</c> — a Collapsed or out-of-layout element fails Measure/Arrange and cannot pass this.</summary>
    /// <summary>M1 (2026-07-31): widened to accept a <see cref="HealthChip"/> as well as a bare
    /// <see cref="TextBlock"/>, because the partial-data notes moved off the red family and into the amber
    /// chip. The assertions are unchanged in strength — exact text, genuinely Visible, non-zero rendered
    /// height — and the resolver REJECTS any other element type rather than skipping, so replacing a note
    /// with something unassertable is a failure, not a silent pass.</summary>
    private static void AssertHealthNoteVisible(FrameworkElement view, string elementName, string expectedText)
    {
        (string text, Visibility visibility, double height) = HealthNote(view, elementName);
        Assert.Equal(expectedText, text);
        Assert.Equal(Visibility.Visible, visibility);
        Assert.True(height > 0, $"{elementName} claimed Visible but had zero rendered height.");
    }

    /// <summary>Resolves a named health note to its text, visibility and rendered height, whichever of the two
    /// sanctioned presentations it uses.</summary>
    private static (string Text, Visibility Visibility, double Height) HealthNote(
        FrameworkElement view, string elementName)
        => view.FindName(elementName) switch
        {
            TextBlock block =>
                (block.Text, block.Visibility, Math.Max(block.ActualHeight, block.RenderSize.Height)),
            HealthChip chip =>
                (chip.Text, chip.Visibility, Math.Max(chip.ActualHeight, chip.RenderSize.Height)),
            null => throw new Xunit.Sdk.XunitException(
                $"'{elementName}' was not found in the rendered view."),
            var other => throw new Xunit.Sdk.XunitException(
                $"'{elementName}' is a {other.GetType().Name}; a health note must be a TextBlock or a HealthChip."),
        };

    /// <summary>NEW-07 MAJOR-02 fix: the healthy-path counterpart — asserts the same named health-note
    /// TextBlock is <see cref="Visibility.Collapsed"/> (no caution text shown) when its source is Complete.
    /// MINOR-02 (2026-07-29): widened from <c>CleanView</c> to <see cref="FrameworkElement"/> so the Install and
    /// Backup manifest notes can use it too — the absence of a warning is a claim that needs proving as much as
    /// its presence does.</summary>
    private static void AssertHealthNoteCollapsed(FrameworkElement view, string elementName)
    {
        (_, Visibility visibility, _) = HealthNote(view, elementName);
        Assert.Equal(Visibility.Collapsed, visibility);
    }

    private static void AssertVisibleRowText(IEnumerable<TextBlock> blocks, string expectedText)
    {
        TextBlock block = Assert.Single(blocks, text => text.Text == expectedText);
        Assert.Equal(Visibility.Visible, block.Visibility);
        Assert.True(
            block.ActualHeight > 0 || block.RenderSize.Height > 0,
            $"row text '{expectedText}' claimed Visible but had zero rendered height.");
    }

    private static void AssertInside(FrameworkElement ancestor, FrameworkElement element, double maxWidth, string label)
    {
        Rect bounds = element.TransformToAncestor(ancestor)
            .TransformBounds(new Rect(new Point(0, 0), element.RenderSize));
        Assert.True(bounds.Left >= -0.5, $"{label} left edge was clipped: {bounds}");
        Assert.True(bounds.Right <= maxWidth + 0.5, $"{label} right edge was clipped: {bounds}");
        Assert.True(element.ActualWidth > 0, $"{label} had no rendered width.");
    }

    /// <summary>
    /// Render-smoke tests that fire fire-and-forget async commands must marshal the awaited
    /// continuations back to THIS STA thread. Without a SynchronizationContext an
    /// <c>await Task.Run(...)</c> continuation resumes on a ThreadPool thread and mutates a
    /// UI-bound <see cref="System.Collections.ObjectModel.ObservableCollection{T}"/> off the
    /// Dispatcher thread — a NotSupportedException that crashes the whole test host (surfaces only
    /// under the full suite's ThreadPool pressure, so it reads as a flaky CI failure). Installing a
    /// <see cref="DispatcherSynchronizationContext"/> makes the continuations post here;
    /// <see cref="PumpAsyncWork"/> then drains them before the view binds a CollectionView. Scoped
    /// to the async-command tests so the blocking <c>.GetAwaiter().GetResult()</c> render cases keep
    /// working (installing it globally would deadlock those).
    /// </summary>
    private static void InstallDispatcherSyncContext()
        => SynchronizationContext.SetSynchronizationContext(
            new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));

    /// <summary>Pump the STA thread's Dispatcher until <paramref name="settled"/> holds (or the
    /// timeout elapses), running any posted async continuations on THIS thread, then flush once more
    /// so trailing PropertyChanged/RaiseAll work also runs here rather than racing the render.</summary>
    private static void PumpAsyncWork(Func<bool> settled, TimeSpan timeout)
    {
        Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
        DateTime deadline = DateTime.UtcNow + timeout;
        while (!settled() && DateTime.UtcNow < deadline)
        {
            dispatcher.Invoke(() => { }, DispatcherPriority.Background);
            Thread.Sleep(5);
        }

        dispatcher.Invoke(() => { }, DispatcherPriority.Background);
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        RenderDispatcher.Value.Invoke(() =>
        {
            SynchronizationContext? previousContext = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(null);
                AssertNoBindingWarnings(action);
            }
            catch (Exception ex)
            {
                failure = ex;
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previousContext);
            }
        });

        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Dispatcher CreateRenderDispatcher()
    {
        Dispatcher? dispatcher = null;
        Exception? failure = null;
        using var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                dispatcher = Dispatcher.CurrentDispatcher;
                ready.Set();
                Dispatcher.Run();
            }
            catch (Exception ex)
            {
                failure = ex;
                ready.Set();
            }
        })
        {
            IsBackground = true,
            Name = "ViewRenderSmokeTests.STA",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(ready.Wait(TimeSpan.FromSeconds(10)), "render STA dispatcher did not start in time");
        if (failure is not null)
            ExceptionDispatchInfo.Capture(failure).Throw();
        return dispatcher ?? throw new InvalidOperationException("render STA dispatcher was not created");
    }

    internal static void AssertNoBindingWarnings(Action render)
    {
        TraceSource source = PresentationTraceSources.DataBindingSource;
        var listener = new BindingTraceListener(Environment.CurrentManagedThreadId);
        lock (BindingTraceLock)
        {
            if (BindingTraceScopes++ == 0)
                BindingTracePreviousLevel = source.Switch.Level;
            source.Listeners.Add(listener);
            source.Switch.Level = SourceLevels.Warning | SourceLevels.Error;
        }

        try
        {
            render();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.DataBind);
        }
        finally
        {
            lock (BindingTraceLock)
            {
                source.Listeners.Remove(listener);
                if (--BindingTraceScopes == 0)
                    source.Switch.Level = BindingTracePreviousLevel;
            }
        }

        Assert.True(listener.Messages.Length == 0,
            "WPF binding warning/error during render:" + Environment.NewLine + listener.Messages);
    }

    internal static void AssertNoBindingErrors(DependencyObject root)
    {
        var failures = new List<string>();
        var pending = new Stack<DependencyObject>();
        var visited = new HashSet<DependencyObject>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            DependencyObject current = pending.Pop();
            if (!visited.Add(current))
                continue;

            LocalValueEnumerator values = current.GetLocalValueEnumerator();
            while (values.MoveNext())
            {
                LocalValueEntry entry = values.Current;
                BindingExpressionBase? expression = BindingOperations.GetBindingExpressionBase(current, entry.Property);
                if (expression?.Status != BindingStatus.PathError)
                    continue;

                string path = expression.ParentBindingBase is Binding binding
                    ? binding.Path?.Path ?? "(no path)"
                    : expression.ParentBindingBase.ToString() ?? "(unknown binding)";
                failures.Add($"{current.GetType().Name}.{entry.Property.Name}: PathError for '{path}'");
            }

            if (current is Visual || current is System.Windows.Media.Media3D.Visual3D)
            {
                int visualCount = VisualTreeHelper.GetChildrenCount(current);
                for (int i = 0; i < visualCount; i++)
                    pending.Push(VisualTreeHelper.GetChild(current, i));
            }

            foreach (object child in LogicalTreeHelper.GetChildren(current))
                if (child is DependencyObject dependencyObject)
                    pending.Push(dependencyObject);
        }

        Assert.True(failures.Count == 0,
            "WPF binding PathError during render:" + Environment.NewLine + string.Join(Environment.NewLine, failures));
    }

    private sealed class BindingTraceListener(int ownerThreadId) : TraceListener
    {
        private readonly StringBuilder _messages = new();

        public string Messages => _messages.ToString();

        public override void Write(string? message)
        {
            if (Environment.CurrentManagedThreadId == ownerThreadId)
                _messages.Append(message);
        }

        public override void WriteLine(string? message)
        {
            if (Environment.CurrentManagedThreadId == ownerThreadId)
                _messages.AppendLine(message);
        }
    }

    private static string ViewsPath => Path.Combine(RepoRoot, "src", "Suite.App.Wpf", "Views");
    private static string CleanModuleViewsPath => Path.Combine(RepoRoot, "src", "Suite.Module.Clean", "Views");
    private static string InstallModuleViewsPath => Path.Combine(RepoRoot, "src", "Suite.Module.Install", "Views");
    private static string RestoreModuleViewsPath => Path.Combine(RepoRoot, "src", "Suite.Module.Restore", "Views");
    private static string BackupModuleViewsPath => Path.Combine(RepoRoot, "src", "Suite.Module.Backup", "Views");
    private static string UninstallModuleViewsPath => Path.Combine(RepoRoot, "src", "Suite.Module.Uninstall", "Views");
    private static string MigrationModuleViewsPath => Path.Combine(RepoRoot, "src", "Suite.Module.Migration", "Views");
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
                new(i18n, "nav.install", "", new object(), "nav.install.desc"),
                new(i18n, "nav.restore", "", new object(), "nav.restore.desc"),
                new(i18n, "nav.settings", "", new ShellProbeSettings(), "nav.settings.desc", isSettings: true),
            ];
            SelectedNav = Nav[0];
            FeatureNav = Nav.Where(item => !item.IsSettings).ToArray();
            SettingsNav = Nav.Where(item => item.IsSettings).ToArray();
            SettingsNavItem = SettingsNav.SingleOrDefault();
            SelectNavCommand = new RelayCommand(parameter =>
            {
                NavItem? target = Nav.SingleOrDefault(item =>
                    item.Id.Equals(parameter as string, StringComparison.OrdinalIgnoreCase));
                if (target is not null)
                    SelectedNav = target;
            });
            DismissFirstRunCommand = new RelayCommand(() => ShowFirstRun = false);
        }

        public I18n I18n { get; }
        public IReadOnlyList<NavItem> Nav { get; }
        public IReadOnlyList<NavItem> FeatureNav { get; }
        public IReadOnlyList<NavItem> SettingsNav { get; }
        public NavItem? SettingsNavItem { get; }
        public NavItem SelectedNav { get; set; }
        public NavItem? SelectedFeatureNav
        {
            get => SelectedNav.IsSettings ? null : SelectedNav;
            set
            {
                if (value is not null)
                    SelectedNav = value;
            }
        }
        public NavItem? SelectedSettingsNav
        {
            get => SelectedNav.IsSettings ? SelectedNav : null;
            set
            {
                if (value is not null)
                    SelectedNav = value;
            }
        }
        public object CurrentContent => SelectedNav.Content;
        public string ModuleHealthNotice => string.Empty;
        public bool ShowFirstRun { get; set; }
        public ICommand SelectNavCommand { get; }
        public ICommand DismissFirstRunCommand { get; }
    }

    private sealed class ShellProbeSettings
    {
        public string RepositoryUrl => SettingsViewModel.ProjectRepositoryUrl;
        public ICommand OpenExternalLinkCommand { get; } = new RelayCommand(_ => { });
    }

    private sealed class RenderShellModule : IWckModule
    {
        public string Id => "settings";
        public string TitleKey => "nav.settings";
        public string DescKey => "nav.settings.desc";
        public string IconKey => "\uE713";
        public int Order => 900;
        public bool IsSettings => true;

        public void RegisterServices(IServiceCollection services)
        {
        }

        public object CreateContent(IServiceProvider sp) => new Border();

        public FrameworkElement? CreateView() => null;
    }

    private sealed class FakeInstalledAppReader(params InstalledApp[] apps) : IInstalledAppReader
    {
        public IReadOnlyList<InstalledApp> ReadAll() => apps;
    }

    private sealed class FakeAppxReader : IAppxReader
    {
        public IReadOnlyList<InstalledAppx> ReadCurrentUserPackages() => Array.Empty<InstalledAppx>();
    }

    private sealed class FakeExecutor : IPlanExecutor
    {
        public PlanExecutionReport ExecuteWithReport(OperationPlan plan, string approvedPlanHash)
            => new(true, approvedPlanHash,
                plan.Actions.Select(a => new PlanActionResult(a.Id, a.Kind, PlanActionStatus.Done, "not used")).ToArray());
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

    private sealed class RenderFakeStartupProbe(StartupInventory inventory) : IStartupProbe
    {
        public RenderFakeStartupProbe(params StartupEntry[] entries)
            : this(new StartupInventory(entries, SourceHealth.Complete, Array.Empty<InventorySourceFault>())) { }

        public StartupInventory ReadAll() => inventory;
    }

    private sealed class RenderFakeBrowserExtensionInventory(BrowserExtensionListing listing) : IBrowserExtensionInventory
    {
        public RenderFakeBrowserExtensionInventory()
            : this(new BrowserExtensionListing(Array.Empty<BrowserExtension>(), SourceHealth.Complete, Array.Empty<InventorySourceFault>())) { }

        public BrowserExtensionListing ReadAll() => listing;
    }

    private sealed class RenderFakeRecycleBinService(RecycleBinInventory inventory) : IRecycleBinService
    {
        public RenderFakeRecycleBinService(RecycleBinStats stats) : this(RecycleBinInventory.Complete(stats)) { }

        public RecycleBinInventory Query() => inventory;
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

    private sealed class RenderFakeManifestLoader : IInstallManifestLoader
    {
        private readonly InstallManifestLoadResult _result;

        public RenderFakeManifestLoader(params InstallEntry[] entries)
            : this(InstallManifestLoadResult.Loaded(new InstallManifest(entries), "<render>"))
        {
        }

        public RenderFakeManifestLoader(InstallManifestLoadResult result) => _result = result;

        public InstallManifestLoadResult Load(string manifestPath) => _result;
        public InstallManifestLoadResult Parse(string json) => _result;
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
            => TryLoad(stateDirectory).State;

        public RestoreStateLoad TryLoad(string stateDirectory)
            => _byDir.TryGetValue(stateDirectory, out RestoreState? state)
                ? RestoreStateLoad.Loaded(state)
                : RestoreStateLoad.Missing;

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
