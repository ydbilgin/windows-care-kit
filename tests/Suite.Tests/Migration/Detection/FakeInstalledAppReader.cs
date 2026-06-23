using WindowsCareKit.Core.Modules.Uninstall;

namespace WindowsCareKit.Tests.Migration.Detection;

/// <summary>
/// In-memory <see cref="IInstalledAppReader"/> for Detection unit tests. Returns whatever list
/// was supplied at construction. Optionally throws to test the crash-safe path.
/// </summary>
internal sealed class FakeInstalledAppReader : IInstalledAppReader
{
    private readonly IReadOnlyList<InstalledApp> _apps;
    private readonly bool _throws;

    public FakeInstalledAppReader(IReadOnlyList<InstalledApp> apps, bool throws = false)
    {
        _apps = apps;
        _throws = throws;
    }

    public IReadOnlyList<InstalledApp> ReadAll()
    {
        if (_throws)
            throw new UnauthorizedAccessException("Simulated registry access denial.");
        return _apps;
    }

    // ── Factory helpers ──────────────────────────────────────────────────────────────────────────

    public static FakeInstalledAppReader Empty() => new([]);

    public static FakeInstalledAppReader Throwing() => new([], throws: true);

    public static FakeInstalledAppReader With(params InstalledApp[] apps) => new(apps);
}
