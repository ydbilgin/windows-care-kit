using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.App.Deployment;

/// <summary>
/// Adapts the resolved application root onto the pure <see cref="PayloadRootPolicy"/>. This is the one
/// place where "the application directory" becomes "a forbidden payload root", which is why the rule
/// itself lives in Suite.Core and knows nothing about deployment.
/// </summary>
public static class AppPayloadRoots
{
    /// <summary>The policy implied by <paramref name="layout"/>: its resolved root is forbidden.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="layout"/> is null.</exception>
    /// <exception cref="InvalidOperationException">The layout is undetermined, so no directory can be
    /// named as forbidden. Refusing here is the point: a guard that cannot say what it protects must
    /// not pretend to protect anything.</exception>
    public static PayloadRootPolicy For(AppLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        return new PayloadRootPolicy([layout.Root]);
    }

    /// <summary>The policy for this process's frozen layout.</summary>
    public static PayloadRootPolicy ForCurrentProcess() => For(AppLayout.Current);
}
