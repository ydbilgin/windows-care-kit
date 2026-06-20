using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Execution.Adapters;
using WindowsCareKit.Win32;

namespace WindowsCareKit.Tests.TestInfra;

/// <summary>
/// Builds a <see cref="GatedExecutor"/> over the PRODUCTION safety gate
/// (<see cref="SafetyGate"/> with <see cref="ProtectedResources.ForCurrentSystem"/> +
/// <see cref="Win32PathCanonicalizer"/>) and the REAL destructive adapters, plus a temp-file
/// <see cref="ExecutionLog"/> and a temp regbak directory. It mirrors the SHAPE of
/// <c>ExecutionTestHelpers.ExecutorFixture</c> but wires real sinks instead of recording fakes, so a test
/// drives the entire authorize → re-gate → dispatch → real-side-effect pipeline (Step 4 Tier A).
///
/// <para>Everything it creates is host-safe: a GUID-named <see cref="TempWorkspace"/> for the action
/// targets, a temp-file execution log, and a temp regbak dir. <see cref="Dispose"/> cleans all three.
/// The payload root sits under <see cref="Path.GetTempPath"/>, which lives under the current user's
/// profile — the hardened write-target gate allows it (same blast radius as the existing host-safe
/// tests). The spec permits pinning under <c>%LocalAppData%</c> if <c>%TEMP%</c> ever canonicalizes
/// oddly; %TEMP% already resolves there on a normal box, so no override is needed.</para>
/// </summary>
internal sealed class RealExecutorFixture : IDisposable
{
    /// <summary>A throwaway working directory for action targets (under <see cref="Path.GetTempPath"/>).</summary>
    public TempWorkspace Workspace { get; }

    /// <summary>The temp directory the real <see cref="RegistryDeleteAdapter"/> writes its <c>.reg</c> backups into.</summary>
    public string RegBackupDir { get; }

    /// <summary>The append-only execution-log file backing <see cref="LogLines"/>.</summary>
    public string LogPath { get; }

    public ExecutionLog Log { get; }

    /// <summary>The real production gate: ForCurrentSystem policy + the real Win32 path canonicalizer.</summary>
    public SafetyGate Gate { get; }

    /// <summary>The real executor over real adapters — the single destructive entry point under test.</summary>
    public GatedExecutor Executor { get; }

    public RealExecutorFixture()
    {
        Workspace = new TempWorkspace("wck-real-exec-");

        RegBackupDir = Path.Combine(Workspace.Root, "regbak");
        Directory.CreateDirectory(RegBackupDir);

        LogPath = Path.Combine(Workspace.Root, "exec-" + Guid.NewGuid().ToString("N") + ".jsonl");
        Log = new ExecutionLog(LogPath, new LogRedactor(null, null));

        // The PRODUCTION gate: real policy table + real path canonicalizer (no fakes).
        Gate = new SafetyGate(ProtectedResources.ForCurrentSystem(), new Win32PathCanonicalizer());

        Executor = new GatedExecutor(
            Gate,
            Log,
            new RecycleBinFileDeleteAdapter(),
            new RegistryDeleteAdapter(RegBackupDir),
            new ServiceControlAdapter(),
            new ScheduledTaskAdapter(),
            new ProcessAdapter(),
            new CopyAdapter());
    }

    /// <summary>The lines written to the execution log so far.</summary>
    public string[] LogLines()
        => File.Exists(LogPath) ? File.ReadAllLines(LogPath) : Array.Empty<string>();

    public void Dispose()
    {
        // Workspace teardown removes the log + regbak too (both live under its root); best-effort.
        Workspace.Dispose();
    }
}
