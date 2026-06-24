using System.Text;
using WindowsCareKit.Core.Modules.Migration;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

public sealed class ContentSignatureClassifierTests
{
    private static readonly byte[] SyntheticDpapiHeader =
    [
        0x01, 0x00, 0x00, 0x00,
        0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15, 0xD1, 0x11,
        0x8C, 0x7A, 0x00, 0xC0, 0x4F, 0xC2, 0x97, 0xEB,
    ];

    [Fact]
    public void Synthetic_dpapi_provider_header_is_machine_bound()
    {
        byte[] bytes = [0x7B, 0x00, .. SyntheticDpapiHeader, 0x00, 0x7D];

        ContentSignature signature = ContentSignatureClassifier.Classify(bytes);

        Assert.True(signature.HasDpapiBlob);
        Assert.True(signature.HasMachineBoundContent);
    }

    [Theory]
    [InlineData("owner=S-1-5-21-111111111-222222222-333333333-1001")]
    [InlineData("machineGuid={01234567-89ab-cdef-0123-456789abcdef}")]
    public void Text_bindings_are_recognized_from_synthetic_content(string text)
    {
        ContentSignature signature = ContentSignatureClassifier.Classify(Encoding.UTF8.GetBytes(text));

        Assert.True(signature.HasSidBinding || signature.HasMachineGuidBinding);
        Assert.True(signature.HasMachineBoundContent);
    }

    [Fact]
    public void Three_absolute_user_profile_literals_trigger_path_binding()
    {
        byte[] bytes = Encoding.UTF8.GetBytes(
            """{"a":"C:\Users\alice\one","b":"C:\Users\alice\two","c":"D:\Users\bob\three"}""");

        ContentSignature signature = ContentSignatureClassifier.Classify(bytes);

        Assert.True(signature.HasAbsolutePathBinding);
        Assert.True(signature.HasMachineBoundContent);
    }

    [Theory]
    [MemberData(nameof(CredentialStoreHeaders))]
    public void Credential_store_headers_are_conservatively_machine_bound(byte[] bytes)
    {
        ContentSignature signature = ContentSignatureClassifier.Classify(bytes);

        Assert.True(signature.HasCredentialStoreHeader);
        Assert.True(signature.HasMachineBoundContent);
    }

    public static TheoryData<byte[]> CredentialStoreHeaders => new()
    {
        Encoding.ASCII.GetBytes("SQLite format 3\0synthetic"),
        new byte[] { 0x01, 0x02, 0x57, 0xFB, 0x80, 0x8B, 0x24, 0x75, 0x47, 0xDB },
        Encoding.ASCII.GetBytes("MANIFEST-000007\n"),
    };

    [Fact]
    public void Benign_content_does_not_fabricate_a_machine_binding()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("""{"theme":"dark","fontSize":14}""");
        ContentSignature signature = ContentSignatureClassifier.Classify(bytes);

        Assert.False(signature.HasMachineBoundContent);
        Assert.Equal(bytes.Length, signature.BytesInspected);
    }

    [Fact]
    public void Stream_classifier_reads_no_more_than_the_requested_cap()
    {
        byte[] bytes = new byte[256];
        SyntheticDpapiHeader.CopyTo(bytes, 128);
        using var stream = new MemoryStream(bytes);

        ContentSignature signature = ContentSignatureClassifier.Classify(stream, maxBytes: 64);

        Assert.Equal(64, signature.BytesInspected);
        Assert.False(signature.HasDpapiBlob);
    }

    [Fact]
    public void Inconclusive_is_fail_closed()
    {
        ContentSignature signature = ContentSignature.Inconclusive();

        Assert.True(signature.IsInconclusive);
        Assert.True(signature.HasMachineBoundContent);
    }
}
