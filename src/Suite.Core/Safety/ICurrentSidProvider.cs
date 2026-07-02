namespace WindowsCareKit.Core.Safety;

/// <summary>Supplies the current Windows user's SID for HKU scoping decisions.</summary>
public interface ICurrentSidProvider
{
    /// <summary>Returns the current user's SID, or null when it cannot be resolved.</summary>
    string? GetCurrentSid();
}
