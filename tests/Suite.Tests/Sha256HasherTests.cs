using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Real-IO test for the production hasher over a throwaway <see cref="TempWorkspace"/> under the temp path.
/// Verifies it reproduces the canonical SHA-256 of known synthetic content, as lowercase hex. No real user
/// files are touched.
/// </summary>
public class Sha256HasherTests
{
    [Fact]
    public void Computes_the_canonical_lowercase_hex_sha256_of_known_content()
    {
        using var ws = new TempWorkspace();
        // "hello" → canonical SHA-256 (verified independently).
        string file = ws.WriteFile("a.txt", "hello");

        string actual = new Sha256Hasher().ComputeFileSha256(file);

        Assert.Equal("2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824", actual);
    }

    [Fact]
    public void Computes_the_canonical_hash_of_a_second_known_payload()
    {
        using var ws = new TempWorkspace();
        string file = ws.WriteFile("nested/b.bin", "wck-known-content-v1");

        string actual = new Sha256Hasher().ComputeFileSha256(file);

        Assert.Equal("2d202cb9b0a7a34b24ad9b988f56c84f21a6618931615051afa3298fa3a4296c", actual);
    }

    [Fact]
    public void Hash_is_always_lowercase_hex_of_length_64()
    {
        using var ws = new TempWorkspace();
        string file = ws.WriteFile("c.txt", "Some Mixed Content 123");

        string actual = new Sha256Hasher().ComputeFileSha256(file);

        Assert.Equal(64, actual.Length);
        Assert.True(actual.All(ch => (ch >= '0' && ch <= '9') || (ch >= 'a' && ch <= 'f')),
            "hash must be lowercase hex");
    }
}
