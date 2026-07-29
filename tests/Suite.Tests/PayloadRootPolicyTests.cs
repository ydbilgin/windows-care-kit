using System.Reflection;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

public sealed class PayloadRootPolicyTests
{
    [Fact]
    public void Constructor_canonicalizes_deduplicates_and_snapshots_roots_in_first_seen_order()
    {
        var supplied = new List<string> { @"C:\App\", @"c:\app", @"D:\Payloads\" };

        var policy = new PayloadRootPolicy(supplied);
        supplied[0] = @"E:\changed-after-construction";

        Assert.Equal(new[] { @"C:\App", @"D:\Payloads" }, policy.ForbiddenRoots);
    }

    [Fact]
    public void Constructor_refuses_a_null_sequence()
    {
        Assert.Throws<ArgumentNullException>(() => new PayloadRootPolicy(null!));
    }

    public static TheoryData<string[]> UnusableRoots => new()
    {
        new string[] { null! },
        new string[] { string.Empty },
        new string[] { "   " },
        new string[] { @"relative\path" },
        new string[] { @"C:\good", "   " },
    };

    [Theory]
    [MemberData(nameof(UnusableRoots))]
    public void Constructor_refuses_an_unusable_root(string[] forbiddenRoots)
    {
        Assert.Throws<ArgumentException>(() => new PayloadRootPolicy(forbiddenRoots));
    }

    [Fact]
    public void Constructor_refuses_an_empty_forbidden_set()
    {
        Assert.Throws<ArgumentException>(() => new PayloadRootPolicy([]));
    }

    [Fact]
    public void An_allowed_root_is_returned_canonicalized()
    {
        var policy = new PayloadRootPolicy([@"C:\app"]);

        PayloadRootVerdict verdict = policy.Evaluate(@"D:\backups\payload\");

        Assert.True(verdict.IsAllowed);
        Assert.Equal(@"D:\backups\payload", verdict.NormalizedRoot);
        Assert.Equal(string.Empty, verdict.MatchedForbiddenRoot);
    }

    [Fact]
    public void Every_forbidden_root_is_enforced()
    {
        var policy = new PayloadRootPolicy([@"C:\a", @"C:\b"]);

        PayloadRootVerdict second = policy.Evaluate(@"C:\b\payload");
        PayloadRootVerdict first = policy.Evaluate(@"C:\a");

        Assert.False(second.IsAllowed);
        Assert.Equal(PayloadRootRejection.InsideForbiddenRoot, second.Rejection);
        Assert.Equal(@"C:\b", second.MatchedForbiddenRoot);
        Assert.False(first.IsAllowed);
        Assert.Equal(PayloadRootRejection.InsideForbiddenRoot, first.Rejection);
        Assert.Equal(@"C:\a", first.MatchedForbiddenRoot);
    }

    [Fact]
    public void A_nearby_prefix_is_not_inside_the_forbidden_root()
    {
        var policy = new PayloadRootPolicy([@"C:\app"]);

        Assert.True(policy.Evaluate(@"C:\application-data").IsAllowed);
    }

    [Fact]
    public void Rejections_are_ordered_and_distinct()
    {
        var policy = new PayloadRootPolicy([@"C:\app"]);

        Assert.Equal(PayloadRootRejection.NotProvided, policy.Evaluate(null).Rejection);
        Assert.Equal(PayloadRootRejection.NotProvided, policy.Evaluate(string.Empty).Rejection);
        Assert.Equal(PayloadRootRejection.NotProvided, policy.Evaluate("   ").Rejection);
        Assert.Equal(
            PayloadRootRejection.NotLocalDrivePath,
            policy.Evaluate(@"\\server\share\pkg").Rejection);
        Assert.Equal(
            PayloadRootRejection.Unparseable,
            policy.Evaluate("embedded\0null").Rejection);

        // Classification must precede containment too. A UNC path is non-local even if a caller supplied
        // that same UNC tree as a forbidden root; moving containment ahead of the drive check changes this.
        var policyWithUncRoot = new PayloadRootPolicy([@"\\server\share"]);
        Assert.Equal(
            PayloadRootRejection.NotLocalDrivePath,
            policyWithUncRoot.Evaluate(@"\\server\share\pkg").Rejection);
    }

    [Fact]
    public void A_drive_root_forbids_everything_beneath_it()
    {
        var policy = new PayloadRootPolicy([@"C:\"]);

        PayloadRootVerdict verdict = policy.Evaluate(@"C:\anything");

        Assert.False(verdict.IsAllowed);
        Assert.Equal(PayloadRootRejection.InsideForbiddenRoot, verdict.Rejection);
        Assert.Equal(@"C:\", verdict.MatchedForbiddenRoot);
    }

    [Fact]
    public void Only_the_policy_can_produce_a_verdict()
    {
        Assert.Empty(typeof(PayloadRootVerdict).GetConstructors());
        Assert.DoesNotContain(
            typeof(PayloadRootVerdict).GetMethods(BindingFlags.Public | BindingFlags.Static),
            member => member.ReturnType == typeof(PayloadRootVerdict));
    }
}
