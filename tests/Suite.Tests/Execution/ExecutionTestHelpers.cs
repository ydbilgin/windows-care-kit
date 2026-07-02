using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;

namespace WindowsCareKit.Tests.Execution;

/// <summary>Builds a <see cref="GatedExecutor"/> over recording fakes and a temp-file <see cref="ExecutionLog"/>.</summary>
internal sealed class ExecutorFixture : IDisposable
{
    public RecordingAdapters Adapters { get; } = new();
    public string LogPath { get; }
    public ExecutionLog Log { get; }
    public SafetyGate Gate { get; }
    public RecordingRecycleBinEmptier RecycleBinEmptier { get; }
    public GatedExecutor Executor { get; }

    public ExecutorFixture(SafetyGate? gate = null, RecordingRecycleBinEmptier? recycleBinEmptier = null)
    {
        LogPath = Path.Combine(Path.GetTempPath(), "wck-exec-" + Guid.NewGuid().ToString("N") + ".jsonl");
        Log = new ExecutionLog(LogPath, new LogRedactor(null, null));
        Gate = gate ?? TestData.Gate();
        RecycleBinEmptier = recycleBinEmptier ?? new RecordingRecycleBinEmptier();
        Executor = new GatedExecutor(
            Gate, Log,
            Adapters.File, Adapters.Registry, Adapters.Service,
            Adapters.Task, Adapters.Process, Adapters.Copy,
            recycleBinEmptier: RecycleBinEmptier);
    }

    /// <summary>The lines written to the execution log so far.</summary>
    public string[] LogLines()
        => File.Exists(LogPath) ? File.ReadAllLines(LogPath) : Array.Empty<string>();

    public void Dispose()
    {
        try { if (File.Exists(LogPath)) File.Delete(LogPath); }
        catch { /* best-effort cleanup */ }
    }
}
