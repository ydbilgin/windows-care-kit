using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Modules.Install;
using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

public sealed class SanctionedFileWriterTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "wck-sanctioned-writer-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void AtomicReplace_writes_and_leaves_no_wcktmp()
    {
        string path = Path.Combine(_root, "state", "checkpoint.json");
        var writer = new SanctionedFileWriter();

        writer.AtomicReplace(path, "first");
        writer.AtomicReplace(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
        Assert.Empty(Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.wcktmp"));
    }

    [Fact]
    public void WriteAllText_creates_parent_and_overwrites()
    {
        string path = Path.Combine(_root, "nested", "metadata.json");
        var writer = new SanctionedFileWriter();

        writer.WriteAllText(path, "first");
        writer.WriteAllText(path, "second");

        Assert.Equal("second", File.ReadAllText(path));
        Assert.False(File.ReadAllText(path).StartsWith('\uFEFF'));
    }

    [Fact]
    public void RestoreStateStore_Save_uses_AtomicReplace_once_with_checkpoint_path()
    {
        var writer = new RecordingFileWriter();
        var store = new RestoreStateStore(writer);

        store.Save(_root, RestoreState.Empty);

        Assert.Equal(1, writer.AtomicReplaceCount);
        Assert.Equal(store.PathFor(_root), writer.LastPath);
        Assert.Equal(0, writer.WriteAllTextCount);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                TestFs.DeleteResilient(_root);
        }
        catch
        {
            // Best-effort test cleanup.
        }
    }

    private sealed class RecordingFileWriter : IFileWriter
    {
        public int WriteAllTextCount { get; private set; }
        public int AtomicReplaceCount { get; private set; }
        public string? LastPath { get; private set; }

        public void WriteAllText(string path, string contents)
        {
            WriteAllTextCount++;
            LastPath = path;
        }

        public void AtomicReplace(string path, string contents)
        {
            AtomicReplaceCount++;
            LastPath = path;
        }
    }
}
