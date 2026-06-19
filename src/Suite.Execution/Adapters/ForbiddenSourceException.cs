namespace WindowsCareKit.Execution.Adapters;

/// <summary>
/// Thrown by <see cref="CopyAdapter"/> when a copy is refused because its source is a protected/forbidden
/// secret store (a credential / cookie / session leaf, or a manifest <c>forbiddenSources</c> entry — spec §1.3).
/// It is a distinct type (not a bare <see cref="InvalidOperationException"/>) so the Backup report can classify
/// the skip reliably: the executor records <c>"{TypeName}: {Message}"</c>, and the report matches on
/// <see cref="TypeToken"/> instead of a brittle English substring of the message.
/// </summary>
public sealed class ForbiddenSourceException : InvalidOperationException
{
    /// <summary>The stable token both the thrower and the report classifier match on (the exception type name).</summary>
    public const string TypeToken = nameof(ForbiddenSourceException);

    public ForbiddenSourceException(string message) : base(message)
    {
    }
}
