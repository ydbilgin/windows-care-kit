namespace WindowsCareKit.Core.Modules.Backup;

/// <summary>
/// Thrown by <see cref="BackupIntegrityWriter"/> when a destination leaf cannot be expressed as a path that
/// stays UNDER the payload root — the relative result is itself rooted, or it escapes the root via <c>..</c>.
/// The integrity manifest must only ever record destination-relative paths that live inside the payload (it
/// must never leak an absolute/off-root path, nor a source path — locked decision #4). Rather than silently
/// writing such a path, the writer fails closed with this typed exception so the off-root/malformed-destination
/// invariant is enforced, not papered over.
/// </summary>
public sealed class OffRootDestinationException : InvalidOperationException
{
    public OffRootDestinationException(string message) : base(message)
    {
    }
}
