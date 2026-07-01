using System.Text;
using WindowsCareKit.Core.Modules.Migration;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

public class EmbeddedSecretScannerTests
{
    private static EmbeddedSecretScanResult Scan(string text, string sourcePath = "settings.json")
        => EmbeddedSecretScanner.Scan(Encoding.UTF8.GetBytes(text), sourcePath);

    [Fact]
    public void Detects_openai_style_sk_prefix_token()
    {
        string token = "sk-" + new string('A', 24) + "1234567890";

        EmbeddedSecretScanResult result = Scan("apiKey = \"" + token + "\"");

        Assert.True(result.ContainsSecret);
        Assert.Contains("token prefix", result.Reason);
    }

    [Fact]
    public void Detects_github_ghp_prefix_token()
    {
        string token = "ghp_" + new string('B', 36);

        EmbeddedSecretScanResult result = Scan("token = \"" + token + "\"");

        Assert.True(result.ContainsSecret);
        Assert.Contains("token prefix", result.Reason);
    }

    [Fact]
    public void Detects_json_api_key_with_non_placeholder_value()
    {
        EmbeddedSecretScanResult result = Scan("{ \"openai_api_key\": \"synthetic-value-for-test\" }");

        Assert.True(result.ContainsSecret);
        Assert.Contains("key/value", result.Reason);
    }

    [Fact]
    public void Detects_private_key_header()
    {
        EmbeddedSecretScanResult result = Scan("-----BEGIN PRIVATE KEY-----\nsynthetic\n-----END PRIVATE KEY-----");

        Assert.True(result.ContainsSecret);
        Assert.Contains("private key", result.Reason);
    }

    [Theory]
    [InlineData("{ \"apiKey\": \"\" }")]
    [InlineData("{ \"apiKey\": \"<your-key>\" }")]
    [InlineData("api_key = \"changeme\"")]
    [InlineData("token = \"your-token\"")]
    [InlineData("client_secret = \"example-secret\"")]
    public void Ignores_obvious_placeholder_values(string text)
    {
        EmbeddedSecretScanResult result = Scan(text);

        Assert.False(result.ContainsSecret);
    }
}
