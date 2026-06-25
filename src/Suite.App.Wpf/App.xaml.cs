using System.Globalization;
using System.IO;
using System.Security.Principal;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.App.Localization;
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
        ConfigureServices(services);
        Services = services.BuildServiceProvider();

        var i18n = Services.GetRequiredService<I18n>();
        i18n.Load(CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("tr", StringComparison.OrdinalIgnoreCase) ? "tr" : "en");

        var main = Services.GetRequiredService<MainViewModel>();
        var window = new MainWindow { DataContext = main };
        window.Show();

        _ = main.Uninstall.LoadAsync(); // kick off the read-only inventory load
    }

    private static void ConfigureServices(IServiceCollection s)
    {
        s.AddSingleton<I18n>();

        // Safety core
        s.AddSingleton<IPathCanonicalizer, Win32PathCanonicalizer>();
        s.AddSingleton(_ => ProtectedResources.ForCurrentSystem());
        s.AddSingleton<ISafetyGate>(sp =>
            new SafetyGate(sp.GetRequiredService<ProtectedResources>(), sp.GetRequiredService<IPathCanonicalizer>()));

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
        // Restore-point creation (PR-5): the protective system-call sink, gate-armed + dispatched by the
        // executor. SRSetRestorePointW via P/Invoke (no process launch). Injected into GatedExecutor below.
        // The capability probe (registered below) is injected so the creator re-checks SR availability before
        // the Win32 call and never reports a fake success against a disabled System Restore (PR-5 audit FIX 2).
        s.AddSingleton<IRestorePointCreator>(sp =>
            new Win32RestorePointCreator(sp.GetRequiredService<IRestorePointCapabilityProbe>()));
        // Register the concrete GatedExecutor once (CleanViewModel needs the concrete type for
        // ExecuteWithReport) and alias IExecutor to that same instance. The IRestorePointCreator above is
        // resolved into its (optional) ctor param.
        s.AddSingleton<GatedExecutor>();
        s.AddSingleton<IExecutor>(sp => sp.GetRequiredService<GatedExecutor>());

        // Uninstall module (read-only readers + probe + per-user AppX remover).
        s.AddSingleton<IRegistryProbe, Win32RegistryProbe>();
        s.AddSingleton<IInstalledAppReader, Win32InstalledAppReader>();
        s.AddSingleton<IAppxReader, Win32AppxReader>();
        s.AddSingleton<IMsiCatalog, Win32MsiCatalog>();
        s.AddSingleton<IStartMenuShortcutReader, Win32StartMenuShortcutReader>();
        s.AddSingleton<IContentSignatureProbe, Win32ContentSignatureProbe>();
        s.AddSingleton<ILeftoverProbe, Win32LeftoverProbe>();
        s.AddSingleton<IAppxRemover>(sp => new Win32AppxRemover(sp.GetRequiredService<ExecutionLog>()));
        // Restore-point capability probe (PR-5): availability = SR enabled on the system drive AND elevated
        // (NOT mere service presence — SRSetRestorePointW can succeed when SR is off). The wizard flips its
        // toggle from this. The composing LOGIC is host-testable; the real signals are the two Win32 probes.
        s.AddSingleton<ISystemRestoreConfigProbe, Win32SystemRestoreConfigProbe>();
        s.AddSingleton<IElevationProbe, Win32ElevationProbe>();
        s.AddSingleton<IRestorePointCapabilityProbe>(sp => new DefaultRestorePointCapabilityProbe(
            sp.GetRequiredService<ISystemRestoreConfigProbe>(), sp.GetRequiredService<IElevationProbe>()));

        // Clean module (read-only probes/services).
        s.AddSingleton<IJunkProbe, Win32JunkProbe>();
        s.AddSingleton<IStartupProbe, Win32StartupProbe>();
        s.AddSingleton<IBrowserExtensionInventory, Win32BrowserExtensionInventory>();
        s.AddSingleton<IRecycleBinService, Win32RecycleBinService>();

        // Migration Slice-A: read-only sources are only enumerated when MainViewModel navigates to Migration.
        // Construction stores seams only; no registry/filesystem scan and no restore service is wired.
        s.AddSingleton<IRecipeFileSystem, Win32RecipeFileSystem>();
        s.AddSingleton<IProgramSource>(sp => new RegistryUninstallSource(
            sp.GetRequiredService<IInstalledAppReader>(), new Win32PathCanonicalizer()));
        s.AddSingleton<IProgramSource>(_ => new MsiProductSource(
            new Win32MsiCatalog(), new Win32PathCanonicalizer(), CurrentUserSid()));
        s.AddSingleton<IProgramSource>(sp => new AppxProgramSource(
            sp.GetRequiredService<IAppxReader>(), new Win32PathCanonicalizer()));
        s.AddSingleton<IProgramSource>(sp => new AppPathsSource(
            sp.GetRequiredService<IRegistryProbe>(), new Win32PathCanonicalizer()));
        s.AddSingleton<IProgramSource>(sp => new StartMenuSource(
            sp.GetRequiredService<IStartMenuShortcutReader>(), new Win32PathCanonicalizer()));
        s.AddSingleton<IMigrationScanService>(sp => new MigrationScanService(
            sp.GetServices<IProgramSource>(),
            ProfileRoots.ForCurrentUser,
            sp.GetRequiredService<IRecipeFileSystem>(),
            sp.GetRequiredService<IContentSignatureProbe>()));

        // Backup module (manifest loader + env expander + planner + report writer).
        s.AddSingleton<IEnvironmentExpander, Win32EnvironmentExpander>();
        s.AddSingleton<IManifestLoader, ManifestLoader>();
        s.AddSingleton<BackupPlanner>();
        // The report writer redacts the username/profile out of RAPOR.md/MANUAL_TODO.md (they land on
        // external/USB media); ILogRedactor is already registered (LogRedactor.ForCurrentUser) so this auto-wires.
        s.AddSingleton<BackupReportWriter>();
        // Backup integrity ring + headless runner (Step 3). Read-only ports + the integrity writer + the
        // execution adapter that bridges the sanctioned GatedExecutor onto the Core IBackupExecutor seam.
        s.AddSingleton<IClock, SystemClock>();
        s.AddSingleton<IHasher, Sha256Hasher>();
        s.AddSingleton<IFileSystem, PhysicalFileSystem>();
        s.AddSingleton<IIntegrityWriter, BackupIntegrityWriter>();
        s.AddSingleton<IBackupExecutor>(sp => new BackupExecutorAdapter(sp.GetRequiredService<GatedExecutor>()));
        s.AddSingleton<BackupRunner>();

        // Install/Restore (Kur) module (manifest loader + driver/auth guards + state store + planner).
        s.AddSingleton<IInstallManifestLoader, InstallManifestLoader>();
        s.AddSingleton<IAuthProbe, Win32AuthProbe>();
        s.AddSingleton<IDriverGuard, Win32DriverGuard>();
        s.AddSingleton<IRestoreStateStore, RestoreStateStore>();
        s.AddSingleton<InstallPlanner>();
        // Host-safe EXPORT slice (Step 3): the plan writer + the thin runner that projects a built plan into
        // install_plan.json (the writer re-gates the payload root). The IInstallExecutor seam is declared in Core
        // but intentionally NOT wired here — execute mode is Step 4, so the runner's optional executor stays null
        // and no dormant adapter exists. IClock is already registered (Backup ring).
        s.AddSingleton<IInstallPlanWriter, InstallPlanWriter>();
        s.AddSingleton(sp => new InstallRunner(
            sp.GetRequiredService<IInstallPlanWriter>(), sp.GetRequiredService<IClock>()));

        // View-models
        s.AddSingleton<UninstallViewModel>();
        s.AddSingleton<CleanViewModel>();
        s.AddSingleton<BackupViewModel>();
        s.AddSingleton<MigrationViewModel>();
        s.AddSingleton<InstallViewModel>();
        s.AddSingleton<MainViewModel>();
    }

    private static string? CurrentUserSid()
    {
        try
        {
            using WindowsIdentity identity = WindowsIdentity.GetCurrent();
            return identity.User?.Value;
        }
        catch
        {
            return null;
        }
    }
}
