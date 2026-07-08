using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.Execution;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;
using WindowsCareKit.App.Theming;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Modules.Clean;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Modules.Migration.Detection;
using WindowsCareKit.Core.Modules.Migration.Execution;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Execution.Adapters;
using WindowsCareKit.Win32;

namespace WindowsCareKit.App;

public partial class App : Application
{
    public IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var services = new ServiceCollection();
        ConfigureServices(services, e.Args);
        Services = services.BuildServiceProvider();

        var i18n = Services.GetRequiredService<I18n>();
        i18n.Load(ResolveCulture(e.Args));

        var themeService = Services.GetRequiredService<IThemeService>();
        ThemeDictionary.ApplyStartupTheme(Resources, themeService.AppliedTheme);

        var main = Services.GetRequiredService<MainViewModel>();
        if (main.SelectNavByKey(ExtractOption(e.Args, "--screen")))
            main.ShowFirstRun = false; // deep-linked to a module → skip the first-run modal (demo/screenshot mode)
        var window = new MainWindow { DataContext = main };
        window.Show();

        main.OnShellStartup(); // kick off the read-only inventory load
    }

    /// <summary>
    /// Picks the UI language. An explicit <c>--lang en|tr</c> argument or the
    /// <c>WCK_LANG</c> environment variable wins; otherwise English, regardless of
    /// OS UI culture. Keeping the language overridable makes it deterministic for screenshots, demos,
    /// and global users on a non-English Windows.
    /// </summary>
    internal static string ResolveCulture(string[] args)
        => ResolveCulture(
            args,
            Environment.GetEnvironmentVariable("WCK_LANG"),
            CultureInfo.CurrentUICulture.TwoLetterISOLanguageName);

    /// <summary>Pure, testable core of <see cref="ResolveCulture(string[])"/>.</summary>
    internal static string ResolveCulture(string[] args, string? envLang, string osTwoLetter)
    {
        // osTwoLetter is retained for signature/back-compat only; OS locale no longer affects the
        // default — with no --lang/WCK_LANG override the UI language is always English.
        string? pick = Normalize(ExtractLangArg(args)) ?? Normalize(envLang);
        if (pick is not null) return pick;
        return "en";
    }

    private static string? ExtractLangArg(string[] args) => ExtractOption(args, "--lang");

    /// <summary>
    /// Reads a <c>--name value</c> or <c>--name=value</c> option from the argv (case-insensitive),
    /// returning the first occurrence's value or <c>null</c>. Used for <c>--lang</c> and <c>--screen</c>.
    /// </summary>
    internal static string? ExtractOption(string[] args, string name)
    {
        string eq = name + "=";
        for (int i = 0; i < args.Length; i++)
        {
            string a = args[i];
            if (a.StartsWith(eq, StringComparison.OrdinalIgnoreCase))
                return a[eq.Length..];
            if (a.Equals(name, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Length)
                return args[i + 1];
        }
        return null;
    }

    private static string? Normalize(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        code = code.Trim().ToLowerInvariant();
        return code is "en" or "tr" ? code : null;
    }

    private static void ConfigureServices(IServiceCollection s, string[] args)
    {
        AddBaseServices(s, args);

        IReadOnlyList<IWckModule> modules = new DirectoryModuleCatalog().LoadModules();
        foreach (IWckModule module in modules)
            module.RegisterServices(s);

        s.AddSingleton(modules);
    }

    /// <summary>
    /// The default module set, discovered from <c>&lt;appdir&gt;\Modules\</c> per the ratified M4 trust
    /// policy (see <see cref="DirectoryModuleCatalog"/>). Single production/test seam.
    /// </summary>
    internal static IReadOnlyList<IWckModule> CreateDefaultModules() => new DirectoryModuleCatalog().LoadModules();

    internal static void AddBaseServices(IServiceCollection s, string[] args)
    {
        // The graceful `?? empty` is REQUIRED: ModuleCompositionTests builds base-only providers with no
        // module list registered and still resolves I18n (shell-only strings, the modular truth). The
        // real app registers the module list at ConfigureServices below, so the app singleton gets the
        // full merged set (lazy resolution — I18n itself isn't Load()ed until OnStartup).
        s.AddSingleton<I18n>(sp => new I18n(sp.GetService<IReadOnlyList<IWckModule>>() ?? Array.Empty<IWckModule>()));
        s.AddSingleton<IThemePreferenceStore>(_ => new JsonThemePreferenceStore(JsonThemePreferenceStore.DefaultBaseDirectory));
        s.AddSingleton<IThemeService>(sp => new ThemeService(
            args,
            Environment.GetEnvironmentVariable(ThemeService.EnvironmentVariableName),
            sp.GetRequiredService<IThemePreferenceStore>()));

        // Safety core
        s.AddSingleton<IPathCanonicalizer, Win32PathCanonicalizer>();
        s.AddSingleton<ICurrentSidProvider, Win32CurrentSidProvider>();
        s.AddSingleton(_ => ProtectedResources.ForCurrentSystem());
        s.AddSingleton<ISafetyGate>(sp =>
            new SafetyGate(
                sp.GetRequiredService<ProtectedResources>(),
                sp.GetRequiredService<IPathCanonicalizer>(),
                sp.GetRequiredService<ICurrentSidProvider>()));

        // Logging + execution (the one sanctioned destructive layer — §A / §C.0).
        // Logs/backups live under per-user %LocalAppData% (not the app folder): writable even when installed
        // under Program Files, and not world-readable (the .reg backups can contain secret value data).
        string logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WindowsCareKit", "logs");
        s.AddSingleton<ILogRedactor>(_ => LogRedactor.ForCurrentUser());
        s.AddSingleton(sp => new ExecutionLog(
            Path.Combine(logDir, "execution.jsonl"),
            sp.GetRequiredService<ILogRedactor>()));
        s.AddSingleton<IFileDeleteAdapter, RecycleBinFileDeleteAdapter>();
        s.AddSingleton<IRegistryAdapter>(_ =>
            new RegistryDeleteAdapter(Path.Combine(logDir, "regbak")));
        s.AddSingleton<IServiceAdapter, ServiceControlAdapter>();
        s.AddSingleton<ITaskAdapter, ScheduledTaskAdapter>();
        s.AddSingleton<IProcessAdapter, ProcessAdapter>();
        s.AddSingleton<ICopyAdapter, CopyAdapter>();
        s.AddSingleton<IRecycleBinEmptier, RecycleBinEmptier>();
        s.AddSingleton<IFolderOpener, FolderOpener>(); // benign read-only folder open (not a gated action)
        s.AddSingleton<IUrlOpener, UrlOpener>(); // user-clicked external documentation/release links
        // Restore-point creation (PR-5): the protective system-call sink, gate-armed + dispatched by the
        // executor. SRSetRestorePointW via P/Invoke (no process launch). Injected into GatedExecutor below.
        // The capability probe (registered below) is injected so the creator re-checks SR availability before
        // the Win32 call and never reports a fake success against a disabled System Restore (PR-5 audit FIX 2).
        s.AddSingleton<IRestorePointCreator>(sp =>
            new Win32RestorePointCreator(sp.GetRequiredService<IRestorePointCapabilityProbe>()));
        // Register the concrete GatedExecutor once and expose UI/core aliases to that same instance.
        s.AddSingleton(sp => new GatedExecutor(
            sp.GetRequiredService<ISafetyGate>(),
            sp.GetRequiredService<ExecutionLog>(),
            sp.GetRequiredService<IFileDeleteAdapter>(),
            sp.GetRequiredService<IRegistryAdapter>(),
            sp.GetRequiredService<IServiceAdapter>(),
            sp.GetRequiredService<ITaskAdapter>(),
            sp.GetRequiredService<IProcessAdapter>(),
            sp.GetRequiredService<ICopyAdapter>(),
            sp.GetRequiredService<IRestorePointCreator>(),
            sp.GetRequiredService<IRecycleBinEmptier>()));
        s.AddSingleton<IExecutor>(sp => sp.GetRequiredService<GatedExecutor>());
        s.AddSingleton<IPlanExecutor>(sp => new GatedPlanExecutor(sp.GetRequiredService<GatedExecutor>()));

        // Shared read-only Win32 ports.
        s.AddSingleton<IRegistryProbe, Win32RegistryProbe>();
        s.AddSingleton<IInstalledAppReader, Win32InstalledAppReader>();
        s.AddSingleton<IAppxReader, Win32AppxReader>();

        // Restore-point capability probe (PR-5): availability = SR enabled on the system drive AND elevated
        // (NOT mere service presence — SRSetRestorePointW can succeed when SR is off). The wizard flips its
        // toggle from this. The composing LOGIC is host-testable; the real signals are the two Win32 probes.
        s.AddSingleton<ISystemRestoreConfigProbe, Win32SystemRestoreConfigProbe>();
        s.AddSingleton<IElevationProbe, Win32ElevationProbe>();
        s.AddSingleton<IRestorePointCapabilityProbe>(sp => new DefaultRestorePointCapabilityProbe(
            sp.GetRequiredService<ISystemRestoreConfigProbe>(), sp.GetRequiredService<IElevationProbe>()));

        // Backup integrity ring + headless runner (Step 3). Read-only ports + the integrity writer + the
        // execution adapter that bridges the sanctioned GatedExecutor onto the Core IBackupExecutor seam.
        s.AddSingleton<IClock, SystemClock>();
        s.AddSingleton<IHasher, Sha256Hasher>();
        s.AddSingleton<IFileSystem, PhysicalFileSystem>();
        s.AddSingleton<IBackupExecutor>(sp => new BackupExecutorAdapter(sp.GetRequiredService<GatedExecutor>()));
        s.AddSingleton<MigrationRestoreManifestStore>();

        // Shared install/restore checkpoint state (Install + Restore both use it).
        s.AddSingleton<IRestoreStateStore, RestoreStateStore>();

        // Shell view-models.
        s.AddSingleton<SettingsViewModel>();
        s.AddSingleton<MainViewModel>(sp => new MainViewModel(
            sp.GetRequiredService<I18n>(),
            sp.GetRequiredService<IReadOnlyList<IWckModule>>(),
            sp));
    }

}
