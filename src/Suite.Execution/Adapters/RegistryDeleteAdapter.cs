using System.Globalization;
using System.Text;
using Microsoft.Win32;
using WindowsCareKit.Core.Planning;
using BclHive = Microsoft.Win32.RegistryHive;
using BclView = Microsoft.Win32.RegistryView;
using CoreHive = WindowsCareKit.Core.Planning.RegistryHive;
using CoreView = WindowsCareKit.Core.Planning.RegistryView;

namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// Writes a <c>.reg</c> backup capturing a key (and its subtree) or a single value, so a registry
/// delete can be re-imported manually (spec §3 rollback). Injectable so the executor's tests can verify
/// "backup BEFORE delete" and the fail-closed behaviour when a backup write fails.
/// </summary>
public interface IRegBackupWriter
{
    /// <summary>
    /// Write a standard <c>Windows Registry Editor Version 5.00</c> file at <paramref name="destinationPath"/>
    /// for the given target. When <paramref name="valueName"/> is null the whole key/subtree is captured;
    /// otherwise just that value. Throws if the backup cannot be written (the adapter then refuses to delete).
    /// </summary>
    void WriteBackup(string destinationPath, CoreHive hive, CoreView view, string subKeyPath, string? valueName);
}

/// <summary>
/// Deletes a registry value or key — but always EXPORTS a <c>.reg</c> backup FIRST. If the backup cannot
/// be written, the delete is refused (fail closed: no backup → no delete). The 64/32 view is honored
/// throughout. No <c>reg.exe</c> is used (it is on the command deny-list); the backup is produced from
/// the registry API directly.
/// </summary>
public sealed class RegistryDeleteAdapter : IRegistryAdapter
{
    private readonly IRegBackupWriter _backupWriter;
    private readonly string _backupDir;

    /// <summary>
    /// Default executor wiring: backups land under <c>&lt;logDir&gt;\regbak</c> when the action does not
    /// already carry a <see cref="RegistryDeleteAction.BackupRegFile"/>.
    /// </summary>
    public RegistryDeleteAdapter(string backupDir)
        : this(backupDir, new RegFileBackupWriter())
    {
    }

    /// <summary>Test/seam ctor allowing a fake backup writer.</summary>
    public RegistryDeleteAdapter(string backupDir, IRegBackupWriter backupWriter)
    {
        _backupDir = backupDir ?? throw new ArgumentNullException(nameof(backupDir));
        _backupWriter = backupWriter ?? throw new ArgumentNullException(nameof(backupWriter));
    }

    /// <inheritdoc />
    public void Delete(RegistryDeleteAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        // 1) The backup path is ALWAYS derived by the adapter under its own regbak dir — never taken from the
        //    (publicly settable) action.BackupRegFile, which would be an un-gated arbitrary-path write sink.
        string backupPath = ResolveBackupPath(action);

        // Defense in depth: the resolved path must stay under the regbak base (no traversal).
        string baseFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_backupDir));
        string backupFull = Path.GetFullPath(backupPath);
        if (!backupFull.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Registry backup path escaped the backup directory.");

        // 2) Export the backup FIRST. Any failure here means NO delete (fail closed).
        _backupWriter.WriteBackup(backupPath, action.Hive, action.View, action.SubKeyPath, action.ValueName);

        // 3) Delete, honoring the 64/32 view.
        using RegistryKey baseKey = RegistryKey.OpenBaseKey(MapHive(action.Hive), MapView(action.View));

        if (action.ValueName is not null)
        {
            // Value-delete: open the key writable and remove the single value.
            using RegistryKey? key = baseKey.OpenSubKey(action.SubKeyPath, writable: true);
            if (key is null)
                return; // key already gone — nothing to delete (backup captured the prior state)
#pragma warning disable RS0030 // Sanctioned destructive sink (Suite.Execution): registry value delete, backup taken first.
            key.DeleteValue(action.ValueName, throwOnMissingValue: false);
#pragma warning restore RS0030
            return;
        }

        // Key-delete: open the PARENT writable and delete the last segment's subtree.
        SplitParent(action.SubKeyPath, out string? parentPath, out string lastSegment);
        if (lastSegment.Length == 0)
            throw new InvalidOperationException("Refusing to delete a registry base key (empty subkey path).");

        using RegistryKey? parent = parentPath is null
            ? baseKey
            : baseKey.OpenSubKey(parentPath, writable: true);
        if (parent is null)
            return; // parent already gone
#pragma warning disable RS0030 // Sanctioned destructive sink (Suite.Execution): registry key/subtree delete, backup taken first.
        parent.DeleteSubKeyTree(lastSegment, throwOnMissingSubKey: false);
#pragma warning restore RS0030
    }

    private string ResolveBackupPath(RegistryDeleteAction action)
    {
        string stamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
        string sanitized = Sanitize(action.SubKeyPath);
        return Path.Combine(_backupDir, $"{stamp}_{sanitized}.reg");
    }

    internal static string Sanitize(string subKeyPath)
    {
        var sb = new StringBuilder(subKeyPath.Length);
        foreach (char c in subKeyPath)
            sb.Append(c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or ' ' ? '_' : c);
        string s = sb.ToString().Trim('_');
        return s.Length == 0 ? "key" : (s.Length > 80 ? s[^80..] : s);
    }

    internal static void SplitParent(string subKeyPath, out string? parentPath, out string lastSegment)
    {
        string trimmed = subKeyPath.Trim('\\');
        int idx = trimmed.LastIndexOf('\\');
        if (idx < 0)
        {
            parentPath = null;
            lastSegment = trimmed;
        }
        else
        {
            parentPath = trimmed[..idx];
            lastSegment = trimmed[(idx + 1)..];
        }
    }

    internal static BclHive MapHive(CoreHive hive) => hive switch
    {
        CoreHive.ClassesRoot => BclHive.ClassesRoot,
        CoreHive.CurrentUser => BclHive.CurrentUser,
        CoreHive.LocalMachine => BclHive.LocalMachine,
        CoreHive.Users => BclHive.Users,
        CoreHive.CurrentConfig => BclHive.CurrentConfig,
        _ => BclHive.LocalMachine,
    };

    internal static BclView MapView(CoreView view)
        => view == CoreView.Registry32 ? BclView.Registry32 : BclView.Registry64;
}
