using Xunit;

namespace WindowsCareKit.Tests;

public sealed class BannedSymbolsTests
{
    [Fact]
    public void Banned_symbols_include_the_destructive_api_overloads_that_guard_the_executor_boundary()
    {
        string root = FindRepoRoot();
        var docIds = File.ReadAllLines(Path.Combine(root, "BannedSymbols.txt"))
            .Select(line => line.Split(';', 2)[0].Trim())
            .Where(line => line.Length > 0)
            .ToHashSet(StringComparer.Ordinal);

        string[] required =
        {
            "M:System.IO.FileInfo.Delete",
            "M:System.IO.DirectoryInfo.Delete",
            "M:System.IO.DirectoryInfo.Delete(System.Boolean)",
            "M:System.IO.FileInfo.MoveTo(System.String)",
            "M:System.IO.FileInfo.MoveTo(System.String,System.Boolean)",
            "M:System.IO.DirectoryInfo.MoveTo(System.String)",
            "M:System.IO.File.Replace(System.String,System.String,System.String)",
            "M:System.IO.File.Replace(System.String,System.String,System.String,System.Boolean)",
            "M:Microsoft.Win32.RegistryKey.SetValue(System.String,System.Object,Microsoft.Win32.RegistryValueKind)",
            "M:Microsoft.Win32.Registry.SetValue(System.String,System.String,System.Object)",
            "M:Microsoft.Win32.Registry.SetValue(System.String,System.String,System.Object,Microsoft.Win32.RegistryValueKind)",
            "M:Microsoft.Win32.RegistryKey.CreateSubKey(System.String,System.Boolean)",
            "M:Microsoft.Win32.RegistryKey.CreateSubKey(System.String,Microsoft.Win32.RegistryKeyPermissionCheck)",
            "M:Microsoft.Win32.RegistryKey.CreateSubKey(System.String,System.Boolean,Microsoft.Win32.RegistryOptions)",
            "M:Microsoft.Win32.RegistryKey.CreateSubKey(System.String,Microsoft.Win32.RegistryKeyPermissionCheck,System.Security.AccessControl.RegistrySecurity)",
            "M:Microsoft.Win32.RegistryKey.CreateSubKey(System.String,Microsoft.Win32.RegistryKeyPermissionCheck,Microsoft.Win32.RegistryOptions)",
            "M:Microsoft.Win32.RegistryKey.CreateSubKey(System.String,Microsoft.Win32.RegistryKeyPermissionCheck,Microsoft.Win32.RegistryOptions,System.Security.AccessControl.RegistrySecurity)",
        };

        foreach (string docId in required)
            Assert.Contains(docId, docIds);
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "BannedSymbols.txt")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new DirectoryNotFoundException("Could not find repository root containing BannedSymbols.txt.");
    }
}
