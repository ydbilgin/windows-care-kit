namespace WindowsCareKit.App.Modules;

/// <summary>
/// The current, compile-time module set. M4 replaces this with directory discovery behind the same
/// <see cref="IModuleCatalog"/> interface.
/// </summary>
public sealed class StaticModuleCatalog : IModuleCatalog
{
    public IReadOnlyList<IWckModule> LoadModules()
        => new IWckModule[]
        {
            new UninstallModule(),
            new CleanModule(),
            new BackupModule(),
            new MigrationModule(),
            new RestoreModule(),
            new InstallModule(),
            new SettingsModule(),
        };
}
