using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.App.ViewModels;
using Xunit;
using WpfApp = WindowsCareKit.App.App;

namespace WindowsCareKit.Tests;

[Collection(WpfResourceCollection.Name)]
public sealed class RailShellTests
{
    private static readonly string[] RailIds =
        ["uninstall", "clean", "backup", "migration", "install", "restore", "settings"];

    [Fact]
    public void Ctrl_1_through_7_drive_window_bindings_and_select_rail_targets()
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources("Strongbox", out ResourceDictionary theme);
            try
            {
                MainViewModel vm = CreateShell();
                var window = new MainWindow { DataContext = vm };

                Key[] keys = [Key.D1, Key.D2, Key.D3, Key.D4, Key.D5, Key.D6, Key.D7];
                for (int index = 0; index < keys.Length; index++)
                {
                    KeyBinding binding = Assert.Single(
                        window.InputBindings.OfType<KeyBinding>(),
                        candidate =>
                            candidate.Key == keys[index] &&
                            candidate.Modifiers == ModifierKeys.Control);

                    Assert.NotNull(binding.Command);
                    Assert.True(binding.Command.CanExecute(binding.CommandParameter));
                    binding.Command.Execute(binding.CommandParameter);

                    Assert.Equal(RailIds[index], vm.SelectedNav.Id);
                }
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    [Fact]
    public void Default_sorted_module_catalog_follows_rail_order_with_settings_trailing()
    {
        string[] sortedCatalog = WpfApp.CreateDefaultModules()
            .OrderBy(module => module.Order)
            .Select(module => module.Id)
            .ToArray();

        Assert.Equal(RailIds, sortedCatalog);
        Assert.True(WpfApp.CreateDefaultModules().Single(module => module.IsSettings).Order >
                    WpfApp.CreateDefaultModules().Where(module => !module.IsSettings).Max(module => module.Order));
    }

    [Fact]
    public void Every_rail_target_is_one_direct_selection_from_every_other_target()
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources("Strongbox", out ResourceDictionary theme);
            try
            {
                MainViewModel vm = CreateShell();
                var window = new MainWindow { DataContext = vm };
                FrameworkElement root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                var size = new Size(1100, 720);
                root.Measure(size);
                root.Arrange(new Rect(size));
                root.UpdateLayout();

                var featureList = Assert.IsType<ListBox>(window.FindName("FeatureRailNav"));
                var settingsList = Assert.IsType<ListBox>(window.FindName("SettingsRailNav"));
                Assert.Equal(6, featureList.Items.Count);
                Assert.Single(settingsList.Items);

                foreach (NavItem target in vm.Nav)
                {
                    ListBox owner = target.IsSettings ? settingsList : featureList;
                    var container = Assert.IsType<ListBoxItem>(
                        owner.ItemContainerGenerator.ContainerFromItem(target));
                    Assert.True(container.IsEnabled);
                    Assert.True(container.IsHitTestVisible);
                }

                foreach (NavItem source in vm.Nav)
                {
                    foreach (NavItem target in vm.Nav.Where(item => !ReferenceEquals(item, source)))
                    {
                        vm.SelectedNav = source;
                        ListBox owner = target.IsSettings ? settingsList : featureList;

                        owner.SelectedItem = target;

                        Assert.Same(target, vm.SelectedNav);
                    }
                }
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
    public void Rail_layout_stays_inside_measured_bounds_from_980_through_1920(string themeName)
    {
        RunOnStaThread(() =>
        {
            bool createdApplication = EnsureApplicationResources(themeName, out ResourceDictionary theme);
            try
            {
                MainViewModel vm = CreateShell();
                var window = new MainWindow { DataContext = vm };
                FrameworkElement root = Assert.IsAssignableFrom<FrameworkElement>(window.Content);
                var body = Assert.IsType<Grid>(window.FindName("ShellBodyGrid"));
                var rail = Assert.IsType<Border>(window.FindName("TaskRail"));
                var content = Assert.IsType<Grid>(window.FindName("ContentColumn"));
                var featureList = Assert.IsType<ListBox>(window.FindName("FeatureRailNav"));
                var promises = Assert.IsType<Border>(window.FindName("PromisesFooter"));
                var settingsList = Assert.IsType<ListBox>(window.FindName("SettingsRailNav"));
                var moduleHost = Assert.IsType<ContentControl>(window.FindName("ModuleContentHost"));

                for (int width = 980; width <= 1920; width++)
                {
                    var size = new Size(width, 720);
                    root.Measure(size);
                    root.Arrange(new Rect(size));
                    root.UpdateLayout();

                    Rect railBounds = BoundsIn(body, rail);
                    Rect contentBounds = BoundsIn(body, content);
                    Assert.False(HasPositiveAreaOverlap(railBounds, contentBounds),
                        $"Shell columns intersected at width {width}: {railBounds} and {contentBounds}.");
                    AssertInside(body, rail, $"rail at width {width}");
                    AssertInside(body, content, $"content at width {width}");
                    Assert.Equal(200, rail.ActualWidth);

                    Rect featuresBounds = BoundsIn(rail, featureList);
                    Rect promisesBounds = BoundsIn(rail, promises);
                    Rect settingsBounds = BoundsIn(rail, settingsList);
                    Assert.True(featuresBounds.Bottom <= promisesBounds.Top + 0.5,
                        $"Feature rows intersected the promises footer at width {width}.");
                    Assert.True(promisesBounds.Bottom <= settingsBounds.Top + 0.5,
                        $"Promises footer intersected Settings at width {width}.");
                    AssertInside(rail, featureList, $"feature list at width {width}");
                    AssertInside(rail, promises, $"promises footer at width {width}");
                    AssertInside(rail, settingsList, $"settings list at width {width}");

                    foreach (ListBox list in new[] { featureList, settingsList })
                    {
                        var itemBounds = new List<Rect>();
                        foreach (NavItem item in list.Items)
                        {
                            var container = Assert.IsType<ListBoxItem>(
                                list.ItemContainerGenerator.ContainerFromItem(item));
                            AssertInside(list, container, $"{item.Id} item at width {width}");
                            itemBounds.Add(BoundsIn(list, container));

                            TextBlock[] itemText = Descendants<TextBlock>(container).ToArray();
                            TextBlock label = itemText.Single(block => block.Text == item.Title);
                            TextBlock glyph = itemText.Single(block => block.Text == item.Glyph);
                            Assert.Equal(TextTrimming.CharacterEllipsis, label.TextTrimming);
                            AssertInside(container, label, $"{item.Id} label at width {width}");
                            AssertInside(container, glyph, $"{item.Id} glyph at width {width}");
                            Assert.False(
                                HasPositiveAreaOverlap(
                                    BoundsIn(container, glyph),
                                    BoundsIn(container, label)),
                                $"{item.Id} glyph intersected its label at width {width}.");
                            Assert.True(label.DesiredSize.Width <= container.RenderSize.Width + 0.5,
                                $"{item.Id} label desired width overran its measured item at width {width}.");
                        }

                        AssertNoPositiveAreaOverlap(itemBounds, $"{list.Name} items at width {width}");
                    }

                    TextBlock[] promiseLines = Descendants<TextBlock>(promises).ToArray();
                    Assert.Equal(3, promiseLines.Length);
                    foreach (TextBlock line in promiseLines)
                        AssertInside(promises, line, $"promise line at width {width}");
                    AssertNoPositiveAreaOverlap(
                        promiseLines.Select(line => BoundsIn(promises, line)).ToList(),
                        $"promise lines at width {width}");

                    AssertInside(content, moduleHost, $"module host at width {width}");
                    Assert.DoesNotContain(Ancestors(moduleHost), ancestor => ancestor is ScrollViewer);
                }
            }
            finally
            {
                CleanupApplicationResources(createdApplication, theme);
            }
        });
    }

    private static MainViewModel CreateShell()
    {
        I18n i18n = TestI18n.Full("en");
        IWckModule[] modules =
        [
            new RailModule("restore", "nav.restore", "nav.restore.desc", "\uE81C", ModuleOrder.Restore, false),
            new RailModule("clean", "nav.clean", "nav.clean.desc", "\uE75C", ModuleOrder.Clean, false),
            new RailModule("settings", "nav.settings", "nav.settings.desc", "\uE713", ModuleOrder.Settings, true),
            new RailModule("install", "nav.install", "nav.install.desc", "\uE896", ModuleOrder.Install, false),
            new RailModule("migration", "nav.migration", "nav.migration.desc", "\uE7AD", ModuleOrder.Migration, false),
            new RailModule("backup", "nav.backup", "nav.backup.desc", "\uE74E", ModuleOrder.Backup, false),
            new RailModule("uninstall", "nav.uninstall", "nav.uninstall.desc", "\uE74D", ModuleOrder.Uninstall, false),
        ];

        return new MainViewModel(i18n, modules, TestHelpers.NoComponentsDiscovered())
        {
            ShowFirstRun = false,
        };
    }

    private static Rect BoundsIn(Visual ancestor, FrameworkElement element)
        => element.TransformToAncestor(ancestor)
            .TransformBounds(new Rect(new Point(0, 0), element.RenderSize));

    private static bool HasPositiveAreaOverlap(Rect left, Rect right)
        => Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left) > 0.5 &&
           Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top) > 0.5;

    private static void AssertNoPositiveAreaOverlap(IReadOnlyList<Rect> bounds, string label)
    {
        for (int left = 0; left < bounds.Count; left++)
        {
            for (int right = left + 1; right < bounds.Count; right++)
            {
                Assert.False(
                    HasPositiveAreaOverlap(bounds[left], bounds[right]),
                    $"{label} intersected: {bounds[left]} and {bounds[right]}.");
            }
        }
    }

    private static void AssertInside(FrameworkElement ancestor, FrameworkElement element, string label)
    {
        Rect bounds = BoundsIn(ancestor, element);
        Assert.True(bounds.Left >= -0.5, $"{label} crossed its parent's left edge: {bounds}.");
        Assert.True(bounds.Top >= -0.5, $"{label} crossed its parent's top edge: {bounds}.");
        Assert.True(bounds.Right <= ancestor.RenderSize.Width + 0.5,
            $"{label} crossed its parent's right edge: {bounds}.");
        Assert.True(bounds.Bottom <= ancestor.RenderSize.Height + 0.5,
            $"{label} crossed its parent's bottom edge: {bounds}.");
    }

    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int index = 0; index < count; index++)
        {
            DependencyObject child = VisualTreeHelper.GetChild(root, index);
            if (child is T typed)
                yield return typed;

            foreach (T descendant in Descendants<T>(child))
                yield return descendant;
        }
    }

    private static IEnumerable<DependencyObject> Ancestors(DependencyObject child)
    {
        DependencyObject? current = VisualTreeHelper.GetParent(child);
        while (current is not null)
        {
            yield return current;
            current = VisualTreeHelper.GetParent(current);
        }
    }

    private static bool EnsureApplicationResources(string themeName, out ResourceDictionary theme)
    {
        bool createdApplication = Application.Current is null;
        Application application = Application.Current ??
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        theme = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/WindowsCareKit;component/Themes/{themeName}.xaml",
                UriKind.Absolute)
        };
        application.Resources.MergedDictionaries.Add(theme);
        application.Resources["BoolToVis"] = new BooleanToVisibilityConverter();
        application.Resources["NonEmptyToVis"] = new NonEmptyToVisibleConverter();
        return createdApplication;
    }

    private static void CleanupApplicationResources(bool shutdownApplication, ResourceDictionary theme)
    {
        Application.Current?.Resources.MergedDictionaries.Remove(theme);
        _ = shutdownApplication;
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? exception = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                exception = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (exception is not null)
            ExceptionDispatchInfo.Capture(exception).Throw();
    }

    private sealed class RailModule(
        string id,
        string titleKey,
        string descKey,
        string iconKey,
        int order,
        bool isSettings) : IWckModule
    {
        public string Id => id;
        public string TitleKey => titleKey;
        public string DescKey => descKey;
        public string IconKey => iconKey;
        public int Order => order;
        public bool IsSettings => isSettings;

        public void RegisterServices(IServiceCollection services)
        {
        }

        public object CreateContent(IServiceProvider services) => new Border();

        public FrameworkElement? CreateView() => null;
    }
}
