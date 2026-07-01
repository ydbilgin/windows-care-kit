using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Execution.Adapters;

namespace WindowsCareKit.Tests.Execution;

/// <summary>Records dispatch order and can be made to throw, for executor tests. No real OS calls.</summary>
internal sealed class RecordingAdapters
{
    public List<string> Calls { get; } = new();

    /// <summary>The actual actions dispatched to an adapter, in order — for asserting WHICH target ran.</summary>
    public List<PlannedAction> Dispatched { get; } = new();

    /// <summary>Action ids that should throw when their adapter is invoked.</summary>
    public HashSet<string> ThrowForActionIds { get; } = new();

    /// <summary>When true, ANY adapter call throws (used to prove nothing is called on refusal).</summary>
    public bool ThrowOnAnyCall { get; set; }

    public IFileDeleteAdapter File => new FakeFile(this);
    public IRegistryAdapter Registry => new FakeRegistry(this);
    public IServiceAdapter Service => new FakeService(this);
    public ITaskAdapter Task => new FakeTask(this);
    public IProcessAdapter Process => new FakeProcess(this);
    public ICopyAdapter Copy => new FakeCopy(this);

    private void Hit(string kind, PlannedAction action)
    {
        if (ThrowOnAnyCall)
            throw new InvalidOperationException($"adapter '{kind}' must not be called");
        Calls.Add($"{kind}:{action.Id}");
        Dispatched.Add(action);
        if (ThrowForActionIds.Contains(action.Id))
            throw new InvalidOperationException($"boom:{action.Id}");
    }

    private sealed class FakeFile(RecordingAdapters o) : IFileDeleteAdapter
    {
        public void Delete(FileDeleteAction action) => o.Hit("file", action);
    }

    private sealed class FakeRegistry(RecordingAdapters o) : IRegistryAdapter
    {
        public void Delete(RegistryDeleteAction action) => o.Hit("registry", action);
    }

    private sealed class FakeService(RecordingAdapters o) : IServiceAdapter
    {
        public void Apply(ServiceDeleteAction action) => o.Hit("service", action);
    }

    private sealed class FakeTask(RecordingAdapters o) : ITaskAdapter
    {
        public void Apply(TaskDeleteAction action) => o.Hit("task", action);
    }

    private sealed class FakeProcess(RecordingAdapters o) : IProcessAdapter
    {
        public void Run(CommandAction action) => o.Hit("command", action);
    }

    private sealed class FakeCopy(RecordingAdapters o) : ICopyAdapter
    {
        public CopyAdapterResult Copy(CopyAction action)
        {
            o.Hit("copy", action);
            return new CopyAdapterResult(1, 1, Array.Empty<CopySkippedItem>());
        }

        public void Merge(RestoreMergeAction action) => o.Hit("merge", action);
    }
}
