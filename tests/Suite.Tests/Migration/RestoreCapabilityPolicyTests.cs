using WindowsCareKit.Core.Modules.Migration;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

/// <summary>
/// Exercises <see cref="RestoreCapabilityPolicy.IsProfileRoot"/> against every member of
/// <see cref="KnownFolder"/>. Walking the enum means that adding a new member forces a
/// deliberate decision about its profile-root status rather than silently defaulting.
/// </summary>
public sealed class RestoreCapabilityPolicyTests
{
    private static readonly IReadOnlyDictionary<KnownFolder, bool> ExpectedProfileRootStatus = new Dictionary<KnownFolder, bool>
    {
        [KnownFolder.UserProfile] = true,
        [KnownFolder.AppData] = true,
        [KnownFolder.LocalAppData] = true,
        [KnownFolder.ProgramData] = false,
        [KnownFolder.ProgramFiles] = false,
        [KnownFolder.ProgramFilesX86] = false,
        [KnownFolder.WindowsEtc] = false,
    };

    [Fact]
    public void IsProfileRoot_covers_every_KnownFolder_member()
    {
        var enumValues = Enum.GetValues<KnownFolder>().ToHashSet();
        Assert.True(
            enumValues.SetEquals(ExpectedProfileRootStatus.Keys),
            "The KnownFolder enum and its expected profile-root classification map must contain exactly the same members.");

        foreach (KnownFolder folder in Enum.GetValues<KnownFolder>())
        {
            bool expected = ExpectedProfileRootStatus[folder];
            bool actual = RestoreCapabilityPolicy.IsProfileRoot(folder);

            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(KnownFolder.UserProfile, true)]
    [InlineData(KnownFolder.AppData, true)]
    [InlineData(KnownFolder.LocalAppData, true)]
    [InlineData(KnownFolder.ProgramData, false)]
    [InlineData(KnownFolder.ProgramFiles, false)]
    [InlineData(KnownFolder.ProgramFilesX86, false)]
    [InlineData(KnownFolder.WindowsEtc, false)]
    public void IsProfileRoot_returns_expected_value(KnownFolder folder, bool expected)
    {
        Assert.Equal(expected, RestoreCapabilityPolicy.IsProfileRoot(folder));
    }
}
