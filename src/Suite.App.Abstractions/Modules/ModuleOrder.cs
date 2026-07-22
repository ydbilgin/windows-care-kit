namespace WindowsCareKit.App.Modules;

/// <summary>
/// The single owner of the shell's left-nav ordering policy (CONST-01). The shell's module catalog
/// sorts ascending by <see cref="IWckModule.Order"/>; each feature module returns its band from here instead of
/// a bare literal. Feature modules occupy the 10..60 band spaced by tens (leaving room to insert a new feature
/// between two existing ones without renumbering); settings-class modules sit at the trailing
/// <see cref="Settings"/> band so they always render last regardless of feature growth.
/// </summary>
public static class ModuleOrder
{
    public const int Uninstall = 10;
    public const int Clean = 20;
    public const int Backup = 30;
    public const int Migration = 40;
    public const int Restore = 50;
    public const int Install = 60;

    /// <summary>Trailing band for settings-class modules; always sorts after every feature module.</summary>
    public const int Settings = 900;
}
