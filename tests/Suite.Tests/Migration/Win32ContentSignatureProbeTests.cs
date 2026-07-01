using System.Text;
using Microsoft.Extensions.DependencyInjection;
using WindowsCareKit.Core.Modules.Migration;
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
        Assert.False(signature.HasSqliteHeader);
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
    public void Directory_sampler_returns_clean_directory_signature()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "theme=dark");
        Directory.CreateDirectory(Path.Combine(_root, "Cache"));
        File.WriteAllText(Path.Combine(_root, "Cache", "ignored.txt"), "S-1-5-21-111-222-333-444");
        File.WriteAllText(Path.Combine(_root, "id_rsa"), "secret leaf is skipped by name");

        var signature = new Win32ContentSignatureProbe(maxBytes: 64).ProbeFile(_root);

        Assert.True(signature.IsDirectorySignature);
        Assert.Equal(1, signature.DirectoryFilesSampled);
        Assert.Equal(1, signature.DirectoryFilesTotalSeen);
        Assert.False(signature.DirectoryEnumerationTruncated);
        Assert.Equal(["a.txt"], signature.DirectorySampledFiles);
        Assert.False(signature.HasMachineBoundContent);
    }

    [Fact]
    public void Directory_sampler_marks_clean_large_directory_as_truncated()
    {
        for (int i = 0; i < Win32ContentSignatureProbe.DefaultDirectorySampleFileCount + 1; i++)
            File.WriteAllText(Path.Combine(_root, $"{i:D2}.txt"), "theme=dark");

        var signature = new Win32ContentSignatureProbe(maxBytes: 64).ProbeFile(_root);

        Assert.True(signature.IsDirectorySignature);
        Assert.Equal(Win32ContentSignatureProbe.DefaultDirectorySampleFileCount, signature.DirectoryFilesSampled);
        Assert.Equal(Win32ContentSignatureProbe.DefaultDirectorySampleFileCount + 1, signature.DirectoryFilesTotalSeen);
        Assert.True(signature.DirectoryEnumerationTruncated);
        Assert.False(signature.HasMachineBoundContent);
        Assert.True(signature.BlocksPortabilityClaim);
    }

    [Fact]
    public void Directory_sampler_does_not_count_excluded_secret_or_cache_files_as_uncovered()
    {
        File.WriteAllText(Path.Combine(_root, "a.txt"), "theme=dark");
        File.WriteAllText(Path.Combine(_root, "logins.json"), "secret leaf is deliberately excluded");
        Directory.CreateDirectory(Path.Combine(_root, "Cache"));
        File.WriteAllText(Path.Combine(_root, "Cache", "state.txt"), "cache is deliberately excluded");

        var signature = new Win32ContentSignatureProbe(maxBytes: 64).ProbeFile(_root);

        Assert.Equal(1, signature.DirectoryFilesSampled);
        Assert.Equal(1, signature.DirectoryFilesTotalSeen);
        Assert.False(signature.DirectoryEnumerationTruncated);
        Assert.False(signature.BlocksPortabilityClaim);
    }

    [Fact]
    public void Directory_sampler_detects_machine_bound_sampled_file_deterministically()
    {
        string sub = Path.Combine(_root, "sub");
        Directory.CreateDirectory(sub);
        File.WriteAllText(Path.Combine(sub, "z.txt"), "owner=S-1-5-21-111111111-222222222-333333333-1001");
        File.WriteAllText(Path.Combine(_root, "a.txt"), "theme=dark");

        var probe = new Win32ContentSignatureProbe(maxBytes: 256, directorySampleFileCount: 2);
        var first = probe.ProbeFile(_root);
        var second = probe.ProbeFile(_root);

        Assert.True(first.IsDirectorySignature);
        Assert.True(first.HasMachineBoundContent);
        Assert.Equal(first.DirectorySampledFiles, second.DirectorySampledFiles);
        Assert.Equal(["a.txt", "sub/z.txt"], first.DirectorySampledFiles);
    }

    [Fact]
    public void Locked_file_returns_locked_now_without_machine_bound_evidence()
    {
        string path = Path.Combine(_root, "locked.txt");
        File.WriteAllText(path, "theme=dark");
        using var hold = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var signature = new Win32ContentSignatureProbe().ProbeFile(path);

        Assert.Equal(ContentProbeStatus.LockedNow, signature.Status);
        Assert.False(signature.HasMachineBoundContent);
        Assert.True(signature.BlocksPortabilityClaim);
    }

    [Fact]
    public void Di_resolved_probe_loads_default_profile_roots()
    {
        var services = new ServiceCollection();
        services.AddSingleton<IContentSignatureProbe, Win32ContentSignatureProbe>();
        using ServiceProvider provider = services.BuildServiceProvider();

        var probe = Assert.IsType<Win32ContentSignatureProbe>(
            provider.GetRequiredService<IContentSignatureProbe>());
        var field = typeof(Win32ContentSignatureProbe).GetField(
            "_defaultOptions",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);

        Assert.NotNull(field);
        var options = Assert.IsType<ContentSignatureOptions>(field!.GetValue(probe));
        Assert.NotEmpty(options.ProfileRoots);
    }

    [Fact]
    public void Offline_placeholder_returns_cloud_placeholder_before_read()
    {
        string path = Path.Combine(_root, "cloud.txt");
        File.WriteAllText(path, "theme=dark");
        File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Offline);

        try
        {
            var signature = new Win32ContentSignatureProbe().ProbeFile(path);

            Assert.Equal(ContentProbeStatus.CloudPlaceholder, signature.Status);
            Assert.False(signature.HasMachineBoundContent);
            Assert.True(signature.BlocksPortabilityClaim);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
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
