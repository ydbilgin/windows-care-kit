using System.Globalization;
using System.Text;
using Microsoft.Win32;
using BclHive = Microsoft.Win32.RegistryHive;
using BclView = Microsoft.Win32.RegistryView;
using CoreHive = WindowsCareKit.Core.Planning.RegistryHive;
using CoreView = WindowsCareKit.Core.Planning.RegistryView;

namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// Produces a standard <c>Windows Registry Editor Version 5.00</c> file by reading the live registry via
/// the API (no <c>reg.exe</c>). Captures a single value or a whole subtree (recursively) honoring the
/// 64/32 view. The output is re-importable by RegEdit, giving the registry delete a manual rollback path
/// (spec §3). Throws if the file cannot be written — the adapter treats that as "no backup → no delete".
/// </summary>
public sealed class RegFileBackupWriter : IRegBackupWriter
{
    /// <inheritdoc />
    public void WriteBackup(string destinationPath, CoreHive hive, CoreView view, string subKeyPath, string? valueName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string? dir = Path.GetDirectoryName(destinationPath);
        if (!string.IsNullOrEmpty(dir))
            CreateSecuredDirectory(dir);

        var sb = new StringBuilder();
        sb.Append("Windows Registry Editor Version 5.00\r\n\r\n");

        string hivePrefix = HivePrefix(hive);

        using RegistryKey baseKey = RegistryKey.OpenBaseKey(MapHive(hive), MapView(view));
        using RegistryKey? key = baseKey.OpenSubKey(subKeyPath, writable: false);

        if (key is null)
        {
            // Target already gone: still write a header-only backup so the file exists (the prior state
            // was "absent"). This keeps "backup before delete" honest without failing the delete.
            File.WriteAllText(destinationPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            return;
        }

        if (valueName is not null)
        {
            // Single-value backup: emit the key header with just this one value.
            sb.Append('[').Append(hivePrefix).Append('\\').Append(subKeyPath).Append("]\r\n");
            if (key.GetValueNames().Any(n => string.Equals(n, valueName, StringComparison.OrdinalIgnoreCase)))
                AppendValue(sb, valueName, key.GetValue(valueName, null, RegistryValueOptions.DoNotExpandEnvironmentNames), key.GetValueKind(valueName));
            sb.Append("\r\n");
        }
        else
        {
            // Whole-subtree backup, depth-first.
            AppendKeyRecursive(sb, key, hivePrefix + "\\" + subKeyPath.TrimEnd('\\'));
        }

        File.WriteAllText(destinationPath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    /// <summary>
    /// Creates the regbak directory and best-effort restricts it to the current user + SYSTEM + Administrators
    /// (removing inherited Users/Everyone read), because the <c>.reg</c> files contain registry value data in
    /// cleartext (spec §3/§9). The dir already sits under per-user <c>%LocalAppData%</c>; this hardens it further.
    /// </summary>
    private static void CreateSecuredDirectory(string dir)
    {
        var info = Directory.CreateDirectory(dir);
        try
        {
            var security = new System.Security.AccessControl.DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);

            var user = System.Security.Principal.WindowsIdentity.GetCurrent().User;
            if (user is not null)
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    user,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));

            foreach (var sid in new[]
            {
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.LocalSystemSid, null),
                new System.Security.Principal.SecurityIdentifier(System.Security.Principal.WellKnownSidType.BuiltinAdministratorsSid, null),
            })
            {
                security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(
                    sid,
                    System.Security.AccessControl.FileSystemRights.FullControl,
                    System.Security.AccessControl.InheritanceFlags.ContainerInherit | System.Security.AccessControl.InheritanceFlags.ObjectInherit,
                    System.Security.AccessControl.PropagationFlags.None,
                    System.Security.AccessControl.AccessControlType.Allow));
            }

            System.IO.FileSystemAclExtensions.SetAccessControl(info, security);
        }
        catch (Exception)
        {
            // Best-effort hardening; the per-user %LocalAppData% location is the primary protection.
        }
    }

    private static void AppendKeyRecursive(StringBuilder sb, RegistryKey key, string fullPath)
    {
        sb.Append('[').Append(fullPath).Append("]\r\n");
        foreach (string name in key.GetValueNames())
        {
            object? value = key.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames);
            AppendValue(sb, name, value, key.GetValueKind(name));
        }
        sb.Append("\r\n");

        foreach (string subName in key.GetSubKeyNames())
        {
            using RegistryKey? sub = key.OpenSubKey(subName, writable: false);
            if (sub is not null)
                AppendKeyRecursive(sb, sub, fullPath + "\\" + subName);
        }
    }

    private static void AppendValue(StringBuilder sb, string name, object? value, RegistryValueKind kind)
    {
        // Value name: "@" for the default value, otherwise a quoted/escaped name.
        sb.Append(name.Length == 0 ? "@" : Quote(name)).Append('=');

        switch (kind)
        {
            case RegistryValueKind.String:
                string str = value?.ToString() ?? string.Empty;
                // A REG_SZ can legally hold CR/LF/NUL, but those cannot survive the quoted-string form
                // (RegEdit splits on the newline / truncates at the NUL). reg.exe emits such values as the
                // hex(1) form — the raw UTF-16LE bytes incl. the terminating null — so they round-trip exactly.
                if (NeedsHexString(str))
                    sb.Append("hex(1):").Append(HexBytes(Encoding.Unicode.GetBytes(str + "\0")));
                else
                    sb.Append(Quote(str));
                break;
            case RegistryValueKind.ExpandString:
                sb.Append("hex(2):").Append(HexBytes(Encoding.Unicode.GetBytes((value?.ToString() ?? string.Empty) + "\0")));
                break;
            case RegistryValueKind.MultiString:
                var joined = string.Join("\0", (string[])(value ?? Array.Empty<string>())) + "\0\0";
                sb.Append("hex(7):").Append(HexBytes(Encoding.Unicode.GetBytes(joined)));
                break;
            case RegistryValueKind.DWord:
                sb.Append("dword:").Append(((uint)Convert.ToInt32(value, CultureInfo.InvariantCulture)).ToString("x8", CultureInfo.InvariantCulture));
                break;
            case RegistryValueKind.QWord:
                sb.Append("hex(b):").Append(HexBytes(BitConverter.GetBytes(Convert.ToInt64(value, CultureInfo.InvariantCulture))));
                break;
            case RegistryValueKind.Binary:
                sb.Append("hex:").Append(HexBytes((byte[])(value ?? Array.Empty<byte>())));
                break;
            default:
                sb.Append("hex(0):");
                break;
        }

        sb.Append("\r\n");
    }

    /// <summary>
    /// True when a REG_SZ value contains a control character that the quoted-string form cannot represent
    /// faithfully — a CR, LF, or embedded NUL. Such values must be emitted as <c>hex(1):</c> (like reg.exe).
    /// </summary>
    private static bool NeedsHexString(string s)
    {
        foreach (char c in s)
            if (c is '\r' or '\n' or '\0')
                return true;
        return false;
    }

    private static string Quote(string s)
    {
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (char c in s)
        {
            if (c is '\\' or '"')
                sb.Append('\\');
            sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string HexBytes(byte[] bytes)
        => string.Join(",", bytes.Select(b => b.ToString("x2", CultureInfo.InvariantCulture)));

    internal static string HivePrefix(CoreHive hive) => hive switch
    {
        CoreHive.ClassesRoot => "HKEY_CLASSES_ROOT",
        CoreHive.CurrentUser => "HKEY_CURRENT_USER",
        CoreHive.LocalMachine => "HKEY_LOCAL_MACHINE",
        CoreHive.Users => "HKEY_USERS",
        CoreHive.CurrentConfig => "HKEY_CURRENT_CONFIG",
        _ => "HKEY_LOCAL_MACHINE",
    };

    private static BclHive MapHive(CoreHive hive) => RegistryDeleteAdapter.MapHive(hive);
    private static BclView MapView(CoreView view) => RegistryDeleteAdapter.MapView(view);
}
