namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// Thrown by <see cref="CopyAdapter"/> when a copy/merge is refused at the write boundary because the
/// destination (its leaf or an existing parent component) is a reparse point — a junction/symlink that a
/// same-user attacker could have swapped in after the gate authorized the destination, redirecting the write
/// into a protected/other tree (the write-side TOCTOU counterpart of the delete adapter's pre-op re-check —
/// spec §1.3/§3). It is a distinct type (not a bare <see cref="InvalidOperationException"/>) so
/// <c>GatedExecutor</c> can classify it once, by TYPE, into <c>ExecutionFailureCode.Forbidden</c> (NEW-06) —
/// the Backup report reads that typed code, never a substring of the exception message.
/// </summary>
public sealed class DestinationReparseException : InvalidOperationException
{
    public DestinationReparseException(string message) : base(message)
    {
    }
}
