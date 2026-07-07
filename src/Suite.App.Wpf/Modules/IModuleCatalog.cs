namespace WindowsCareKit.App.Modules;

/// <summary>Shell-internal seam for how the module set is discovered. Modules never see this.</summary>
public interface IModuleCatalog
{
    IReadOnlyList<IWckModule> LoadModules();
}
