using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WindowsCareKit.App.Controls;
using WindowsCareKit.App.Execution;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Mvvm;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Module.Uninstall.ViewModels;
using WindowsCareKit.Module.Uninstall.Views;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// The M3 Uninstall screen's acceptance, asserted from a RENDERED visual tree rather than from the view model.
/// <para>
/// <c>UninstallRemovalTests</c> proves the arithmetic and the composition; these prove the seam — that the
/// typed state reaches the screen unchanged, that a required step cannot present itself as a choice, and
/// that nothing the gate hides goes unannounced.
/// </para>
/// </summary>
[Collection(WpfResourceCollection.Name)]
public sealed class UninstallScreenTests
{
    /// <summary>The main column's width range. The shell's rail is a fixed 200 px, so a window of 980–1920 —
    /// the app's MinWidth through a 1080p desktop — hands the module exactly 780–1720.</summary>
    private const int MinContentWidth = 780;
    private const int MaxContentWidth = 1720;

    private const string UninstallString = "\"C:\\Program Files\\SomeApp\\uninst.exe\" /S";
    private const string InstallLocation = @"C:\Program Files\SomeApp";

    // ---- A-M3-2: a required step is a lock badge, never a checkbox ----

    /// <summary>
    /// A-M3-2. The restore point and the vendor uninstall render the Required lock badge and carry no checkbox
    /// ELEMENT — not a disabled one, not a collapsed one, because a disabled checkbox still says "this is a
    /// choice" (SPEC §2.1 rule 2). An optional leftover carries exactly one, and a <c>WON'T RUN</c> row
    /// carries neither, so this is not passing because the sweep found nothing.
    /// </summary>
    [Fact]
    public void A_required_gate_row_shows_a_lock_badge_and_no_checkbox_while_an_optional_row_shows_one()
    {
        RunRendered("Strongbox", leftovers: 1, (view, vm, _) =>
        {
            OpenGate(view, vm);

            DependencyObject restorePoint = GateRow(view, 0);
            DependencyObject vendor = GateRow(view, 1);
            DependencyObject optional = GateRow(view, 2);
            DependencyObject blocked = GateRow(view, 3);

            Assert.IsType<CreateRestorePointAction>(vm.Gate.Rows[0].Action);
            Assert.IsType<CommandAction>(vm.Gate.Rows[1].Action);
            Assert.True(vm.Gate.Rows[2].IsVetoable);
            Assert.True(vm.Gate.Rows[3].IsSkipped);

            foreach (DependencyObject required in new[] { restorePoint, vendor })
            {
                Assert.Empty(Descendants<CheckBox>(required));
                Assert.Contains(Descendants<FamilyChip>(required),
                    chip => chip.Glyph == ChipGlyphs.Locked && chip.Family == ChipFamily.Neutral);
            }

            Assert.Single(Descendants<CheckBox>(optional));
            Assert.DoesNotContain(Descendants<FamilyChip>(optional), chip => chip.Glyph == ChipGlyphs.Locked);

            // The blocked row states its refusal with a chip and a reason, and offers no control at all.
            Assert.Empty(Descendants<CheckBox>(blocked));
            Assert.Contains(Descendants<RiskChip>(blocked), chip => chip.IsBlocked);
        });
    }

    // ---- A-M3-4 / SPEC §1.3: nothing the fold hides goes unannounced ----

    /// <summary>
    /// A-M3-4. With enough rows to overflow the gate's viewport, the bottom affordance is VISIBLE, it states
    /// the hidden count, and — the whole reason §1.3 exists — when the clipped content includes a
    /// <c>WON'T RUN</c> row, the affordance names it rather than folding it into a generic total.
    /// </summary>
    [Fact]
    public void An_overflowing_gate_announces_what_it_hides_and_names_the_clipped_refusal()
    {
        RunRendered("Strongbox", leftovers: 16, (view, vm, _) =>
        {
            OpenGate(view, vm);

            var scroll = GateNamed<ScrollViewer>(view, "GateScroll");
            var affordance = GateNamed<FrameworkElement>(view, "GateOverflowAffordance");
            var text = GateNamed<TextBlock>(view, "GateOverflowText");

            // Non-vacuity: the fixture really did overflow.
            Assert.True(scroll.ScrollableHeight > 0,
                $"the gate did not overflow (scrollable height {scroll.ScrollableHeight}).");

            Assert.True(IsShown(affordance, view), "the gate clipped content without announcing it.");
            Assert.True(vm.Gate.ClippedRowCount > 0);
            Assert.Contains(vm.Gate.ClippedRowCount.ToString(), text.Text, StringComparison.Ordinal);

            // The last row is the WON'T RUN row, so the refusal is exactly what the fold hides.
            Assert.True(vm.Gate.Rows[^1].IsSkipped);
            Assert.True(vm.Gate.ClippedBlockedCount > 0,
                "a skipped row was clipped and the affordance did not count it.");
            Assert.Contains(vm.Gate.ClippedBlockedCount.ToString(), text.Text, StringComparison.Ordinal);
        });
    }

    /// <summary>The other half: a gate that hides nothing must not cry wolf, or the affordance stops carrying
    /// information the moment it matters.</summary>
    [Fact]
    public void A_gate_that_fits_renders_no_overflow_affordance()
    {
        RunRendered("Strongbox", leftovers: 0, shared: false, (view, vm, _) =>
        {
            OpenGate(view, vm);

            var scroll = GateNamed<ScrollViewer>(view, "GateScroll");
            Assert.Equal(2, vm.Gate.Rows.Count);   // the required pair, and nothing else to hide
            Assert.Equal(0, scroll.ScrollableHeight);
            Assert.Equal(0, vm.Gate.ClippedRowCount);
            Assert.False(IsShown(GateNamed<FrameworkElement>(view, "GateOverflowAffordance"), view));
        });
    }

    // ---- The gate counter reaches the screen, and no language moves it ----

    /// <summary>
    /// The "n of m optional leftovers included" line is arithmetic over <see cref="PlanRow.Selection"/>, and
    /// it reaches the screen that way: checking a box moves it, and switching the language re-renders every
    /// localized string on the gate and moves not one number. Written at the seam on purpose — a counter that
    /// parsed a rendered row would satisfy a unit test of itself under a single language.
    /// </summary>
    [Fact]
    public void The_gate_counter_moves_with_the_veto_and_not_with_the_language()
    {
        RunRendered("Strongbox", leftovers: 3, (view, vm, i18n) =>
        {
            OpenGate(view, vm);
            var counter = GateNamed<TextBlock>(view, "GateOptionalCounterText");

            Assert.Equal(3, vm.Gate.OptionalTotalCount);
            Assert.Contains("0 of 3", counter.Text, StringComparison.Ordinal);

            vm.Gate.Rows.First(row => row.IsVetoable).IsIncluded = true;
            view.UpdateLayout();
            Assert.Equal(1, vm.Gate.OptionalIncludedCount);
            Assert.Contains("1 of 3", counter.Text, StringComparison.Ordinal);

            string counterBefore = counter.Text;
            string bannerBefore = GateNamed<TextBlock>(view, "GateBannerText").Text;

            i18n.Load("tr");
            view.UpdateLayout();

            // Non-vacuity: the rendered PROSE really did switch language on this very gate — the counter
            // sentence and the tier banner beside it are both different strings now…
            Assert.NotEqual(counterBefore, counter.Text);
            Assert.NotEqual(bannerBefore, GateNamed<TextBlock>(view, "GateBannerText").Text);

            // …and not one number moved with it.
            Assert.Equal(1, vm.Gate.OptionalIncludedCount);
            Assert.Equal(3, vm.Gate.OptionalTotalCount);
            Assert.Contains("1", counter.Text, StringComparison.Ordinal);
            Assert.Contains("3", counter.Text, StringComparison.Ordinal);
        });
    }

    // ---- A-M3-6: partial inventory is amber attention, never red ----

    [Fact]
    public void The_inventory_notice_renders_in_the_amber_attention_family()
    {
        RunRendered("Strongbox", leftovers: 1, (view, vm, i18n) =>
        {
            var notice = Named<HealthChip>(view, "UninstallInventoryNoteText");

            Assert.True(vm.HasInventoryNotice);
            Assert.Equal(i18n["uninstall.inventory.partial"], notice.Text);
            Assert.True(IsShown(notice, view));
            Assert.Equal(ChipFamily.Attention, HealthChip.Family);

            FamilyChip chip = Assert.Single(Descendants<FamilyChip>(notice));
            Assert.Equal(ChipFamily.Attention, chip.Family);
            Assert.NotEqual(ChipFamily.Irreversible, chip.Family);
        });
    }

    // ---- A-M3-1: one scan action, and the literal command on screen ----

    [Fact]
    public void The_rail_offers_exactly_one_scan_action_and_prints_the_literal_vendor_command()
    {
        RunRendered("Strongbox", leftovers: 1, (view, vm, i18n) =>
        {
            Button[] scanButtons = Descendants<Button>(Named<FrameworkElement>(view, "RemovalRail"))
                .Where(button => ReferenceEquals(button.Command, vm.ScanLeftoversCommand))
                .ToArray();
            Assert.Single(scanButtons);
            Assert.Equal(i18n["uninstall.removal.scan"], scanButtons[0].Content as string);

            // The literal command, glyph for glyph, from the STAGED action's typed fields — and it wraps
            // rather than ellipsizing, because a path the user cannot read in full is one they cannot check.
            var command = Named<TextBlock>(view, "VendorCommandText");
            Assert.Contains("uninst.exe", command.Text, StringComparison.Ordinal);
            Assert.Contains("/S", command.Text, StringComparison.Ordinal);
            Assert.Equal(TextTrimming.None, command.TextTrimming);
            Assert.Equal(TextWrapping.Wrap, command.TextWrapping);
        });
    }

    // ---- SPEC §5 L-1..L-5: measured layout, not eyeballed ----

    /// <summary>
    /// The responsive acceptance, measured. At every integer width the module can receive, the master grid and
    /// the removal rail never intersect, every element stays inside its parent, no label wants more width than
    /// it was measured into, and nothing overflows horizontally into a hidden region.
    /// </summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void Uninstall_layout_stays_inside_measured_bounds_across_the_whole_content_width_range(
        string themeName)
    {
        RunRendered(themeName, leftovers: 2, (view, _, _) =>
        {
            var body = Named<FrameworkElement>(view, "UninstallBody");
            var master = Named<FrameworkElement>(view, "MasterColumn");
            var rail = Named<FrameworkElement>(view, "RemovalRail");
            var railScroll = Named<ScrollViewer>(view, "RailScroll");
            var commandBar = Named<FrameworkElement>(view, "UninstallCommandBar");
            var rows = Named<ItemsControl>(view, "LeftoverRowsHost");

            for (int width = MinContentWidth; width <= MaxContentWidth; width++)
            {
                Layout(view, width);

                Rect masterBounds = BoundsIn(body, master);
                Rect railBounds = BoundsIn(body, rail);
                Assert.False(Intersects(masterBounds, railBounds),
                    $"The master grid and the removal rail intersected at width {width}: "
                    + $"{masterBounds} / {railBounds}.");
                AssertInside(body, master, $"master grid at width {width}");
                AssertInside(body, rail, $"removal rail at width {width}");
                Assert.Equal(292, rail.ActualWidth);

                // L-5: wide content reflows inside the rail; it never becomes a hidden horizontal overflow.
                Assert.Equal(0, railScroll.ScrollableWidth);

                int measuredLabels = 0;
                foreach (FrameworkElement label in Descendants<TextBlock>(view).Cast<FrameworkElement>()
                             .Concat(Descendants<FamilyChip>(view)))
                {
                    if (!IsShown(label, view))
                        continue;

                    var parent = VisualTreeHelper.GetParent(label) as FrameworkElement;
                    if (parent is null)
                        continue;

                    measuredLabels++;

                    // L-2 / L-3: measured against the STYLE-derived desired size, never against a padding
                    // constant the view also owns — a formula sharing a constant with production code would
                    // validate itself and stay green while the screen was broken.
                    Assert.True(
                        label.DesiredSize.Width <= parent.RenderSize.Width + 0.5,
                        $"'{Describe(label)}' wanted {label.DesiredSize.Width:0.##} px inside a "
                        + $"{parent.RenderSize.Width:0.##} px parent at width {width}.");
                    AssertInside(parent, label, $"'{Describe(label)}' at width {width}");
                }

                // A width at which the sweep found nothing would pass silently, which is exactly how a layout
                // assertion stops being evidence.
                Assert.True(measuredLabels > 20, $"Only {measuredLabels} labels were measured at width {width}.");

                // The command bar's controls are siblings in one row and none of them may overlap another.
                FrameworkElement[] bar =
                [
                    .. Descendants<TextBox>(commandBar).Take(1),
                    .. Descendants<ListBox>(commandBar).Take(1),
                    .. Descendants<Button>(commandBar).Take(1),
                    .. Descendants<CheckBox>(commandBar).Take(1),
                ];
                AssertNoPairIntersects(commandBar, bar, width, "Command-bar controls");

                // Row internals: the risk chip and the action line are siblings in one preview row.
                DependencyObject row = RowContainer(rows, 0);
                FrameworkElement[] parts =
                [
                    .. Descendants<RiskChip>(row).Take(1),
                    .. Descendants<UndoLine>(row).Take(1),
                ];
                AssertNoPairIntersects(rows, parts, width, "Row parts");
            }
        });
    }

    // ===== fixture =====

    private static void OpenGate(FrameworkElement view, UninstallViewModel vm)
    {
        vm.UninstallSelectedCommand.Execute(null);
        Assert.True(vm.Gate.IsOpen, "the removal gate did not open");
        Layout(view, 1100);
    }

    private static void Layout(FrameworkElement view, int width)
    {
        var size = new Size(width, 720);
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();
    }

    /// <summary>
    /// Builds a populated Uninstall screen — one desktop program with a real uninstall string, a partial
    /// inventory read, <paramref name="leftovers"/> program-owned leftovers and one shared one the classifier
    /// refuses — scans it, renders it in the named theme, and hands it to the assertion. Every render is
    /// wrapped in the binding-warning guard, so a binding this screen silently loses fails here.
    /// </summary>
    private static void RunRendered(
        string themeName, int leftovers, Action<FrameworkElement, UninstallViewModel, I18n> assert)
        => RunRendered(themeName, leftovers, shared: true, assert);

    private static void RunRendered(
        string themeName, int leftovers, bool shared,
        Action<FrameworkElement, UninstallViewModel, I18n> assert)
    {
        RunOnStaThread(() =>
        {
            ResourceDictionary theme = MergeTheme(themeName);
            try
            {
                I18n i18n = TestI18n.Full("en");
                var probe = new FakeLeftoverProbe();

                // ProgramOwned has exactly three shapes, and only ONE of them can be produced N times: a
                // service whose image path lives under the install directory. A registry SUB-key of the
                // vendor leaf is SHARED, so seeding those would have produced one owned row however many
                // were asked for — which is how this fixture first lied about being populated.
                for (int index = 0; index < leftovers; index++)
                {
                    probe.Services.Add(new LeftoverService(
                        "SomeAppSvc" + index,
                        "owned service " + index,
                        InstallLocation + @"\svc" + index + ".exe"));
                }

                // One SHARED candidate: the vendor parent key. It renders as the gate's WON'T RUN row, LAST.
                if (shared)
                {
                    probe.RegistryKeys.Add(new LeftoverRegistryKey(
                        RegistryHive.LocalMachine, @"SOFTWARE\SomeVendor", RegistryView.Registry64,
                        "vendor parent"));
                }

                InstalledApp app = TestData.App(
                    uninstall: UninstallString, installLocation: InstallLocation);
                var vm = new UninstallViewModel(
                    i18n,
                    new PartialAppReader(app),
                    new NoAppxReader(),
                    TestData.Gate(),
                    probe,
                    new AlwaysDoneExecutor(),
                    new NoFolderOpener(),
                    new AvailableRestorePointCapability());

                vm.LoadAsync().GetAwaiter().GetResult();
                vm.SelectedRow = Assert.Single(vm.AllRows);
                vm.ScanLeftoversCommand.Execute(null);
                // Wait on the LAST thing the scan writes. Waiting on HasScanned alone is what made this fixture
                // intermittent: it was published before the rows the next line counts.
                Settle(() => !vm.IsScanningLeftovers && vm.HasScanned);

                Assert.Equal(leftovers, vm.LeftoverRows.Count);      // the fixture really is populated…
                Assert.Equal(shared ? 1 : 0, vm.LeftoverSkipped.Count); // …including the refused row when asked

                var view = new UninstallView { DataContext = vm };
                var host = new Border { Child = view };
                host.Measure(new Size(1100, 720));
                host.Arrange(new Rect(new Size(1100, 720)));
                host.UpdateLayout();
                Layout(view, 1100);

                assert(view, vm, i18n);
            }
            finally
            {
                Application.Current?.Resources.MergedDictionaries.Remove(theme);
            }
        });
    }

    private static void Settle(Func<bool> until)
    {
        System.Windows.Threading.Dispatcher dispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!until() && DateTime.UtcNow < deadline)
        {
            dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(5);
        }

        dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
        Assert.True(until(), "the uninstall fixture did not settle in time");
    }

    private static ResourceDictionary MergeTheme(string themeName)
    {
        Application application = Application.Current ??
            new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        var theme = new ResourceDictionary
        {
            Source = new Uri(
                $"pack://application:,,,/WindowsCareKit;component/Themes/{themeName}.xaml",
                UriKind.Absolute)
        };
        application.Resources.MergedDictionaries.Add(theme);
        application.Resources["BoolToVis"] = new BooleanToVisibilityConverter();
        application.Resources["NonEmptyToVis"] = new NonEmptyToVisibleConverter();
        return theme;
    }

    private static void RunOnStaThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                ViewRenderSmokeTests.AssertNoBindingWarnings(action);
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

    // ===== geometry + tree helpers =====

    private static T Named<T>(FrameworkElement view, string name) where T : class
        => view.FindName(name) as T
           ?? throw new InvalidOperationException($"'{name}' is not a {typeof(T).Name} on the Uninstall view.");

    /// <summary>Resolves a name inside the hosted <see cref="ConfirmGate"/>, whose names live in its own
    /// namescope rather than the Uninstall view's.</summary>
    private static T GateNamed<T>(FrameworkElement view, string name) where T : class
    {
        ConfirmGate gate = Descendants<ConfirmGate>(view).FirstOrDefault()
            ?? throw new InvalidOperationException("The Uninstall view hosts no ConfirmGate.");
        return gate.FindName(name) as T
               ?? throw new InvalidOperationException($"'{name}' is not a {typeof(T).Name} on the gate.");
    }

    private static DependencyObject GateRow(FrameworkElement view, int index)
        => RowContainer(GateNamed<ItemsControl>(view, "GateRowsHost"), index);

    private static DependencyObject RowContainer(ItemsControl host, int index)
        => host.ItemContainerGenerator.ContainerFromIndex(index)
           ?? throw new InvalidOperationException($"'{host.Name}' has no container at index {index}.");

    private static void AssertNoPairIntersects(
        Visual ancestor, FrameworkElement[] parts, int width, string label)
    {
        for (int left = 0; left < parts.Length; left++)
        {
            for (int right = left + 1; right < parts.Length; right++)
            {
                Assert.False(
                    Intersects(BoundsIn(ancestor, parts[left]), BoundsIn(ancestor, parts[right])),
                    $"{label} intersected at width {width}.");
            }
        }
    }

    private static Rect BoundsIn(Visual ancestor, FrameworkElement element)
        => element.TransformToAncestor(ancestor)
            .TransformBounds(new Rect(new Point(0, 0), element.RenderSize));

    private static bool Intersects(Rect left, Rect right)
        => Math.Min(left.Right, right.Right) - Math.Max(left.Left, right.Left) > 0.5 &&
           Math.Min(left.Bottom, right.Bottom) - Math.Max(left.Top, right.Top) > 0.5;

    private static void AssertInside(FrameworkElement ancestor, FrameworkElement element, string label)
    {
        Rect bounds = BoundsIn(ancestor, element);
        Assert.True(bounds.Left >= -0.5, $"{label} crossed its parent's left edge: {bounds}.");
        Assert.True(bounds.Top >= -0.5, $"{label} crossed its parent's top edge: {bounds}.");
        Assert.True(bounds.Right <= ancestor.RenderSize.Width + 0.5,
            $"{label} crossed its parent's right edge: {bounds} in {ancestor.RenderSize}.");
    }

    private static string Describe(FrameworkElement element)
        => element is TextBlock { Text.Length: > 0 } block
            ? block.Text[..Math.Min(40, block.Text.Length)]
            : element.Name.Length > 0 ? element.Name : element.GetType().Name;

    /// <summary>
    /// Whether this element is actually drawn. <see cref="UIElement.IsVisible"/> cannot be used here: it is
    /// false for EVERY element in a tree that was measured and arranged without a window, so a sweep filtered
    /// on it silently matches nothing and passes for the wrong reason. This walks the real chain instead.
    /// </summary>
    private static bool IsShown(DependencyObject element, DependencyObject root)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is UIElement { Visibility: not Visibility.Visible })
                return false;
            if (ReferenceEquals(current, root))
                return true;
        }

        return false;
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

    // ===== stubs: no registry, no filesystem, no uninstaller =====

    /// <summary>Reports a PARTIAL classic-app read, so the amber health note has something honest to say.</summary>
    private sealed class PartialAppReader(params InstalledApp[] apps) : IInstalledAppReader
    {
        public IReadOnlyList<InstalledApp> ReadAll() => apps;

        public InstalledAppReadResult ReadAllWithStatus()
            => new(apps, InstalledAppReadStatus.Partial, new[] { InstalledAppSource.MachineWide32 });
    }

    private sealed class NoAppxReader : IAppxReader
    {
        public IReadOnlyList<InstalledAppx> ReadCurrentUserPackages() => Array.Empty<InstalledAppx>();
    }

    private sealed class NoFolderOpener : IFolderOpener
    {
        public void OpenFolder(string path) { }
    }

    private sealed class AvailableRestorePointCapability : IRestorePointCapabilityProbe
    {
        public bool IsAvailable() => true;
    }

    private sealed class AlwaysDoneExecutor : IPlanExecutor
    {
        public PlanExecutionReport ExecuteWithReport(OperationPlan plan, string approvedPlanHash)
            => new(true, approvedPlanHash,
                plan.Actions
                    .Select(a => new PlanActionResult(a.Id, a.Kind, PlanActionStatus.Done, "rendered"))
                    .ToArray());
    }
}
