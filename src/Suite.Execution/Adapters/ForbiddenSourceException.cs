namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// Thrown by <see cref="CopyAdapter"/> when a copy is refused because its source is a protected/forbidden
/// secret store (a credential / cookie / session leaf, or a manifest <c>forbiddenSources</c> entry — spec §1.3).
/// It is a distinct type (not a bare <see cref="InvalidOperationException"/>) so <c>GatedExecutor</c> can
/// classify it once, by TYPE, into <c>ExecutionFailureCode.Forbidden</c> (NEW-06) — the Backup report reads
/// that typed code, never a substring of the exception message.
/// </summary>
public sealed class ForbiddenSourceException : InvalidOperationException
{
    public ForbiddenSourceException(string message) : base(message)
    {
    }
}
