using System.Globalization;
using System.Security.Cryptography;
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

/// <summary>Raised when a generated registry-backup destination already exists and the caller should retry.</summary>
internal sealed class RegBackupCollisionException : IOException
{
    public RegBackupCollisionException(string path, Exception inner)
        : base($"Registry backup file already exists: {path}", inner)
    {
        Path = path;
    }

    public string Path { get; }
}

/// <summary>
/// Deletes a registry value or key — but always EXPORTS a <c>.reg</c> backup FIRST. If the backup cannot
/// be written, the delete is refused (fail closed: no backup → no delete). The 64/32 view is honored
/// throughout. No <c>reg.exe</c> is used (it is on the command deny-list); the backup is produced from
/// the registry API directly.
/// </summary>
public sealed class RegistryDeleteAdapter : IRegistryAdapter
{
    private const int MaxBackupPathAttempts = 5;
    private const int MaxBackupFileNameLength = 120;
    private static readonly TimeSpan BackupPathRetryDelay = TimeSpan.FromMilliseconds(10);

    private readonly IRegBackupWriter _backupWriter;
    private readonly string _backupDir;
    private readonly Func<DateTime> _utcNow;
    private readonly Func<Guid> _newGuid;

    /// <summary>
    /// Default executor wiring: backups land under <c>&lt;logDir&gt;\regbak</c> when the action does not
    /// already carry a <see cref="RegistryDeleteAction.BackupRegFile"/>.
    /// </summary>
    public RegistryDeleteAdapter(string backupDir)
        : this(backupDir, new RegFileBackupWriter())
    {
    }

    /// <summary>Test/seam ctor allowing a fake backup writer.</summary>
    public RegistryDeleteAdapter(
        string backupDir,
        IRegBackupWriter backupWriter,
        Func<DateTime>? utcNow = null,
        Func<Guid>? newGuid = null)
    {
        _backupDir = backupDir ?? throw new ArgumentNullException(nameof(backupDir));
        _backupWriter = backupWriter ?? throw new ArgumentNullException(nameof(backupWriter));
        _utcNow = utcNow ?? (() => DateTime.UtcNow);
        _newGuid = newGuid ?? Guid.NewGuid;
    }

    /// <inheritdoc />
    public void Delete(RegistryDeleteAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        string baseFull = Path.TrimEndingDirectorySeparator(Path.GetFullPath(_backupDir));

        // 1) The backup path is ALWAYS derived by the adapter under its own regbak dir — never taken from the
        //    (publicly settable) action.BackupRegFile, which would be an un-gated arbitrary-path write sink.
        //    CreateNew collisions retry with a fresh high-resolution stamp/Guid. Any other backup failure
        //    still fails closed before the delete.
        for (int attempt = 1; ; attempt++)
        {
            string backupPath = ResolveBackupPath(action);

            // Defense in depth: the resolved path must stay under the regbak base (no traversal).
            string backupFull = Path.GetFullPath(backupPath);
            if (!backupFull.StartsWith(baseFull + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Registry backup path escaped the backup directory.");

            try
            {
                // 2) Export the backup FIRST. Any failure here means NO delete (fail closed).
                _backupWriter.WriteBackup(backupPath, action.Hive, action.View, action.SubKeyPath, action.ValueName);
                break;
            }
            catch (RegBackupCollisionException) when (attempt < MaxBackupPathAttempts)
            {
                Thread.Sleep(BackupPathRetryDelay);
            }
        }

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
        string stamp = _utcNow().ToString("yyyyMMdd_HHmmssfffffff", CultureInfo.InvariantCulture);
        string identity = IdentityHash(action.SubKeyPath, action.ValueName ?? "<key>");
        string key = Sanitize(action.SubKeyPath);
        string target = action.ValueName is null
            ? "key"
            : "value_" + Sanitize(action.ValueName.Length == 0 ? "default" : action.ValueName);
        string suffix = _newGuid().ToString("N")[..8];
        string[] parts = FitNameParts(key, target, identity, stamp, suffix);
        return Path.Combine(_backupDir, $"{stamp}_{parts[0]}_{parts[1]}_{identity}_{suffix}.reg");
    }

    internal static string Sanitize(string subKeyPath)
    {
        var sb = new StringBuilder(subKeyPath.Length);
        foreach (char c in subKeyPath)
            sb.Append(c is '\\' or '/' or ':' or '*' or '?' or '"' or '<' or '>' or '|' or ' ' ? '_' : c);
        string s = sb.ToString().Trim('_');
        return s.Length == 0 ? "key" : (s.Length > 80 ? s[^80..] : s);
    }

    private static string IdentityHash(string subKeyPath, string rawTarget)
    {
        byte[] bytes = SHA256.HashData(Encoding.UTF8.GetBytes(subKeyPath + "|" + rawTarget));
        return Convert.ToHexString(bytes, 0, 4).ToLowerInvariant();
    }

    private static string[] FitNameParts(string key, string target, string identity, string stamp, string suffix)
    {
        int fixedLength = stamp.Length + identity.Length + suffix.Length + ".reg".Length + 4;
        int budget = Math.Max(12, MaxBackupFileNameLength - fixedLength);
        int keyBudget = Math.Max(4, budget / 2);
        int targetBudget = Math.Max(4, budget - keyBudget);

        string fittedKey = Tail(key, keyBudget);
        string fittedTarget = Tail(target, targetBudget);
        int over = fixedLength + fittedKey.Length + fittedTarget.Length - MaxBackupFileNameLength;
        if (over > 0)
        {
            if (fittedKey.Length >= fittedTarget.Length)
                fittedKey = Tail(fittedKey, Math.Max(4, fittedKey.Length - over));
            else
                fittedTarget = Tail(fittedTarget, Math.Max(4, fittedTarget.Length - over));
        }

        return [fittedKey, fittedTarget];
    }

    private static string Tail(string value, int maxLength)
        => value.Length <= maxLength ? value : value[^maxLength..];

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
