namespace WindowsCareKit.App.Startup;

/// <summary>
/// The shell operations <see cref="StartupSequence"/> drives, in the order it may drive them. The WPF
/// application implements this; a test implements it to record the order, which is the contract under test.
/// </summary>
internal interface IStartupSequenceHost
{
    /// <summary>Write the one-line machine-readable layout report (<c>--verify-layout</c>).</summary>
    void ReportVerification(string reportLine);

    /// <summary>Show the hardcoded-English fatal dialog.</summary>
    void ShowFatalError(string message);

    /// <summary>End the process with this exit code.</summary>
    void Shutdown(int exitCode);

    /// <summary>Raise WPF's public <c>Application.Startup</c> event (<c>base.OnStartup</c>).</summary>
    void PublishStartupEvent();

    /// <summary>Compose services, load strings, show the main window.</summary>
    Task StartShellAsync();
}

/// <summary>
/// The single owner of "in what order does this shell start", extracted from the WPF override so the ordering
/// itself is provable without launching an application.
/// <para>
/// The load-bearing rule: <see cref="IStartupSequenceHost.PublishStartupEvent"/> is reached only after the
/// preflight verdict is in hand and only on the branch where it passed. WPF's <c>base.OnStartup</c> raises the
/// public <c>Startup</c> event, which any XAML or constructor subscription can hook — running it first would
/// let subscribed code (including code that constructs a module catalog from the app root) execute before the
/// root was ever verified. Because <c>base.OnStartup</c> is reachable only through that host method, the
/// ordering cannot be undone by editing the application class alone.
/// </para>
/// </summary>
internal static class StartupSequence
{
    /// <summary>
    /// Runs the gate and then, only if it passed, the shell. <paramref name="preflight"/> is a value, so both
    /// flag parsing and the preflight run are complete before anything here — and therefore before any startup
    /// event — can happen.
    /// </summary>
    public static async Task RunAsync(
        bool verifyLayoutOnly, StartupPreflightResult preflight, IStartupSequenceHost host)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(host);

        if (verifyLayoutOnly)
        {
            // Verification is a report about the artifact, not a launch: it must not publish a startup event,
            // show a window, or compose services, because module loading executes third-party code.
            host.ReportVerification(preflight.ToReportLine());
            host.Shutdown(preflight.ExitCode);
            return;
        }

        if (!preflight.IsOk)
        {
            host.ShowFatalError(preflight.ToFatalMessage());
            host.Shutdown(preflight.ExitCode);
            return;
        }

        host.PublishStartupEvent();
        await host.StartShellAsync();
    }
}
