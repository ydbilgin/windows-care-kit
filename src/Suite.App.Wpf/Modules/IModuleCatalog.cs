namespace WindowsCareKit.App.Modules;

/// <summary>Shell-internal seam for how the module set is discovered. Modules never see this.</summary>
public interface IModuleCatalog
{
    /// <summary>One discovery pass. Returns the modules and what may honestly be said about the component set
    /// as a single value: a caller cannot take the list and drop the health, which is exactly what the previous
    /// read-after-call <c>Diagnostics</c> property permitted.</summary>
    ModuleCatalogResult LoadModules();
}
