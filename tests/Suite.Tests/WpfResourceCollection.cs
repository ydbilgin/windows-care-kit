using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Serializes every test class that loads WPF resource dictionaries or constructs real views.
/// <para>
/// WPF caches pack-URI <see cref="System.Windows.ResourceDictionary"/> instances process-wide, and those
/// caches are not thread-safe. xUnit runs test <em>collections</em> in parallel, so two such classes landing
/// on different threads could mutate the same cached dictionary and take the whole class down with
/// <c>InvalidOperationException: Operations that change non-concurrent collections must have exclusive
/// access</c>. That failure was observed intermittently on an untouched tree (independently reproduced by a
/// reviewer: 9 failures in one run, 0 in the next), so it is an infrastructure race, not a product defect.
/// </para>
/// <para>
/// Membership in one collection makes these classes run one after another. Each test class remains internally
/// sequential as usual; only the cross-class overlap is removed. Add any new test class that renders a view,
/// merges a theme dictionary, or otherwise touches WPF resources to this collection.
/// </para>
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class WpfResourceCollection
{
    public const string Name = "WPF resource dictionaries";
}
