using Xunit;

namespace WindowsCareKit.Tests.TestInfra;

/// <summary>
/// A <see cref="FactAttribute"/> for genuinely destructive tests. It STATICALLY skips the test unless the
/// host is an opted-in disposable machine (see <see cref="DisposableMachineGuard"/>). Static skip is used
/// deliberately: this project runs xUnit v2 (2.9.x), where runtime/dynamic skip (<c>SkipException</c>) is
/// NOT honored by the runner and would surface as a FAILURE. By computing <see cref="FactAttribute.Skip"/>
/// at discovery time, the test is reported as SKIPPED (never FAILED) on any normal machine, so the green
/// baseline count is preserved.
/// </summary>
/// <remarks>
/// Pair with <c>[Trait("Category", TestCategories.Destructive)]</c> so CI's <c>--filter "Category!=Destructive"</c>
/// also excludes it up front. On a real disposable machine the <see cref="FactAttribute.Skip"/> is left null and
/// the test actually runs.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
internal sealed class DisposableFactAttribute : FactAttribute
{
    public DisposableFactAttribute()
    {
        if (!DisposableMachineGuard.IsDisposableMachine)
            Skip = DisposableMachineGuard.SkipMessage;
    }
}
