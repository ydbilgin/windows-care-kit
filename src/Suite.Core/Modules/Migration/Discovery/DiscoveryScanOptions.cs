namespace WindowsCareKit.Core.Modules.Migration.Discovery;

/// <summary>
/// Tuning knobs for one <see cref="IAppDiscoveryProbe.Discover"/> run.
///
/// <para><b>Determinism contract (F2):</b> <see cref="MaxGlobalEntries"/> is the PRIMARY cap that
/// decides <see cref="DiscoveryScanStatus.Complete"/> vs <see cref="DiscoveryScanStatus.IncompleteBudget"/>.
/// Wall-time (via <paramref name="nowUtc"/> and <see cref="System.Threading.CancellationToken"/>) is
/// only a safety backstop so results are reproducible in deterministic tests.</para>
/// </summary>
/// <param name="nowUtc">
/// The "now" reference used for lookback filtering. Injected for testability — pass
/// <c>DateTime.UtcNow</c> in production, or a fixed value in tests.
/// </param>
public sealed class DiscoveryScanOptions
{
    /// <summary>
    /// Creates a new options instance with the given clock reference and defaults for all other settings.
    /// </summary>
    /// <param name="nowUtc">The current time in UTC, used as the lookback anchor. Injected for testability.</param>
    public DiscoveryScanOptions(DateTime nowUtc)
    {
        NowUtc = nowUtc;
    }

    /// <summary>The "now" reference for lookback filtering (injectable for deterministic tests).</summary>
    public DateTime NowUtc { get; }

    /// <summary>
    /// Apps with no allowed file activity within the last <c>LookbackDays</c> days may be ranked lower
    /// by the UI. The engine itself does NOT suppress an incomplete scan based on this threshold.
    /// </summary>
    public int LookbackDays { get; init; } = 7;

    /// <summary>Maximum directory depth to walk below each candidate app root (1 = direct children only).</summary>
    public int MaxDepth { get; init; } = 4;

    /// <summary>
    /// Total file-system entries visited across ALL candidate apps before declaring
    /// <see cref="DiscoveryScanStatus.IncompleteBudget"/>. This is the primary cap for determinism —
    /// wall-time is only a backstop.
    /// </summary>
    public int MaxGlobalEntries { get; init; } = 25_000;
}
