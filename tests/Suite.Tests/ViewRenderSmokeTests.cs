using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Xml;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Theming;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.App.Views;
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
}
