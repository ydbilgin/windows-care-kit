using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests.Migration.Detection;

public sealed class M1bProgramSourceTests
{
    [Fact]
    public void Msi_source_projects_product_code_and_other_user_scope()
    {
        var catalog = new FakeMsiCatalog([
            new MsiProduct
            {
                ProductCode = "{11111111-2222-3333-4444-555555555555}",
                DisplayName = "MSI App 1.0",
                Publisher = "Vendor",
                Version = "1.0",
                InstallLocation = @"C:\Program Files\MSI App",
                UserSid = "S-1-other",
                IsMachineContext = false,
            },
        ]);

        var source = new MsiProductSource(catalog, FakeCanonicalizer.Instance, currentUserSid: "S-1-current");
        ProgramEnumeration result = source.Enumerate();

        Assert.Equal(ProgramSourceStatus.Ok, result.Report.Status);
        DiscoveredProgram program = Assert.Single(result.Programs);
        Assert.Equal("{11111111-2222-3333-4444-555555555555}", program.ProductCode);
        Assert.Equal("msi app", program.NormalizedName);
        Assert.Equal("msi app", program.InstallPathLeaf);
        Assert.Equal(ProgramScope.OtherUserNotEnumerable, program.Scope);
    }

    [Fact]
    public void Msi_source_empty_catalog_is_SourceFailed()
    {
        var source = new MsiProductSource(new FakeMsiCatalog([]), FakeCanonicalizer.Instance);
        ProgramEnumeration result = source.Enumerate();
        Assert.Equal(ProgramSourceStatus.SourceFailed, result.Report.Status);
        Assert.Empty(result.Programs);
    }

    [Fact]
    public void Appx_source_uses_package_family_name_and_preserves_system_flag()
    {
        var package = new InstalledAppx
        {
            PackageFullName = "Contoso.App_1.0.0.0_x64__abc123",
            PackageFamilyName = "Contoso.App_abc123",
            DisplayName = "Contoso App",
            PublisherDisplayName = "Contoso",
            Version = "1.0.0.0",
            InstallLocation = @"C:\Program Files\WindowsApps\Contoso.App_1.0.0.0_x64__abc123",
            IsFrameworkOrSystem = true,
        };

        var source = new AppxProgramSource(new FakeAppxReader([package]), FakeCanonicalizer.Instance);
        ProgramEnumeration result = source.Enumerate();

        DiscoveredProgram program = Assert.Single(result.Programs);
        Assert.Equal("contoso.app_abc123", program.PackageFamilyName);
        Assert.Equal("contoso.app_abc123", program.Id);
        Assert.True(program.IsSystemComponent);
        Assert.Equal(ProgramScope.CurrentUser, program.Scope);
    }

    [Fact]
    public void Appx_source_empty_reader_is_SourceUnavailable()
    {
        var source = new AppxProgramSource(new FakeAppxReader([]), FakeCanonicalizer.Instance);
        ProgramEnumeration result = source.Enumerate();
        Assert.Equal(ProgramSourceStatus.SourceUnavailable, result.Report.Status);
    }

    [Fact]
    public void App_paths_source_reads_all_registry_views_as_launchable_programs()
    {
        var registry = new FakeRegistryProbe();
        registry.AddSubKey(RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths", "foo.exe");
        registry.SetKey(RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\foo.exe",
            new Dictionary<string, object?> { [""] = @"C:\Tools\Foo\foo.exe" });

        var source = new AppPathsSource(registry, FakeCanonicalizer.Instance);
        ProgramEnumeration result = source.Enumerate();

        Assert.Equal(ProgramSourceStatus.Ok, result.Report.Status);
        DiscoveredProgram program = Assert.Single(result.Programs);
        Assert.Equal("foo", program.DisplayName);
        Assert.Equal("foo", program.InstallPathLeaf);
        Assert.Equal(ProgramScope.Machine, program.Scope);
    }

    [Fact]
    public void Start_menu_source_resolves_only_exe_shortcuts()
    {
        var reader = new FakeStartMenuShortcutReader([
            new StartMenuShortcut("Foo", @"C:\ProgramData\Microsoft\Windows\Start Menu\Programs\Foo.lnk", @"C:\Tools\Foo\foo.exe"),
            new StartMenuShortcut("Docs", @"C:\Users\a\AppData\Roaming\Microsoft\Windows\Start Menu\Programs\Docs.lnk", @"C:\Docs\readme.txt"),
        ]);

        var source = new StartMenuSource(reader, FakeCanonicalizer.Instance);
        ProgramEnumeration result = source.Enumerate();

        DiscoveredProgram program = Assert.Single(result.Programs);
        Assert.Equal("Foo", program.DisplayName);
        Assert.Equal(ProgramScope.Machine, program.Scope);
        Assert.Equal(ProgramSourceKind.StartMenu, program.Sources.Single());
    }

    [Fact]
    public void Detector_counts_launchable_programs_without_install_records()
    {
        var registryApp = Program("Foo", ProgramSourceKind.RegistryUninstall, installPathLeaf: "foo");
        var startOnly = Program("Portable", ProgramSourceKind.StartMenu, installPathLeaf: "portable");
        var appPathOnly = Program("Broken", ProgramSourceKind.AppPaths, installPathLeaf: "broken");
        var detector = new ProgramDetector([
            new FakeProgramSource(ProgramSourceKind.RegistryUninstall, [registryApp]),
            new FakeProgramSource(ProgramSourceKind.StartMenu, [startOnly]),
            new FakeProgramSource(ProgramSourceKind.AppPaths, [appPathOnly]),
        ]);

        DetectionResult result = detector.Detect();

        Assert.Equal(2, result.LaunchableWithoutInstallRecordCount);
    }

    [Fact]
    public void Dedup_field_tie_break_is_source_ordinal_not_input_order()
    {
        DiscoveredProgram start = Program("Zed", ProgramSourceKind.StartMenu, installPathLeaf: "same", version: "9");
        DiscoveredProgram registry = Program("Alpha", ProgramSourceKind.RegistryUninstall, installPathLeaf: "same", version: "1");

        DiscoveredProgram merged = Assert.Single(ProgramDedupLayer.Merge([start, registry]));

        Assert.Equal("Alpha", merged.DisplayName);
        Assert.Equal("1", merged.Version);
        Assert.Equal([ProgramSourceKind.RegistryUninstall, ProgramSourceKind.StartMenu], merged.Sources);
    }

    [Fact]
    public void Dedup_same_source_value_conflicts_have_a_stable_tie_break()
    {
        DiscoveredProgram zulu = Program("Same", ProgramSourceKind.AppPaths, installPathLeaf: "same", version: "9")
            with { Publisher = "Zulu" };
        DiscoveredProgram alpha = Program("Same", ProgramSourceKind.AppPaths, installPathLeaf: "same", version: "1")
            with { Publisher = "Alpha" };

        DiscoveredProgram forward = Assert.Single(ProgramDedupLayer.Merge([zulu, alpha]));
        DiscoveredProgram reverse = Assert.Single(ProgramDedupLayer.Merge([alpha, zulu]));

        Assert.Equal(forward.DisplayName, reverse.DisplayName);
        Assert.Equal(forward.Publisher, reverse.Publisher);
        Assert.Equal(forward.Version, reverse.Version);
        Assert.Equal(forward.InstallLocation, reverse.InstallLocation);
        Assert.Equal(forward.InstallPathLeaf, reverse.InstallPathLeaf);
        Assert.Equal(forward.Sources, reverse.Sources);
        Assert.Equal("Alpha", forward.Publisher);
        Assert.Equal("1", forward.Version);
    }

    [Fact]
    public void Recall_oracle_dedups_app_paths_and_start_menu_for_the_same_unregistered_binary()
    {
        var appPath = Program("Portable", ProgramSourceKind.AppPaths, installPathLeaf: "portable");
        var startMenu = Program("Portable Shortcut", ProgramSourceKind.StartMenu, installPathLeaf: "portable");
        var detector = new ProgramDetector([
            new FakeProgramSource(ProgramSourceKind.AppPaths, [appPath]),
            new FakeProgramSource(ProgramSourceKind.StartMenu, [startMenu]),
        ]);

        DetectionResult result = detector.Detect();

        Assert.Single(result.Programs);
        Assert.Equal(1, result.LaunchableWithoutInstallRecordCount);
        Assert.Equal([ProgramSourceKind.AppPaths, ProgramSourceKind.StartMenu], result.Programs[0].Sources);
    }

    [Fact]
    public void Win32InstalledAppReader_reads_through_registry_probe()
    {
        var registry = new FakeRegistryProbe();
        registry.AddSubKey(RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", "Foo");
        registry.SetKey(RegistryHive.LocalMachine, RegistryView.Registry64,
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\Foo",
            new Dictionary<string, object?>
            {
                ["DisplayName"] = "Foo",
                ["Publisher"] = "Vendor",
                ["EstimatedSize"] = 2048,
                ["SystemComponent"] = 1,
            });

        var reader = new Win32InstalledAppReader(registry);
        InstalledApp app = Assert.Single(reader.ReadAll());

        Assert.Equal("Foo", app.DisplayName);
        Assert.Equal("Vendor", app.Publisher);
        Assert.Equal(2048, app.EstimatedSizeKb);
        Assert.True(app.IsSystemComponent);
    }

    private static DiscoveredProgram Program(
        string displayName,
        ProgramSourceKind source,
        string? installPathLeaf = null,
        string? version = null)
    {
        string normalized = ProgramJoinKeys.NormalizeName(displayName);
        return new DiscoveredProgram
        {
            Id = installPathLeaf ?? $"{normalized}|",
            DisplayName = displayName,
            Publisher = null,
            Version = version,
            InstallLocation = installPathLeaf is null ? null : @"C:\Apps\" + installPathLeaf,
            InstallPathLeaf = installPathLeaf,
            ProductCode = null,
            NormalizedName = normalized,
            Scope = ProgramScope.CurrentUser,
            Sources = [source],
            IsSystemComponent = false,
            ReinstallId = null,
            PackageFamilyName = null,
        };
    }

    private sealed class FakeMsiCatalog(IReadOnlyList<MsiProduct> products) : IMsiCatalog
    {
        public IReadOnlyList<MsiProduct> EnumerateProducts() => products;
    }

    private sealed class FakeAppxReader(IReadOnlyList<InstalledAppx> packages) : IAppxReader
    {
        public IReadOnlyList<InstalledAppx> ReadCurrentUserPackages() => packages;
    }

    private sealed class FakeStartMenuShortcutReader(IReadOnlyList<StartMenuShortcut> shortcuts) : IStartMenuShortcutReader
    {
        public IReadOnlyList<StartMenuShortcut> ReadShortcuts() => shortcuts;
    }

    private sealed class FakeProgramSource(ProgramSourceKind kind, IReadOnlyList<DiscoveredProgram> programs) : IProgramSource
    {
        public ProgramSourceKind Kind { get; } = kind;

        public ProgramEnumeration Enumerate()
            => new(programs, new ProgramSourceReport(Kind, ProgramSourceStatus.Ok, programs.Count));
    }

    private sealed class FakeRegistryProbe : IRegistryProbe
    {
        private readonly Dictionary<(RegistryHive Hive, RegistryView View, string Path), List<string>> _subKeys = [];
        private readonly Dictionary<(RegistryHive Hive, RegistryView View, string Path), RegistryKeySnapshot> _keys = [];

        public void AddSubKey(RegistryHive hive, RegistryView view, string path, string subKey)
        {
            var key = (hive, view, path);
            if (!_subKeys.TryGetValue(key, out List<string>? list))
            {
                list = [];
                _subKeys[key] = list;
            }
            list.Add(subKey);
        }

        public void SetKey(RegistryHive hive, RegistryView view, string path, IReadOnlyDictionary<string, object?> values)
            => _keys[(hive, view, path)] = new RegistryKeySnapshot(values);

        public IReadOnlyList<string> GetSubKeyNames(RegistryHive hive, RegistryView view, string subKey)
            => _subKeys.TryGetValue((hive, view, subKey), out List<string>? list) ? list : Array.Empty<string>();

        public RegistryKeySnapshot? ReadKey(RegistryHive hive, RegistryView view, string subKey)
            => _keys.TryGetValue((hive, view, subKey), out RegistryKeySnapshot? key) ? key : null;
    }
}
