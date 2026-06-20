using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// PR-2 — the Sil unified DataGrid / ICollectionView behavior. Covers the load-bearing invariant that the
/// search filter is a VIEW over the backing collection and NEVER mutates it (so a staged plan/selection
/// survives typing), plus the registry Size/InstallDate read-out formatting and the status-badge mapping
/// (neutral/amber, never green-for-present). No OS access — readers are faked (UI decision §2/§3, §7).
/// </summary>
public class UninstallGridViewTests
{
    private static UninstallViewModel BuildVm(
        IReadOnlyList<InstalledApp>? apps = null, IReadOnlyList<InstalledAppx>? appx = null)
    {
        var i18n = new I18n();
        i18n.Load("tr"); // exercise the localized badge mapping
        var appReader = new FakeReader(apps ?? Array.Empty<InstalledApp>());
        var appxReader = new FakeAppxReader(appx ?? Array.Empty<InstalledAppx>());
        return new UninstallViewModel(i18n, appReader, appxReader, TestData.Gate(),
            new FakeLeftoverProbe(), new FakeExecutor(), new FakeAppxRemover(), new FakeFolderOpener());
    }

    private static int ViewCount(System.ComponentModel.ICollectionView v)
    {
        int n = 0;
        foreach (var _ in v)
            n++;
        return n;
    }

    // ---- ICollectionView filter NON-MUTATION (the core PR-2 requirement) ----

    [Fact]
    public async Task Search_filters_the_view_without_mutating_the_backing_collection()
    {
        var apps = new[]
        {
            TestData.App(displayName: "Alpha Tool", publisher: "Acme", regKeyName: "a"),
            TestData.App(displayName: "Beta Suite", publisher: "Globex", regKeyName: "b"),
            TestData.App(displayName: "Gamma App", publisher: "Acme", regKeyName: "g"),
        };
        var vm = BuildVm(apps);
        await vm.LoadAsync();

        // Snapshot the backing collection BEFORE filtering.
        int backingBefore = vm.AllRows.Count;
        var contentsBefore = vm.AllRows.ToList();
        Assert.Equal(3, backingBefore);
        Assert.Equal(3, ViewCount(vm.AppsView));

        // Type a search that matches only one row.
        vm.Search = "beta";

        // The VIEW narrows…
        Assert.Equal(1, ViewCount(vm.AppsView));
        // …but the BACKING collection is byte-for-byte unchanged (count AND contents AND order).
        Assert.Equal(backingBefore, vm.AllRows.Count);
        Assert.Equal(contentsBefore, vm.AllRows.ToList());

        // Clearing the search restores the full view, still off the same untouched backing list.
        vm.Search = "";
        Assert.Equal(3, ViewCount(vm.AppsView));
        Assert.Equal(contentsBefore, vm.AllRows.ToList());
    }

    [Fact]
    public async Task Filtering_does_not_drop_a_staged_selection_from_the_backing_list()
    {
        // A staged plan/selection must survive typing — proven by the selected row still living in the
        // backing collection even when the filter would hide it (UI decision §2).
        var apps = new[]
        {
            TestData.App(displayName: "Alpha Tool", regKeyName: "a"),
            TestData.App(displayName: "Beta Suite", regKeyName: "b"),
        };
        var vm = BuildVm(apps);
        await vm.LoadAsync();

        AppRow selected = vm.AllRows.First(r => r.DisplayName == "Alpha Tool");
        vm.SelectedRow = selected;

        vm.Search = "beta"; // would hide Alpha from the view

        Assert.Contains(selected, vm.AllRows);       // backing list still holds the selected row
        Assert.Same(selected, vm.SelectedRow);        // selection itself is untouched
    }

    [Fact]
    public async Task Scope_filter_narrows_the_view_only()
    {
        var apps = new[] { TestData.App(displayName: "Desktop One", regKeyName: "d") };
        var store = new[] { new InstalledAppx { PackageFullName = "X_1.0_x64__a", DisplayName = "Store One" } };
        var vm = BuildVm(apps, store);
        await vm.LoadAsync();

        Assert.Equal(2, vm.AllRows.Count);
        Assert.Equal(2, ViewCount(vm.AppsView));

        vm.ScopeIndex = 2; // Store only
        Assert.Equal(1, ViewCount(vm.AppsView));
        Assert.Equal(2, vm.AllRows.Count); // backing unchanged

        vm.ScopeIndex = 1; // Desktop only
        Assert.Equal(1, ViewCount(vm.AppsView));
        Assert.Equal(2, vm.AllRows.Count);
    }

    // ---- Registry Size / InstallDate read-out + formatting ----

    [Theory]
    [InlineData(null, "—")]
    [InlineData(0, "0 KB")]
    [InlineData(512, "512 KB")]
    [InlineData(1024, "1 MB")]
    [InlineData(1536, "1{0}5 MB")]   // {0} = the host culture's decimal separator (e.g. "." or ",")
    [InlineData(1048576, "1 GB")]
    [InlineData(1572864, "1{0}5 GB")]
    public void FormatSize_renders_kb_mb_gb(int? kb, string expectedTemplate)
    {
        // Size formatting follows the current UI culture (a Turkish UI shows "1,5 MB") — assert against the
        // host's decimal separator so the test is correct on any culture.
        string sep = System.Globalization.CultureInfo.CurrentCulture.NumberFormat.NumberDecimalSeparator;
        string expected = string.Format(expectedTemplate, sep);
        Assert.Equal(expected, InstalledApp.FormatSize(kb));
    }

    [Theory]
    [InlineData("20240115", "2024-01-15")]
    [InlineData("20011231", "2001-12-31")]
    public void ParseInstallDate_parses_yyyyMMdd(string raw, string expectedIso)
    {
        DateOnly? d = InstalledApp.ParseInstallDate(raw);
        Assert.NotNull(d);
        Assert.Equal(expectedIso, d!.Value.ToString("yyyy-MM-dd"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("not-a-date")]
    [InlineData("2024-01-15")] // wrong shape (dashes) → null, not a guess
    public void ParseInstallDate_returns_null_for_bad_input(string? raw)
        => Assert.Null(InstalledApp.ParseInstallDate(raw));

    [Fact]
    public async Task Grid_rows_surface_registry_size_and_date_or_emdash()
    {
        var withMeta = TestData.App(displayName: "Has Meta", regKeyName: "m") with
        {
            EstimatedSizeKb = 2048,
            InstallDate = new DateOnly(2024, 3, 9),
        };
        var withoutMeta = TestData.App(displayName: "No Meta", regKeyName: "n");
        var vm = BuildVm(new[] { withMeta, withoutMeta });
        await vm.LoadAsync();

        AppRow meta = vm.AllRows.First(r => r.DisplayName == "Has Meta");
        AppRow none = vm.AllRows.First(r => r.DisplayName == "No Meta");

        Assert.Equal("2 MB", meta.SizeDisplay);
        Assert.Equal("2024-03-09", meta.InstallDateDisplay);
        Assert.Equal("—", none.SizeDisplay);
        Assert.Equal("—", none.InstallDateDisplay);
    }

    [Fact]
    public async Task Store_rows_report_emdash_for_size_and_date()
    {
        var store = new[] { new InstalledAppx { PackageFullName = "X_1.0_x64__a", DisplayName = "Store App" } };
        var vm = BuildVm(appx: store);
        await vm.LoadAsync();

        AppRow row = vm.AllRows.Single();
        Assert.True(row.IsStore);
        Assert.Equal("—", row.SizeDisplay);
        Assert.Equal("—", row.InstallDateDisplay);
    }

    // ---- Status badge mapping (no green-for-present; neutral/amber only) ----

    [Fact]
    public void Healthy_present_uninstaller_gets_no_badge()
    {
        // A per-user app with a working uninstaller → NO status badge (never a green "ok" — §7).
        var app = TestData.App(source: InstalledAppSource.CurrentUser,
            uninstall: "\"C:\\Program Files\\App\\uninst.exe\"");
        AppRow row = AppRow.FromApp(app);
        Assert.False(row.HasStatusBadge);
        Assert.Equal(string.Empty, row.StatusBadge);
    }

    [Fact]
    public void Broken_uninstaller_is_amber_attention_not_green()
    {
        var app = TestData.App(uninstall: null, quietUninstall: null); // no uninstaller at all
        AppRow row = AppRow.FromApp(app);
        Assert.True(row.HasStatusBadge);
        Assert.Equal(AppRow.BrokenBadge, row.StatusBadge);
        Assert.Equal(StatusTone.Attention, row.StatusTone); // amber, NOT green and NOT danger-red
    }

    [Fact]
    public void Machine_wide_app_with_uninstaller_is_admin_neutral()
    {
        var app = TestData.App(source: InstalledAppSource.MachineWide64,
            uninstall: "\"C:\\Program Files\\App\\uninst.exe\"");
        AppRow row = AppRow.FromApp(app);
        Assert.Equal(AppRow.AdminBadge, row.StatusBadge);
        Assert.Equal(StatusTone.Neutral, row.StatusTone);
    }

    [Fact]
    public void Store_app_is_store_neutral()
    {
        AppRow row = AppRow.FromAppx(new InstalledAppx { PackageFullName = "X_1.0_x64__a", DisplayName = "S" });
        Assert.Equal(AppRow.StoreBadge, row.StatusBadge);
        Assert.Equal(StatusTone.Neutral, row.StatusTone);
    }

    [Fact]
    public async Task Badge_text_is_localized_to_turkish_brackets()
    {
        var broken = TestData.App(displayName: "Broken", regKeyName: "x", uninstall: null);
        var vm = BuildVm(new[] { broken });
        await vm.LoadAsync();
        AppRow row = vm.AllRows.Single();
        Assert.Equal("[Kaldırıcı Bozuk]", row.BadgeText);
    }

    // ---- fakes ----

    private sealed class FakeReader(IReadOnlyList<InstalledApp> apps) : IInstalledAppReader
    {
        public IReadOnlyList<InstalledApp> ReadAll() => apps;
    }

    private sealed class FakeAppxReader(IReadOnlyList<InstalledAppx> packages) : IAppxReader
    {
        public IReadOnlyList<InstalledAppx> ReadCurrentUserPackages() => packages;
    }

    private sealed class FakeExecutor : IExecutor
    {
        public ExecutionOutcome Execute(OperationPlan plan, string approvedPlanHash) => new(true, "faked");
    }

    private sealed class FakeAppxRemover : IAppxRemover
    {
        public Task<AppxRemovalResult> RemoveCurrentUserAsync(InstalledAppx package, CancellationToken ct = default)
            => Task.FromResult(new AppxRemovalResult(true, "removed"));
    }

    private sealed class FakeFolderOpener : IFolderOpener
    {
        public void OpenFolder(string path) { }
    }
}
