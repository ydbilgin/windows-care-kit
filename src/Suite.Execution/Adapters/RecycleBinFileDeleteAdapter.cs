using Microsoft.VisualBasic.FileIO;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// Deletes files and directories to the Recycle Bin via <c>Microsoft.VisualBasic.FileIO.FileSystem</c>
/// (spec §3: deletes are recoverable by default). A permanent delete is used ONLY when the action
/// explicitly opts out of the recycle bin AND declares no undo capability — no module does that in
/// PR2–PR6, so the recycle path is effectively always taken. This is the only file-delete surface in
/// the suite; it lives in the sanctioned <c>Suite.Execution</c> layer, where the banned-API analyzer is
/// enforced but these specific delete calls are explicitly allowed via narrow <c>#pragma warning disable RS0030</c>.
/// </summary>
public sealed class RecycleBinFileDeleteAdapter : IFileDeleteAdapter
{
    /// <inheritdoc />
    public void Delete(FileDeleteAction action)
    {
        ArgumentNullException.ThrowIfNull(action);

        string path = action.Path;

        // Immediately before deleting, refuse a reparse point (junction/symlink). The gate canonicalized the
        // path earlier; a same-user attacker could race a folder→junction swap between then and now, and
        // deleting through a link could unlink a protected target. No legitimate FileDeleteAction targets a
        // link, so this fails closed (TOCTOU re-check at the destructive boundary, spec §3/§10).
        try
        {
            if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                throw new InvalidOperationException($"Refusing to delete a reparse point (junction/symlink): {path}");
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            // fall through to the not-found handling below
        }

        string callPath = ToExtendedLengthPath(path);

        bool toRecycle = action.ToRecycleBin || action.Undo != UndoCapability.None;
        RecycleOption recycle = toRecycle
            ? RecycleOption.SendToRecycleBin
            : RecycleOption.DeletePermanently;

        // Directory.Exists / File.Exists are read-only checks (not banned). Decide file vs. dir.
        if (Directory.Exists(path))
        {
#pragma warning disable RS0030 // Sanctioned destructive sink (Suite.Execution): recycle-bin directory delete.
            FileSystem.DeleteDirectory(callPath, UIOption.OnlyErrorDialogs, recycle);
#pragma warning restore RS0030
            return;
        }

        if (File.Exists(path))
        {
#pragma warning disable RS0030 // Sanctioned destructive sink (Suite.Execution): recycle-bin file delete.
            FileSystem.DeleteFile(callPath, UIOption.OnlyErrorDialogs, recycle, UICancelOption.ThrowException);
#pragma warning restore RS0030
            return;
        }

        // Nothing to delete: surface it so the executor records a Failed result (or, for best-effort
        // junk cleanup, the executor's Low+Full carve-out tolerates it — §A.4).
        throw new FileNotFoundException($"Delete target does not exist: {path}", path);
    }

    /// <summary>
    /// The app manifest is <c>longPathAware</c>; for paths at/over the legacy MAX_PATH limit we hand the
    /// VB API the extended-length form so a long path does not fail. Already-extended and UNC-extended
    /// forms are left as-is.
    /// </summary>
    internal static string ToExtendedLengthPath(string path)
    {
        if (string.IsNullOrEmpty(path) || path.Length < 260)
            return path;
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
            return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal))
            return @"\\?\UNC\" + path.Substring(2);
        return @"\\?\" + path;
    }
}
