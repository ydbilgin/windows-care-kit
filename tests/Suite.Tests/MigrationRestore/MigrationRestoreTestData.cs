using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Migration;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Execution.Adapters;
using WindowsCareKit.Win32;

namespace WindowsCareKit.Tests.MigrationRestore;

/// <summary>
/// Shared host-safe fixtures for the Slice 2 RESTORE tests: synthetic profile roots, a SafetyGate whose
/// CURRENT/target profile is the fabricated restore profile (so a legitimate restore into the new machine's
/// profile is ALLOWED, while anything outside it is blocked), and a real GatedExecutor wired to the real
/// CopyAdapter (so the atomic .bak-backed merge runs for real on temp files).
/// </summary>
internal static class MigrationRestoreTestData
{
    /// <summary>A temp scratch directory the caller must delete.</summary>
    public static string TempDir(string tag)
    {
        string d = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wck-restore-{tag}-{Guid.NewGuid():N}");
        System.IO.Directory.CreateDirectory(d);
        return d;
    }

    /// <summary>
    /// A SafetyGate whose write policy treats <paramref name="profileRoot"/> as the current/target user profile
    /// and <paramref name="usersRoot"/> as the Users root — mirroring how the freshly-installed machine's gate is
    /// configured. A restore into <paramref name="profileRoot"/> passes; anything else under Users is "another
    /// user's profile" and blocked.
    /// </summary>
    public static SafetyGate GateForProfile(string profileRoot, string usersRoot)
        => new(
            new ProtectedResources(
                protectedDirectories: new[] { @"C:\Windows", @"C:\Program Files", @"C:\ProgramData" },
                windowsDirectory: @"C:\Windows",
                protectedProcessNames: ProtectedResources.DefaultProtectedProcessNames,
                criticalServiceNames: ProtectedResources.DefaultCriticalServiceNames,
                protectedRegistryKeys: ProtectedResources.DefaultProtectedRegistryKeys,
                wholeSubtreeRegistryRoots: ProtectedResources.DefaultWholeSubtreeRegistryRoots,
                commandDenyList: ProtectedResources.DefaultCommandDenyList,
                writeProtectedRoots: new[] { @"C:\Windows", @"C:\Program Files", @"C:\ProgramData" },
                usersRoot: usersRoot,
                currentUserProfile: profileRoot),
            new Win32PathCanonicalizer());

    /// <summary>A real GatedExecutor: real gate + real CopyAdapter (only Copy/Merge are exercised here).</summary>
    public static GatedExecutor Executor(SafetyGate gate)
        => new(
            gate,
            new ExecutionLog(
                System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"wck-restore-log-{Guid.NewGuid():N}.jsonl"),
                new LogRedactor(null, null)),
            new ThrowingFileDeleteAdapter(),
            new ThrowingRegistryAdapter(),
            new ThrowingServiceAdapter(),
            new ThrowingTaskAdapter(),
            new ThrowingProcessAdapter(),
            new CopyAdapter());

    // Restore plans only ever dispatch CopyAction/RestoreMergeAction; the other sinks must never be reached.
    private sealed class ThrowingFileDeleteAdapter : IFileDeleteAdapter
    { public void Delete(Core.Planning.FileDeleteAction a) => throw new InvalidOperationException("file delete not expected in restore"); }
    private sealed class ThrowingRegistryAdapter : IRegistryAdapter
    { public void Delete(Core.Planning.RegistryDeleteAction a) => throw new InvalidOperationException("registry delete not expected in restore"); }
    private sealed class ThrowingServiceAdapter : IServiceAdapter
    { public void Apply(Core.Planning.ServiceDeleteAction a) => throw new InvalidOperationException("service op not expected in restore"); }
    private sealed class ThrowingTaskAdapter : ITaskAdapter
    { public void Apply(Core.Planning.TaskDeleteAction a) => throw new InvalidOperationException("task op not expected in restore"); }
    private sealed class ThrowingProcessAdapter : IProcessAdapter
    { public void Run(Core.Planning.CommandAction a) => throw new InvalidOperationException("process run not expected in restore"); }
}
