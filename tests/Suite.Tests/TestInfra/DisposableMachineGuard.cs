namespace WindowsCareKit.Tests.TestInfra;

/// <summary>
/// Host-safety gate for genuinely destructive tests: they may run only on an explicitly opted-in,
/// disposable machine (a throwaway VM/sandbox). The primary enforcement is the static
/// <see cref="DisposableFactAttribute"/>, which marks such tests as SKIPPED (never FAILED) on any normal
/// machine at discovery time — important because this project runs xUnit v2 (2.9.x), where runtime/dynamic
/// skip is NOT honored and would surface as a failure.
/// </summary>
/// <remarks>
/// Opt-in requires BOTH signals, which a normal dev box never has:
///   1. environment variable <c>WCK_DISPOSABLE_MACHINE == "1"</c>, AND
///   2. a marker file at <c>%TEMP%\wck-disposable.marker</c>.
/// </remarks>
internal static class DisposableMachineGuard
{
    /// <summary>Environment variable that must equal "1" on a disposable machine.</summary>
    public const string EnvVarName = "WCK_DISPOSABLE_MACHINE";

    /// <summary>Marker file name placed under <see cref="Path.GetTempPath"/> on a disposable machine.</summary>
    public const string MarkerFileName = "wck-disposable.marker";

    /// <summary>The reason text reported when a destructive test is skipped on a non-disposable machine.</summary>
    public const string SkipMessage =
        "Destructive test: requires a disposable machine (" + EnvVarName + "=1 and the %TEMP%\\" + MarkerFileName + " marker).";

    /// <summary>The full marker path under the per-user temp directory.</summary>
    public static string MarkerPath => Path.Combine(Path.GetTempPath(), MarkerFileName);

    /// <summary>True only when BOTH the env var is "1" AND the marker file exists.</summary>
    public static bool IsDisposableMachine
        => string.Equals(Environment.GetEnvironmentVariable(EnvVarName), "1", StringComparison.Ordinal)
           && File.Exists(MarkerPath);

    /// <summary>
    /// Defense-in-depth hard guard. A destructive test using <see cref="DisposableFactAttribute"/> is already
    /// statically skipped on a non-disposable host, so this is never reached there; if a destructive sink is
    /// ever reached without the disposable opt-in it throws (fail-closed) rather than running the destructive
    /// body. Returns normally only on a disposable machine.
    /// </summary>
    public static void RequireDisposableOrSkip()
    {
        if (!IsDisposableMachine)
            throw new InvalidOperationException(
                "Refusing to run a destructive test off a disposable machine. " + SkipMessage +
                " (Use [DisposableFact] so it is SKIPPED, not run, on normal hosts.)");
    }
}
