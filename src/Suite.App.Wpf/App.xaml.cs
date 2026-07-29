using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.Deployment;
using WindowsCareKit.App.Execution;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Modules;
using WindowsCareKit.App.Startup;
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
    /// <summary>The composition root's provider. Private on purpose: it is not a service locator — views and
    /// view models take explicit constructor dependencies instead of reaching through the application object.</summary>
    private IServiceProvider Services { get; set; } = null!;

    /// <summary>Verification switch: report the resolved layout and exit, without showing a window and — the
    /// load-bearing part — without loading a single module, since module loading executes third-party code.
    /// This is what CI runs against a published artifact.</summary>
    internal const string VerifyLayoutFlag = "--verify-layout";

    private const string FatalDialogTitle = "Windows Care Kit";

    /// <summary>
    /// The gate runs BEFORE <c>base.OnStartup</c>, which is the only thing this override does other than
    /// gather the gate's two inputs. WPF's base implementation raises the public <c>Startup</c> event, so
    /// running it first would let a subscriber execute before the app root was verified — and
    /// <c>DirectoryModuleCatalog</c> loads assemblies from that root, i.e. it executes third-party code.
    /// The ordering lives in <see cref="StartupSequence"/>, which reaches <c>base.OnStartup</c> only through
    /// <see cref="IStartupSequenceHost.PublishStartupEvent"/> on the branch where the preflight passed.
    /// <para>The one <c>async void</c> in the shell, as the WPF lifecycle override it is; everything it starts
    /// is awaited below.</para>
    /// </summary>
    protected override async void OnStartup(StartupEventArgs e)
    {
        await StartupSequence.RunAsync(
            HasFlag(e.Args, VerifyLayoutFlag),
            StartupPreflight.Run(AppLayout.Current),
            new WpfStartupHost(this, e));
    }

    /// <summary>Adapts the WPF application onto the sequence's host contract. Nothing here decides anything —
    /// the order these run in belongs to <see cref="StartupSequence"/>.</summary>
    private sealed class WpfStartupHost(App app, StartupEventArgs e) : IStartupSequenceHost
    {
        /// <summary>A GUI-subsystem process has no console of its own; the line lands wherever the caller
        /// redirected stdout (how CI reads it) and is discarded otherwise. The exit code is the contract
        /// either way.</summary>
        public void ReportVerification(string reportLine) => Console.Out.WriteLine(reportLine);

        /// <summary>Hardcoded English: the string table is exactly what is unavailable, so I18n here would
        /// print the raw keys this dialog exists to explain (the v0.1.2-beta defect).</summary>
        public void ShowFatalError(string message) =>
            MessageBox.Show(message, FatalDialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);

        public void Shutdown(int exitCode) => app.Shutdown(exitCode);

        public void PublishStartupEvent() => app.PublishStartupEvent(e);

        public Task StartShellAsync() => app.StartShellAsync(e.Args);
    }

    /// <summary>Raises WPF's public <c>Startup</c> event. Private, and called from one place, so the gate
    /// cannot be bypassed by a later edit that merely reorders this class.</summary>
    private void PublishStartupEvent(StartupEventArgs e) => base.OnStartup(e);

    private async Task StartShellAsync(string[] args)
    {
        var services = new ServiceCollection();
        ConfigureServices(services, args);
        Services = services.BuildServiceProvider();

        var i18n = Services.GetRequiredService<I18n>();
        if (!TryLoadStartupLanguage(i18n, ResolveCulture(args)))
            return; // the fatal dialog is up and shutdown is requested; do not raise a window on top of it

        var themeService = Services.GetRequiredService<IThemeService>();
        ThemeDictionary.ApplyStartupTheme(Resources, themeService.AppliedTheme);

        var main = Services.GetRequiredService<MainViewModel>();
        if (main.SelectNavByKey(ExtractOption(args, "--screen")))
            main.ShowFirstRun = false; // deep-linked to a module → skip the first-run modal (demo/screenshot mode)
        var window = new MainWindow { DataContext = main };
        window.Show();

        try
        {
            await main.OnShellStartupAsync(); // kick off and observe the read-only inventory load
        }
        catch (Exception ex)
        {
            Trace.TraceError("Shell startup load failed: " + ex);
        }
    }

    /// <summary>
    /// The first string-table load is still part of the gate. Preflight proved the file usable a moment ago,
    /// but this is a second read of the same file, so it can fail in between — and there is no last known-good
    /// table yet to fall back on, which would leave exactly the raw-key UI the gate exists to prevent. A
    /// failure here is therefore fatal, reported through the very same verdict-to-message mapping preflight
    /// uses. Every LATER load (live language switching) keeps the live table instead — see
    /// <see cref="OnBaseStringTableUnusable"/>.
    /// </summary>
    private bool TryLoadStartupLanguage(I18n i18n, string culture)
    {
        BaseStringTableResult? failure = null;
        void Record(object? _, BaseStringTableResult result) => failure ??= result;

        i18n.BaseStringTableUnusable += Record;
        try
        {
            i18n.Load(culture);
        }
        finally
        {
            i18n.BaseStringTableUnusable -= Record;
        }

        if (failure is null)
        {
            i18n.BaseStringTableUnusable += OnBaseStringTableUnusable;
            return true;
        }

        StartupPreflightResult verdict = StartupPreflight.FromBaseStringTable(AppLayout.Current, failure);
        MessageBox.Show(
            verdict.ToFatalMessage(), FatalDialogTitle, MessageBoxButton.OK, MessageBoxImage.Error);
        Shutdown(verdict.ExitCode);
        return false;
    }

    /// <summary>
    /// The base string table became unusable while the app was running — the supported <c>ExtractedZip</c>
    /// mode is user-writable, so the file can be deleted or corrupted at any time. The live strings are kept,
    /// so the UI still reads correctly; this says why the language did not change. Hardcoded English for the
    /// same reason the fatal dialog is.
    /// </summary>
    private static void OnBaseStringTableUnusable(object? sender, BaseStringTableResult failure)
    {
        MessageBox.Show(
            "Windows Care Kit could not reload its text, so the language was not changed." +
            Environment.NewLine + Environment.NewLine +
            "File: " + failure.FilePath + Environment.NewLine +
            "Reason: " + failure.FailureDetail + Environment.NewLine + Environment.NewLine +
            "The interface is still showing the text loaded at startup. Reinstall Windows Care Kit, or " +
            "re-extract the release archive, to replace the missing or damaged file.",
            FatalDialogTitle,
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
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

    /// <summary>
    /// Reads a bare boolean switch (<c>--name</c>) from the argv, case-insensitively. Separate from
    /// <see cref="ExtractOption"/> on purpose: that parser consumes the NEXT argument as a value, which for a
    /// flag would silently swallow an unrelated argument.
    /// </summary>
    internal static bool HasFlag(string[] args, string name)
    {
        foreach (string arg in args)
        {
            if (arg.Equals(name, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
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
        // The payload-root guard's forbidden set. Registered here, at the composition root, because the
        // rule lives in Suite.Core (pure) while "which directory is the app's" is a deployment fact owned
        // by AppLayout. A lambda so it is computed on first resolve, not at registration.
        s.AddSingleton(_ => AppPayloadRoots.ForCurrentProcess());
        s.AddSingleton<ISafetyGate>(sp =>
            new SafetyGate(
                sp.GetRequiredService<ProtectedResources>(),
                sp.GetRequiredService<IPathCanonicalizer>(),
                sp.GetRequiredService<ICurrentSidProvider>(),
                sp.GetRequiredService<IElevationProbe>()));

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
            sp.GetRequiredService<IRecycleBinEmptier>(),
            new AppxRemoveAdapter(sp.GetRequiredService<ExecutionLog>())));
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
        s.AddSingleton<IFileWriter, SanctionedFileWriter>();
        s.AddSingleton<IBackupExecutor>(sp => new BackupExecutorAdapter(sp.GetRequiredService<GatedExecutor>()));
        s.AddSingleton<MigrationRestoreManifestStore>();

        // Shared install/restore checkpoint state (Install + Restore both use it).
        s.AddSingleton<IRestoreStateStore, RestoreStateStore>();

        // Migration restore service (Restore module). The concrete Execution service + its Abstractions-port
        // adapter are composed here at the root so the Restore module depends only on IMigrationRestoreService
        // (Round 2 DIP-03: no Suite.Module.Restore -> Suite.Execution edge).
        s.AddSingleton<MigrationRestoreService>(sp => new MigrationRestoreService(
            new MigrationRestoreRunner(
                new RecipePathResolver(ProfileRoots.ForCurrentUser()),
                sp.GetRequiredService<ISafetyGate>()),
            sp.GetRequiredService<GatedExecutor>(),
            sp.GetRequiredService<IRestoreStateStore>()));
        s.AddSingleton<IMigrationRestoreService>(sp =>
            new GatedMigrationRestoreService(sp.GetRequiredService<MigrationRestoreService>()));

        // Shell view-models.
        s.AddSingleton<SettingsViewModel>();
        s.AddSingleton<MainViewModel>(sp => new MainViewModel(
            sp.GetRequiredService<I18n>(),
            sp.GetRequiredService<IReadOnlyList<IWckModule>>(),
            sp));
    }

}
