using System.Globalization;
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
using WindowsCareKit.Core.Modules.Clean;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Module.Clean.ViewModels;
using WindowsCareKit.Module.Clean.Views;
using WindowsCareKit.Tests.Execution;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// The M2 Clean screen's acceptance, asserted from a RENDERED visual tree rather than from the view model.
/// <para>
/// The view model already proves the arithmetic; these prove the composition seam — that the typed state
/// reaches the screen unchanged. The two most recent misses in this repo were both at a
/// composition-root→consumer seam the suite covered nowhere, and a counter that parsed rendered text would
/// pass every view-model test written against it.
/// </para>
/// </summary>
[Collection(WpfResourceCollection.Name)]
public sealed class CleanScreenTests
{
    /// <summary>The main column's width range. The shell's rail is a fixed 200 px (asserted by
    /// <c>RailShellTests</c>), so a window of 980–1920 — the app's MinWidth through a 1080p desktop — hands the
    /// module exactly 780–1720. Testing the module's own width range is what makes this range meaningful:
    /// measuring the module at 980 would be measuring a width it never receives.</summary>
    private const int MinContentWidth = 780;
    private const int MaxContentWidth = 1720;

    // ---- A-M2-4 / MP-4: the wire, not the counter ----

    /// <summary>
    /// The review rail's numbers are arithmetic over <see cref="PlanRow.Recovery"/>, and they reach the screen
    /// that way: switching the language re-renders every localized string on the screen and moves not one
    /// count. Written at the seam (view model → view) on purpose — a counter that parsed the localized
    /// <c>Undo</c> string would still satisfy a unit test of itself under a single language.
    /// </summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void Review_rail_counts_are_invariant_under_a_language_switch(string themeName)
    {
        RunRendered(themeName, (view, vm, i18n) =>
        {
            SelectEverything(vm);
            view.UpdateLayout();

            Assert.Equal("3", Named<TextBlock>(view, "SelectedActionCountText").Text);
            Assert.Equal("2", Named<TextBlock>(view, "RecoveryFullText").Text);   // two junk deletes
            Assert.Equal("0", Named<TextBlock>(view, "RecoveryPartialText").Text);
            Assert.Equal("1", Named<TextBlock>(view, "RecoveryNoneText").Text);   // emptying the bin
            Assert.Equal("1", Named<TextBlock>(view, "ProtectedCountText").Text); // the gate-refused row

            string consequenceBefore = Named<TextBlock>(view, "ConsequenceText").Text;
            string undoBefore = FirstRowUndoText(view);

            i18n.Load("tr");
            view.UpdateLayout();

            // Non-vacuity: the rendered PROSE really did switch language on this very screen…
            Assert.NotEqual(consequenceBefore, Named<TextBlock>(view, "ConsequenceText").Text);
            Assert.NotEqual(undoBefore, FirstRowUndoText(view));

            // …and not one number moved with it.
            Assert.Equal("3", Named<TextBlock>(view, "SelectedActionCountText").Text);
            Assert.Equal("2", Named<TextBlock>(view, "RecoveryFullText").Text);
            Assert.Equal("0", Named<TextBlock>(view, "RecoveryPartialText").Text);
            Assert.Equal("1", Named<TextBlock>(view, "RecoveryNoneText").Text);
            Assert.Equal("1", Named<TextBlock>(view, "ProtectedCountText").Text);
        });
    }

    // ---- A-M2-9 / A-M2-5: the protected row ----

    /// <summary>
    /// A-M2-9. The protected row carries no checkbox ELEMENT — not a disabled one, not a collapsed one. The
    /// mockup draws a disabled input there and the mockup is wrong: a disabled checkbox still presents as a
    /// choice. A vetoable row, by contrast, carries exactly one, so this is not passing because the sweep
    /// found nothing.
    /// </summary>
    [Fact]
    public void A_protected_row_renders_no_checkbox_element_while_a_vetoable_row_renders_exactly_one()
    {
        RunRendered("Strongbox", (view, vm, _) =>
        {
            DependencyObject vetoable = RowContainer(view, "JunkRowsHost", 0);
            DependencyObject blocked = RowContainer(view, "JunkSkippedHost", 0);

            Assert.Single(Descendants<CheckBox>(vetoable));
            Assert.Empty(Descendants<CheckBox>(blocked));

            // A-M2-5: it is still on screen, with its chip and its reason — never hidden.
            Assert.Contains(Descendants<RiskChip>(blocked), chip => chip.IsBlocked);
            string reason = vm.JunkSkipped[0].Detail ?? string.Empty;
            Assert.False(string.IsNullOrWhiteSpace(reason));
            Assert.Contains(
                Descendants<TextBlock>(blocked),
                block => IsShown(block, view) && block.Text.Contains(reason, StringComparison.Ordinal));
        });
    }

    // ---- A-M2-7: the toggle ADDS detail (the view-model proves it changes nothing else) ----

    /// <summary>
    /// The other half of A-M2-7. The view model proves the toggle is inert with respect to the plan, the
    /// selection and every count; this proves it is not inert with respect to the SCREEN — otherwise a control
    /// that did nothing at all would satisfy the inertness test perfectly.
    /// </summary>
    [Fact]
    public void The_technical_details_toggle_adds_the_reason_line_and_takes_nothing_else_away()
    {
        RunRendered("Strongbox", (view, vm, _) =>
        {
            DependencyObject row = RowContainer(view, "JunkRowsHost", 0);
            string reason = vm.JunkPreview[0].Detail ?? string.Empty;
            Assert.False(string.IsNullOrWhiteSpace(reason));

            Assert.True(vm.TechnicalDetails);
            Assert.Contains(Descendants<TextBlock>(row),
                block => IsShown(block, view) && block.Text == reason);

            // The literal action line — the path this row will delete — is NOT technical detail and never hides.
            string action = vm.JunkPreview[0].Text;
            Assert.Contains(Descendants<TextBlock>(row), block => IsShown(block, view) && block.Text == action);

            vm.TechnicalDetails = false;
            view.UpdateLayout();

            Assert.DoesNotContain(Descendants<TextBlock>(row),
                block => IsShown(block, view) && block.Text == reason);
            Assert.Contains(Descendants<TextBlock>(row), block => IsShown(block, view) && block.Text == action);
            Assert.Single(Descendants<CheckBox>(row));   // and the veto control is still there
        });
    }

    // ---- The binding section order ----

    /// <summary>Section order is a safety statement (SPEC §3 M2): Junk → Startup → Browser extensions →
    /// Recycle Bin last, behind a divider. Asserted from laid-out geometry rather than from the XAML's
    /// reading order, because a Grid row or a panel change could reorder them without touching the file's
    /// line order.</summary>
    [Fact]
    public void Sections_render_in_the_binding_safety_order_with_the_recycle_bin_last()
    {
        RunRendered("Strongbox", (view, _, _) =>
        {
            double junk = TopIn(view, Named<FrameworkElement>(view, "JunkSection"));
            double startup = TopIn(view, Named<FrameworkElement>(view, "StartupSection"));
            double extensions = TopIn(view, Named<FrameworkElement>(view, "ExtensionsSection"));
            double divider = TopIn(view, Named<FrameworkElement>(view, "SectionDivider"));
            double recycle = TopIn(view, Named<FrameworkElement>(view, "RecycleSection"));

            Assert.True(junk < startup, $"Startup must follow Junk ({junk} vs {startup}).");
            Assert.True(startup < extensions, $"Extensions must follow Startup ({startup} vs {extensions}).");
            Assert.True(extensions < divider, $"The divider must follow Extensions ({extensions} vs {divider}).");
            Assert.True(divider < recycle, $"The Recycle Bin must come last, after the divider ({divider} vs {recycle}).");
        });
    }

    // ---- A-M2-6: the extensions restraint ----

    [Fact]
    public void The_extensions_restraint_sentence_renders_verbatim_and_no_removal_control_exists_there()
    {
        RunRendered("Strongbox", (view, _, i18n) =>
        {
            var restraint = Named<TextBlock>(view, "ExtensionsRestraintText");
            Assert.Equal(i18n["clean.ext.note"], restraint.Text);

            // Verbatim means the whole sentence is drawn: it wraps, it never ellipsizes, and what it wants is
            // no wider than what it got.
            Assert.Equal(TextTrimming.None, restraint.TextTrimming);
            Assert.Equal(TextWrapping.Wrap, restraint.TextWrapping);

            // The only controls in this section are Refresh and Open folder — nothing removes or disables.
            string[] buttons = Descendants<Button>(Named<FrameworkElement>(view, "ExtensionsSection"))
                .Select(button => button.Content as string ?? string.Empty)
                .ToArray();

            Assert.Equal(
                new[] { i18n["clean.ext.openFolder"], i18n["common.refresh"] }.Order(StringComparer.Ordinal),
                buttons.Order(StringComparer.Ordinal));
        });
    }

    // ---- A-M2-8: no aggregate alarm number ----

    /// <summary>
    /// Every number on this screen sits next to the list it counts. Asserted as a sweep over every VISIBLE
    /// numeral in the rendered tree: each one must live inside a section or inside the review rail, so a
    /// headline "N issues found" added later has nowhere to sit that this test would accept.
    /// </summary>
    [Fact]
    public void Every_rendered_number_sits_inside_the_list_or_the_rail_it_counts()
    {
        RunRendered("Strongbox", (view, _, _) =>
        {
            FrameworkElement[] owners =
            [
                Named<FrameworkElement>(view, "JunkSection"),
                Named<FrameworkElement>(view, "StartupSection"),
                Named<FrameworkElement>(view, "ExtensionsSection"),
                Named<FrameworkElement>(view, "RecycleSection"),
                Named<FrameworkElement>(view, "ReviewRail"),
            ];

            TextBlock[] numerals = Descendants<TextBlock>(view)
                .Where(block => IsShown(block, view))
                .Where(block => int.TryParse(block.Text.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                .ToArray();

            TextBlock[] orphanNumerals = numerals
                .Where(block => !owners.Any(owner => IsDescendantOf(block, owner)))
                .ToArray();

            Assert.True(
                orphanNumerals.Length == 0,
                "A number rendered outside every list and the review rail is an aggregate alarm: "
                + string.Join(", ", orphanNumerals.Select(block => block.Text)));

            // Non-vacuous: the sweep does reach the rail's numerals.
            Assert.Contains(numerals, block => block.Name == "SelectedActionCountText");
        });
    }

    // ---- SPEC §5 L-1..L-5: measured layout, not eyeballed ----

    /// <summary>
    /// The responsive acceptance, measured. At every integer width the module can receive, the main column and
    /// the review rail never intersect, every element stays inside its parent, no label wants more width than
    /// it was measured into, and nothing overflows horizontally into a hidden region.
    /// </summary>
    [Theory]
    [InlineData("Strongbox")]
    [InlineData("Daylight")]
    public void Clean_layout_stays_inside_measured_bounds_across_the_whole_content_width_range(string themeName)
    {
        RunRendered(themeName, (view, vm, _) =>
        {
            SelectEverything(vm);
            var body = Named<FrameworkElement>(view, "CleanBody");
            var main = Named<ScrollViewer>(view, "MainColumn");
            var rail = Named<FrameworkElement>(view, "ReviewRail");
            var junk = Named<FrameworkElement>(view, "JunkSection");
            var recycle = Named<FrameworkElement>(view, "RecycleSection");
            var rows = Named<ItemsControl>(view, "JunkRowsHost");

            for (int width = MinContentWidth; width <= MaxContentWidth; width++)
            {
                Layout(view, width);

                Rect mainBounds = BoundsIn(body, main);
                Rect railBounds = BoundsIn(body, rail);
                Assert.False(Intersects(mainBounds, railBounds),
                    $"The main column and the review rail intersected at width {width}: {mainBounds} / {railBounds}.");
                AssertInside(body, main, $"main column at width {width}");
                AssertInside(body, rail, $"review rail at width {width}");
                Assert.Equal(292, rail.ActualWidth);

                // L-5: wide content reflows inside the column; it never becomes a hidden horizontal overflow.
                Assert.Equal(0, main.ScrollableWidth);

                AssertInside(main, junk, $"junk section at width {width}");
                AssertInside(main, recycle, $"recycle section at width {width}");

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

                // Row internals: the veto control, the risk chip, the action line and the recovery statement
                // are four siblings in one row and none of them may overlap another.
                DependencyObject row = RowContainer(view, "JunkRowsHost", 0);
                FrameworkElement[] parts =
                [
                    .. Descendants<ContentControl>(row).Take(1),
                    .. Descendants<RiskChip>(row).Take(1),
                    .. Descendants<UndoLine>(row).Take(1),
                ];
                for (int left = 0; left < parts.Length; left++)
                {
                    for (int right = left + 1; right < parts.Length; right++)
                    {
                        Assert.False(
                            Intersects(BoundsIn(rows, parts[left]), BoundsIn(rows, parts[right])),
                            $"Row parts intersected at width {width}.");
                    }
                }
            }
        });
    }

    // ===== fixture =====

    private static void SelectEverything(CleanViewModel vm)
    {
        foreach (PlanRow row in vm.JunkPreview)
            row.IsIncluded = true;
        vm.RecycleRow.IsIncluded = true;
    }

    private static string FirstRowUndoText(FrameworkElement view)
        => Descendants<UndoLine>(RowContainer(view, "JunkRowsHost", 0)).Single().Text;

    private static void Layout(FrameworkElement view, int width)
    {
        var size = new Size(width, 720);
        view.Measure(size);
        view.Arrange(new Rect(size));
        view.UpdateLayout();
    }

    /// <summary>
    /// Builds a populated Clean screen — two junk candidates the gate allows, one it refuses, a startup entry,
    /// an extension and a readable bin — renders it in the named theme, and hands it to the assertion. Every
    /// render is wrapped in the binding-warning guard, so a binding this screen silently loses fails here
    /// rather than in whichever later test happens to look.
    /// </summary>
    private static void RunRendered(string themeName, Action<FrameworkElement, CleanViewModel, I18n> assert)
    {
        RunOnStaThread(() =>
        {
            ResourceDictionary theme = MergeTheme(themeName);
            try
            {
                I18n i18n = TestI18n.Full("en");
                using var fx = new ExecutorFixture();
                var vm = new CleanViewModel(
                    i18n,
                    new StubJunkProbe(
                        new JunkCandidate(@"C:\Users\alice\AppData\Local\Temp", 2048, "User temp folder"),
                        new JunkCandidate(@"C:\Users\alice\AppData\Local\CrashDumps", 4096, "Crash dumps"),
                        new JunkCandidate(@"C:\Windows\System32\wck-not-junk", 512, "Protected")),
                    new StubStartupProbe(new StartupEntry(
                        "Updater", @"C:\Program Files\App\updater.exe", StartupSource.HkcuRun, null)),
                    new StubExtensionInventory(new BrowserExtension(
                        "Edge", "Default", "abc123", "Test Extension", @"C:\ext\abc123")),
                    new StubRecycleBinService(new RecycleBinStats(3, 2048)),
                    new StubFolderOpener(),
                    fx.Gate,
                    new StubPlanExecutor(fx.Executor));

                vm.ScanJunkCommand.Execute(null);
                Settle(() => vm.JunkScanned && !vm.IsBusy);
                vm.LoadStartupCommand.Execute(null);
                Settle(() => !vm.IsBusy && vm.StartupEntries.Count > 0);
                vm.LoadExtensionsCommand.Execute(null);
                Settle(() => !vm.IsBusy && vm.Extensions.Count > 0);
                vm.RefreshRecycleCommand.Execute(null);
                Settle(() => !vm.IsBusy && vm.RecycleStats.Length > 0);

                Assert.Equal(2, vm.JunkPreview.Count);   // the fixture really is populated…
                Assert.Single(vm.JunkSkipped);           // …including the row the gate refuses

                var view = new CleanView { DataContext = vm };
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
        Assert.True(until(), "the Clean fixture did not settle in time");
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
           ?? throw new InvalidOperationException($"'{name}' is not a {typeof(T).Name} on the Clean view.");

    private static DependencyObject RowContainer(FrameworkElement view, string hostName, int index)
    {
        var host = Named<ItemsControl>(view, hostName);
        return host.ItemContainerGenerator.ContainerFromIndex(index)
               ?? throw new InvalidOperationException($"'{hostName}' has no container at index {index}.");
    }

    private static Rect BoundsIn(Visual ancestor, FrameworkElement element)
        => element.TransformToAncestor(ancestor)
            .TransformBounds(new Rect(new Point(0, 0), element.RenderSize));

    private static double TopIn(Visual ancestor, FrameworkElement element) => BoundsIn(ancestor, element).Top;

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

    private static bool IsDescendantOf(DependencyObject child, DependencyObject ancestor)
    {
        for (DependencyObject? current = VisualTreeHelper.GetParent(child);
             current is not null;
             current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
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

    // ===== stubs: no filesystem, no registry, no recycle bin, no Explorer =====

    private sealed class StubJunkProbe(params JunkCandidate[] candidates) : IJunkProbe
    {
        public IReadOnlyList<JunkCandidate> FindJunk() => candidates;
    }

    private sealed class StubStartupProbe(params StartupEntry[] entries) : IStartupProbe
    {
        public StartupInventory ReadAll()
            => new(entries, SourceHealth.Complete, Array.Empty<InventorySourceFault>());
    }

    private sealed class StubExtensionInventory(params BrowserExtension[] extensions) : IBrowserExtensionInventory
    {
        public BrowserExtensionListing ReadAll()
            => new(extensions, SourceHealth.Complete, Array.Empty<InventorySourceFault>());
    }

    private sealed class StubRecycleBinService(RecycleBinStats stats) : IRecycleBinService
    {
        public RecycleBinInventory Query() => RecycleBinInventory.Complete(stats);
    }

    private sealed class StubFolderOpener : IFolderOpener
    {
        public void OpenFolder(string path) { }
    }

    private sealed class StubPlanExecutor(GatedExecutor executor) : IPlanExecutor
    {
        public PlanExecutionReport ExecuteWithReport(OperationPlan plan, string approvedPlanHash)
        {
            ExecutionReport report = executor.ExecuteWithReport(plan, approvedPlanHash);
            return new PlanExecutionReport(
                report.Authorized,
                report.PlanHash,
                report.Results
                    .Select(r => new PlanActionResult(r.ActionId, r.Kind, Map(r.Status), r.Detail))
                    .ToArray());
        }

        private static PlanActionStatus Map(ActionStatus status) => status switch
        {
            ActionStatus.Done => PlanActionStatus.Done,
            ActionStatus.Skipped => PlanActionStatus.Skipped,
            ActionStatus.Blocked => PlanActionStatus.Blocked,
            ActionStatus.Failed => PlanActionStatus.Failed,
            ActionStatus.NotRun => PlanActionStatus.NotRun,
            _ => PlanActionStatus.Failed,
        };
    }
}
