namespace WindowsCareKit.Core.Safety;

/// <summary>
/// How dangerous a planned action is. Surfaced in the dry-run UI (risk-colored) so the
/// user sees the weight of each step before approving (spec §3, §5).
/// </summary>
public enum RiskLevel
{
    Info = 0,
    Low = 1,
    Medium = 2,
    High = 3,
    Critical = 4,
}
