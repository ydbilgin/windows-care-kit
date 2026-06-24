using System.Text;
using WindowsCareKit.Tests.TestInfra;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

public sealed class Win32ContentSignatureProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wck-content-probe-" + Guid.NewGuid().ToString("N"));

    public Win32ContentSignatureProbeTests() => Directory.CreateDirectory(_root);

    [Fact]
    public void Probe_reads_only_the_configured_prefix()
    {
        string path = Path.Combine(_root, "synthetic.bin");
        byte[] bytes = new byte[256];
        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(bytes, 128);
        File.WriteAllBytes(path, bytes);

        var probe = new Win32ContentSignatureProbe(maxBytes: 64);
        var signature = probe.ProbeFile(path);

        Assert.Equal(64, signature.BytesInspected);
        Assert.False(signature.HasCredentialStoreHeader);
        Assert.False(signature.HasMachineBoundContent);
    }

    [Fact]
    public void Missing_file_is_inconclusive_and_machine_bound()
    {
        var signature = new Win32ContentSignatureProbe().ProbeFile(Path.Combine(_root, "missing.bin"));

        Assert.True(signature.IsInconclusive);
        Assert.True(signature.HasMachineBoundContent);
    }

    [Fact]
    public void Directory_is_not_opened_as_a_file_and_fails_closed()
    {
        var signature = new Win32ContentSignatureProbe().ProbeFile(_root);

        Assert.True(signature.IsInconclusive);
        Assert.True(signature.HasMachineBoundContent);
    }

    [FactRequiresSymlink]
    public void Probe_does_not_follow_a_reparse_parent()
    {
        string target = Path.Combine(_root, "target");
        string link = Path.Combine(_root, "link");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "store.db"), "SQLite format 3\0synthetic");
        Directory.CreateSymbolicLink(link, target);

        try
        {
            var signature = new Win32ContentSignatureProbe().ProbeFile(Path.Combine(link, "store.db"));

            Assert.True(signature.IsInconclusive);
            Assert.False(signature.HasCredentialStoreHeader);
            Assert.True(signature.HasMachineBoundContent);
        }
        finally
        {
            if (Directory.Exists(link))
                Directory.Delete(link);
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { }
    }
}
