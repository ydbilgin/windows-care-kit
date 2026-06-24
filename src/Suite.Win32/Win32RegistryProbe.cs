using Microsoft.Win32;
using WindowsCareKit.Core.Modules.Uninstall;
using CoreHive = WindowsCareKit.Core.Planning.RegistryHive;
using CoreView = WindowsCareKit.Core.Planning.RegistryView;

namespace WindowsCareKit.Win32;

/// <summary>Read-only registry probe used by inventory sources. It never opens writable keys.</summary>
public sealed class Win32RegistryProbe : IRegistryProbe
{
    public IReadOnlyList<string> GetSubKeyNames(CoreHive hive, CoreView view, string subKey)
    {
        try
        {
            using var baseKey = RegistryInterop.OpenBase(hive, view);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            return key is null ? Array.Empty<string>() : key.GetSubKeyNames();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return Array.Empty<string>();
        }
    }

    public RegistryKeySnapshot? ReadKey(CoreHive hive, CoreView view, string subKey)
    {
        try
        {
            using var baseKey = RegistryInterop.OpenBase(hive, view);
            using var key = baseKey.OpenSubKey(subKey, writable: false);
            if (key is null)
                return null;

            var values = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (string name in key.GetValueNames())
                values[name] = key.GetValue(name);
            if (!values.ContainsKey(string.Empty))
                values[string.Empty] = key.GetValue(string.Empty);
            return new RegistryKeySnapshot(values);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
