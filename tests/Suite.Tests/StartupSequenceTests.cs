using System.IO;
using WindowsCareKit.App.Deployment;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.Startup;
using WindowsCareKit.Tests.Security; // RepoSource: the repository-source reader the other source guards use
using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// The startup ORDER, as a contract rather than a comment. WPF's <c>base.OnStartup</c> raises the public
/// <c>Application.Startup</c> event, so anything subscribed to it — in XAML or in a constructor — runs
/// wherever that call sits. If it sits before the layout gate, subscribed code (including code that builds a
/// module catalog, which loads assemblies from the app root) executes against a root nothing has verified.
/// <para>
/// These run headlessly: the sequence drives an <see cref="IStartupSequenceHost"/>, and the recorder below
/// simply writes down what it was asked to do, in order.
/// </para>
/// </summary>
public sealed class StartupSequenceTests
{
    private const string AppDir = @"C:\Program Files\WindowsCareKit";
    private const string AppExe = @"C:\Program Files\WindowsCareKit\WindowsCareKit.exe";
    private const string LinkDir = @"C:\Users\u\AppData\Local\Microsoft\WinGet\Links";
    private const string LinkExe = @"C:\Users\u\AppData\Local\Microsoft\WinGet\Links\WindowsCareKit.exe";
    private static readonly string BaseTablePath = Path.Combine(AppDir, "lang", "en.json");

    private const string PublishStartupEvent = nameof(PublishStartupEvent);
    private const string StartShell = nameof(StartShell);
    private const string ReportVerification = nameof(ReportVerification);
    private const string ShowFatalError = nameof(ShowFatalError);

    private sealed class RecordingHost : IStartupSequenceHost
    {
        public List<string> Calls { get; } = [];

        public string? ReportLine { get; private set; }

        public string? FatalMessage { get; private set; }

        public int? ExitCode { get; private set; }

        public void ReportVerification(string reportLine)
        {
            Calls.Add(nameof(ReportVerification));
            ReportLine = reportLine;
        }

        public void ShowFatalError(string message)
        {
            Calls.Add(nameof(ShowFatalError));
            FatalMessage = message;
        }

        public void Shutdown(int exitCode)
        {
            Calls.Add(nameof(Shutdown));
            ExitCode = exitCode;
        }

        public void PublishStartupEvent() => Calls.Add(nameof(PublishStartupEvent));

        public Task StartShellAsync()
        {
            Calls.Add(StartShell);
            return Task.CompletedTask;
        }
    }

    private static StartupPreflightResult PassingPreflight() => StartupPreflight.Run(
        AppLayout.Create(AppExe, null, AppDir, (p, _) => p.Equals(BaseTablePath, StringComparison.OrdinalIgnoreCase)),
        _ => BaseTableFixture.JsonTitled());

    private static StartupPreflightResult FailingPreflight() => StartupPreflight.Run(
        AppLayout.Create(LinkExe, AppExe, LinkDir, (_, _) => true),
        _ => BaseTableFixture.JsonTitled());

    [Fact]
    public async Task A_verify_only_run_never_publishes_the_startup_event()
    {
        var host = new RecordingHost();

        await StartupSequence.RunAsync(verifyLayoutOnly: true, PassingPreflight(), host);

        Assert.Equal([ReportVerification, nameof(host.Shutdown)], host.Calls);
        Assert.DoesNotContain(PublishStartupEvent, host.Calls);
        Assert.DoesNotContain(StartShell, host.Calls);
        Assert.Equal(StartupPreflight.ExitOk, host.ExitCode);
        Assert.StartsWith("WCK-LAYOUT ", host.ReportLine!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_verify_only_run_of_a_BROKEN_install_still_never_publishes_the_startup_event()
    {
        var host = new RecordingHost();

        await StartupSequence.RunAsync(verifyLayoutOnly: true, FailingPreflight(), host);

        Assert.Equal([ReportVerification, nameof(host.Shutdown)], host.Calls);
        Assert.Equal(StartupPreflight.ExitLayoutUndetermined, host.ExitCode);
    }

    [Fact]
    public async Task A_failed_preflight_shows_the_fatal_dialog_and_never_publishes_the_startup_event()
    {
        var host = new RecordingHost();

        await StartupSequence.RunAsync(verifyLayoutOnly: false, FailingPreflight(), host);

        Assert.Equal([ShowFatalError, nameof(host.Shutdown)], host.Calls);
        Assert.DoesNotContain(PublishStartupEvent, host.Calls);
        Assert.DoesNotContain(StartShell, host.Calls);
        Assert.NotEqual(StartupPreflight.ExitOk, host.ExitCode);
        Assert.Contains("Windows Care Kit cannot start", host.FatalMessage!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_broken_base_string_table_is_fatal_before_the_startup_event_too()
    {
        // The numeric-value case specifically: it used to pass the gate, so the app started and then showed
        // raw keys. It must now stop at the dialog, with no startup event and no shell.
        StartupPreflightResult preflight = StartupPreflight.Run(
            AppLayout.Create(AppExe, null, AppDir, (p, _) => p.Equals(BaseTablePath, StringComparison.OrdinalIgnoreCase)),
            _ => /*lang=json,strict*/ """{ "app.title": 123 }""");
        var host = new RecordingHost();

        await StartupSequence.RunAsync(verifyLayoutOnly: false, preflight, host);

        Assert.Equal([ShowFatalError, nameof(host.Shutdown)], host.Calls);
        Assert.Equal(StartupPreflight.ExitResourcesUnusable, host.ExitCode);
    }

    [Fact]
    public async Task A_normal_start_publishes_the_startup_event_only_after_the_gate_and_before_the_shell()
    {
        var host = new RecordingHost();

        await StartupSequence.RunAsync(verifyLayoutOnly: false, PassingPreflight(), host);

        Assert.Equal([PublishStartupEvent, StartShell], host.Calls);
        Assert.Null(host.ExitCode); // nothing shut anything down
    }

    [Fact]
    public async Task The_shell_start_is_awaited_not_fired_and_forgotten()
    {
        // A fire-and-forget shell start would let OnStartup return before composition finished and would
        // swallow its exception; the sequence must still be running until the shell's task completes.
        var gate = new TaskCompletionSource();
        var host = new AwaitProbeHost(gate.Task);

        Task run = StartupSequence.RunAsync(verifyLayoutOnly: false, PassingPreflight(), host);

        Assert.False(run.IsCompleted);
        gate.SetResult();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Everything above proves the SEQUENCE's branch order against a fake host. This proves the real WPF
    /// override cannot be edited around them: WPF's <c>base.OnStartup</c> is what actually raises the public
    /// <c>Startup</c> event, so a routine "call the base override first" edit republishes it ahead of the gate
    /// — and leaves all six tests above green, because none of them can see the application class. That
    /// mutation was demonstrated; this is what goes red for it.
    /// <para>
    /// A live application-boundary test would be the stronger proof and is not available here: the two
    /// branches this protects end in a modal <c>MessageBox</c> (fatal) or a real <c>MainWindow</c> (success),
    /// and these tests may not put windows on the developer's machine. What is left is the same shape the
    /// BannedSymbols analyzer uses for destructive APIs — <c>base.OnStartup</c> is a banned call with exactly
    /// one sanctioned site — which is also the reason <c>App.PublishStartupEvent</c> is private, one line
    /// long, and reachable only through <see cref="IStartupSequenceHost"/>.
    /// </para>
    /// </summary>
    [Fact]
    public void The_only_call_to_base_OnStartup_is_the_one_the_sequence_reaches_through_its_host()
    {
        // With the parentheses: the prose in these files discusses base.OnStartup by name, and prose is not a
        // call. A doc comment that ever spells the call out with its argument fails this test rather than
        // slipping past it — a loud false alarm on a gate this size is the cheaper mistake.
        const string Call = "base.OnStartup(";
        const string SanctionedSite =
            "private void PublishStartupEvent(StartupEventArgs e) => base.OnStartup(e);";

        string srcRoot = RepoSource.PathFor("src");
        string[] callers = Directory
            .EnumerateFiles(srcRoot, "*.cs", SearchOption.AllDirectories)
            .Where(file => !Path.GetRelativePath(srcRoot, file)
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Any(segment => segment is "bin" or "obj"))
            .Where(file => File.ReadAllText(file).Contains(Call, StringComparison.Ordinal))
            .Select(file => Path.GetRelativePath(srcRoot, file))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal([Path.Combine("Suite.App.Wpf", "App.xaml.cs")], callers);

        string app = RepoSource.Read("src/Suite.App.Wpf/App.xaml.cs");
        Assert.Equal(1, app.Split(Call).Length - 1);
        Assert.Contains(SanctionedSite, app, StringComparison.Ordinal);
    }

    private sealed class AwaitProbeHost(Task shellStart) : IStartupSequenceHost
    {
        public void ReportVerification(string reportLine) => throw new InvalidOperationException("not this path");

        public void ShowFatalError(string message) => throw new InvalidOperationException("not this path");

        public void Shutdown(int exitCode) => throw new InvalidOperationException("not this path");

        public void PublishStartupEvent()
        {
        }

        public Task StartShellAsync() => shellStart;
    }
}
