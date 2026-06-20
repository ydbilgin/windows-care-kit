namespace WindowsCareKit.Core.Modules.Migration.Discovery;

/// <summary>
/// Thin seam for host-safe, read-only discovery of recipe-less app candidates. Parallels
/// <c>ILeftoverProbe</c> and <c>IJunkProbe</c> in shape: one method, read-only result, no side effects.
///
/// <para>Discovery writes nothing and never materializes a recipe. Approved candidates flow through
/// the existing strict-load → RecipeResolver sandbox → backup pipeline only after a separate,
/// user-approved action (a later PR).</para>
/// </summary>
public interface IAppDiscoveryProbe
{
    /// <summary>
    /// Discover recipe-less app candidates from the current user's profile roots.
    /// The returned list is safe-aggregate only (see <see cref="DiscoveredApp"/>).
    /// </summary>
    /// <param name="options">Tuning options (budget, depth, clock).</param>
    /// <param name="ct">Cancellation token. Cancelled apps are emitted with <see cref="DiscoveryScanStatus.Cancelled"/>.</param>
    IReadOnlyList<DiscoveredApp> Discover(DiscoveryScanOptions options, CancellationToken ct);
}
