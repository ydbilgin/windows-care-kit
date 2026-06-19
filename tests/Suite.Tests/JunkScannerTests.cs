using WindowsCareKit.Core.Modules.Clean;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

public class JunkScannerTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>A configurable <see cref="IJunkProbe"/> for the scanner tests (no real filesystem).</summary>
    private sealed class FakeJunkProbe : IJunkProbe
    {
        public List<JunkCandidate> Candidates { get; } = new();
        public IReadOnlyList<JunkCandidate> FindJunk() => Candidates;
    }

    [Fact]
    public void Junk_folders_become_low_risk_recycle_file_deletes()
    {
        var probe = new FakeJunkProbe();
        probe.Candidates.Add(new JunkCandidate(@"C:\Users\alice\AppData\Local\Temp", 2048, "User temp folder"));

        var result = new JunkScanner(probe, TestData.Gate()).Scan(T0);

        Assert.Equal("clean", result.Plan.ModuleName);
        var action = Assert.IsType<FileDeleteAction>(Assert.Single(result.Plan.Actions));
        Assert.True(action.ToRecycleBin);
        Assert.Equal(RiskLevel.Low, action.Risk);
        Assert.Equal(UndoCapability.Full, action.Undo);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Protected_candidates_are_skipped_not_planned()
    {
        var probe = new FakeJunkProbe();
        probe.Candidates.Add(new JunkCandidate(@"C:\Users\alice\AppData\Local\Temp", 0, "safe temp"));
        probe.Candidates.Add(new JunkCandidate(@"C:\Windows", 0, "looks risky"));          // Windows tree → blocked
        probe.Candidates.Add(new JunkCandidate(@"C:\Program Files", 0, "protected"));        // protected dir → blocked

        var result = new JunkScanner(probe, TestData.Gate()).Scan(T0);

        Assert.Single(result.Plan.Actions);
        Assert.Equal(2, result.Skipped.Count);
        Assert.All(result.Plan.Actions, a => Assert.True(TestData.Gate().Evaluate(a).Allowed));
    }

    [Fact]
    public void Empty_probe_yields_empty_plan()
    {
        var result = new JunkScanner(new FakeJunkProbe(), TestData.Gate()).Scan(T0);
        Assert.True(result.Plan.IsEmpty);
        Assert.Empty(result.Skipped);
    }

    [Fact]
    public void Blank_paths_are_dropped_before_gating()
    {
        var probe = new FakeJunkProbe();
        probe.Candidates.Add(new JunkCandidate("   ", 0, "blank"));
        probe.Candidates.Add(new JunkCandidate("", 0, "empty"));

        var result = new JunkScanner(probe, TestData.Gate()).Scan(T0);

        Assert.True(result.Plan.IsEmpty);
        Assert.Empty(result.Skipped);
    }

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(2048, "2 KB")]
    [InlineData(1572864, "1.5 MB")]
    public void FormatBytes_is_compact_and_human(long bytes, string expected)
        => Assert.Equal(expected, JunkScanner.FormatBytes(bytes));

    [Fact]
    public void Approx_size_is_woven_into_the_reason()
    {
        var probe = new FakeJunkProbe();
        probe.Candidates.Add(new JunkCandidate(@"C:\Users\alice\AppData\Local\Temp", 2048, "User temp folder"));

        var result = new JunkScanner(probe, TestData.Gate()).Scan(T0);

        var action = Assert.IsType<FileDeleteAction>(Assert.Single(result.Plan.Actions));
        Assert.Contains("2 KB", action.Reason);
    }
}
