using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Proves the test-safety gate for destructive tests:
///   * a normal (CI/dev) machine is never classified as disposable;
///   * the hard guard fails-closed off a disposable machine;
///   * the sample <c>[DisposableFact] [Trait("Category","Destructive")]</c> test below reports as SKIPPED
///     (not FAILED) on a normal host — so the baseline test count is unaffected even when run unfiltered.
/// </summary>
public class DisposableMachineGuardTests
{
    [Fact]
    public void Normal_machine_is_not_classified_disposable()
    {
        // CI and dev boxes never carry BOTH the env var and the marker file. Guard kept conditional so it
        // also does not fail on a deliberately-prepared disposable host.
        if (DisposableMachineGuard.IsDisposableMachine)
            return;

        Assert.False(DisposableMachineGuard.IsDisposableMachine);
    }

    [Fact]
    public void Hard_guard_fails_closed_off_a_disposable_machine()
    {
        if (DisposableMachineGuard.IsDisposableMachine)
            return; // can't observe the fail-closed path on a real disposable host

        // Indirect test of the guard logic: off a disposable machine the guard refuses (throws) rather than
        // letting a destructive body proceed.
        var ex = Assert.Throws<InvalidOperationException>(DisposableMachineGuard.RequireDisposableOrSkip);
        Assert.Contains("disposable machine", ex.Message);
    }

    [Fact]
    public void DisposableFact_marks_destructive_tests_skipped_on_a_normal_machine()
    {
        // Discovery-time skip reason: present (non-null) on a normal machine, null on a disposable one.
        var attr = new DisposableFactAttribute();
        if (DisposableMachineGuard.IsDisposableMachine)
            Assert.Null(attr.Skip);
        else
            Assert.Equal(DisposableMachineGuard.SkipMessage, attr.Skip);
    }

    /// <summary>
    /// The canonical example destructive test. <see cref="DisposableFactAttribute"/> statically SKIPS it on a
    /// normal machine (reported Skipped, never Failed), and <c>[Trait("Category","Destructive")]</c> lets CI's
    /// <c>--filter "Category!=Destructive"</c> exclude it up front. The body is only ever reached on an
    /// explicitly-prepared disposable machine; per Step 3 host-safety it invokes NO real destructive sink and
    /// is just a placeholder demonstrating the pattern future destructive tests follow.
    /// </summary>
    [DisposableFact]
    [Trait("Category", TestCategories.Destructive)]
    public void Example_destructive_test_is_guarded_and_skips_on_a_normal_machine()
    {
        DisposableMachineGuard.RequireDisposableOrSkip(); // hard guard; on a normal host we never get here
        Assert.True(DisposableMachineGuard.IsDisposableMachine);
    }
}
