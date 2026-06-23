namespace WindowsCareKit.Core.Modules.Migration.Detection;

/// <summary>Which inventory source contributed a program record.</summary>
public enum ProgramSourceKind
{
    /// <summary>Classic Win32/MSI from HKLM or HKCU Uninstall registry keys.</summary>
    RegistryUninstall,

    /// <summary>MSI product catalog (msi.dll query). Reserved — M1b.</summary>
    Msi,

    /// <summary>UWP / AppX package family. Reserved — M1b.</summary>
    Appx,

    /// <summary>App Paths registration (HKLM\SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths). Reserved — M1b.</summary>
    AppPaths,

    /// <summary>Start Menu .lnk inventory. Reserved — M1b.</summary>
    StartMenu,
}
