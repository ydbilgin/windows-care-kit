using WindowsCareKit.Core.Modules.Migration;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

/// <summary>F3: secret-glob overlay blocks key material; forbidden-first — a recipe include can never override it.</summary>
public class MigrationSecretFilterTests
{
    private static readonly string[] FixedLeaves = { "Login Data", "Cookies", "key4.db" };

    [Theory]
    [InlineData("server.key")]
    [InlineData("client.pem")]
    [InlineData("id_rsa")]
    [InlineData("id_rsa.pub")]
    [InlineData("deploy.ppk")]
    [InlineData(".claude.json")]
    [InlineData("wallet.dat")]
    [InlineData("client.pfx")]
    [InlineData("id.kdbx")]
    [InlineData("github_token.txt")]
    [InlineData("app.secret")]
    [InlineData("aws_credentials")]
    public void Secret_glob_overlay_blocks_credential_leaves(string leaf)
        => Assert.True(SecretGlobOverlay.IsSecretLeaf(leaf), $"{leaf} should be a secret");

    [Theory]
    [InlineData("settings.json")]
    [InlineData("CLAUDE.md")]
    [InlineData("keybindings.json")] // 'key' substring but not a *.key / *token* / *secret* match
    public void Non_secret_leaves_are_allowed(string leaf)
    {
        Assert.False(SecretGlobOverlay.IsSecretLeaf(leaf));
        Assert.True(MigrationSecretFilter.IsLeafAllowed(leaf, FixedLeaves));
    }

    [Fact]
    public void Fixed_built_in_credential_leaves_are_blocked_first()
    {
        Assert.False(MigrationSecretFilter.IsLeafAllowed("Login Data", FixedLeaves));
        Assert.False(MigrationSecretFilter.IsLeafAllowed("Cookies", FixedLeaves));
        Assert.False(MigrationSecretFilter.IsLeafAllowed("key4.db", FixedLeaves));
    }

    [Fact]
    public void Secret_glob_overlay_is_blocked_regardless_of_any_include_intent()
    {
        // The filter is the authority that runs BEFORE include; a recipe wanting id_rsa cannot win.
        Assert.False(MigrationSecretFilter.IsLeafAllowed("id_rsa", FixedLeaves));
        Assert.False(MigrationSecretFilter.IsLeafAllowed("private.key", FixedLeaves));
    }
}
