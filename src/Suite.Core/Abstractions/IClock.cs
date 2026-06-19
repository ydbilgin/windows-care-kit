namespace WindowsCareKit.Core.Abstractions;

/// <summary>
/// The single source of "now" for the domain, so plan timestamps and report dates are testable and
/// deterministic. Always UTC — callers format to local time only at the UI/report edge.
/// </summary>
public interface IClock
{
    /// <summary>The current instant in UTC.</summary>
    DateTime UtcNow { get; }
}

/// <summary>The production clock: delegates to <see cref="DateTime.UtcNow"/>.</summary>
public sealed class SystemClock : IClock
{
    public DateTime UtcNow => DateTime.UtcNow;
}
