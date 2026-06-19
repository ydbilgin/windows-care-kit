namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// Thrown by <see cref="CopyAdapter"/> when a copy/merge is refused at the write boundary because the
/// destination (its leaf or an existing parent component) is a reparse point — a junction/symlink that a
/// same-user attacker could have swapped in after the gate authorized the destination, redirecting the write
/// into a protected/other tree (the write-side TOCTOU counterpart of the delete adapter's pre-op re-check —
/// spec §1.3/§3). It is a distinct type (not a bare <see cref="InvalidOperationException"/>) so the Backup
/// report can classify the skip reliably: the executor records <c>"{TypeName}: {Message}"</c>, and the
/// report matches on <see cref="TypeToken"/> instead of a brittle English substring of the message.
/// </summary>
public sealed class DestinationReparseException : InvalidOperationException
{
    /// <summary>The stable token both the thrower and the report classifier match on (the exception type name).</summary>
    public const string TypeToken = nameof(DestinationReparseException);

    public DestinationReparseException(string message) : base(message)
    {
    }
}
