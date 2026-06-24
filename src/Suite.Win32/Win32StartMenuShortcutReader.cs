using System.Runtime.InteropServices;
using System.Text;
using WindowsCareKit.Core.Modules.Migration.Detection;

namespace WindowsCareKit.Win32;

/// <summary>Read-only Start Menu .lnk reader backed by IShellLinkW.</summary>
public sealed class Win32StartMenuShortcutReader : IStartMenuShortcutReader
{
    public IReadOnlyList<StartMenuShortcut> ReadShortcuts()
    {
        var shortcuts = new List<StartMenuShortcut>();
        foreach (string root in StartMenuRoots())
        {
            if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
                continue;

            foreach (string linkPath in EnumerateLinks(root))
            {
                string? target = ResolveTarget(linkPath);
                string displayName = Path.GetFileNameWithoutExtension(linkPath);
                shortcuts.Add(new StartMenuShortcut(displayName, linkPath, target));
            }
        }

        return shortcuts;
    }

    private static IEnumerable<string> StartMenuRoots()
    {
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu);
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.StartMenu);
        yield return Path.Combine(programData, "Programs");
        yield return Path.Combine(appData, "Programs");
    }

    private static IEnumerable<string> EnumerateLinks(string root)
    {
        try
        {
            return Directory.EnumerateFiles(root, "*.lnk", SearchOption.AllDirectories).ToArray();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            return Array.Empty<string>();
        }
    }

    private static string? ResolveTarget(string linkPath)
    {
        IShellLinkW? shellLink = null;
        try
        {
            shellLink = (IShellLinkW)(object)new CShellLink();
            if (shellLink is not IPersistFile persist)
                return null;

            persist.Load(linkPath, 0);
            var path = new StringBuilder(32768);
            shellLink.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            string result = path.ToString();
            return string.IsNullOrWhiteSpace(result) ? null : result;
        }
        catch (Exception ex) when (ex is COMException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
        finally
        {
            if (shellLink is not null)
                Marshal.FinalReleaseComObject(shellLink);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private sealed class CShellLink { }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath(
            [Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile,
            int cchMaxPath,
            IntPtr pfd,
            uint fFlags);

        void GetIDList(out IntPtr ppidl);
        void SetIDList(IntPtr pidl);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMaxName);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMaxPath);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMaxPath);
        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);
        void GetHotkey(out short pwHotkey);
        void SetHotkey(short wHotkey);
        void GetShowCmd(out int piShowCmd);
        void SetShowCmd(int iShowCmd);
        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszIconPath, int cchIconPath, out int piIcon);
        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string pszIconPath, int iIcon);
        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string pszPathRel, uint dwReserved);
        void Resolve(IntPtr hwnd, uint fFlags);
        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid pClassID);
        void IsDirty();
        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);
        void Save([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, bool fRemember);
        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);
        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
