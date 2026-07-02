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
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.App.Theming;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.App.Views;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
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

    [Fact]
    public void No_view_binds_TwoWay_to_the_I18n_indexer()
    {
        string[] xamlFiles = Directory.EnumerateFiles(ViewsPath, "*.xaml", SearchOption.TopDirectoryOnly)
            .Append(MainWindowPath)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Contains(xamlFiles, path => Path.GetFileName(path).Equals("SettingsView.xaml", StringComparison.Ordinal));
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
        => new()
        {
            Source = new Uri(
                "pack://application:,,,/WindowsCareKit;component/Themes/Strongbox.xaml",
                UriKind.Absolute)
        };

    private static bool EnsureApplicationResources(out ResourceDictionary theme)
    {
        bool createdApplication = Application.Current is null;
        Application application = Application.Current ?? new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        theme = LoadStrongboxTheme();
        application.Resources.MergedDictionaries.Add(theme);
        application.Resources["BoolToVis"] = new BooleanToVisibilityConverter();
        application.Resources["ZeroToVis"] = new ZeroToVisibleConverter();
        application.Resources["PositiveToVis"] = new PositiveToVisibleConverter();
        application.Resources["InverseBoolToVis"] = new InverseBoolToVisibilityConverter();
        return createdApplication;
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

    private sealed class FakeInstalledAppReader : IInstalledAppReader
    {
        public IReadOnlyList<InstalledApp> ReadAll() => Array.Empty<InstalledApp>();
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
}
