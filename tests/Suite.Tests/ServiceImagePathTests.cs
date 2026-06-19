using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests;

public class ServiceImagePathTests
{
    [Theory]
    [InlineData("\"C:\\Program Files\\App\\svc.exe\" -k netsvcs", @"C:\Program Files\App\svc.exe")]
    [InlineData(@"C:\Program Files\App\svc.exe -run", @"C:\Program Files\App\svc.exe")]
    [InlineData(@"\??\C:\Program Files\App\svc.exe", @"C:\Program Files\App\svc.exe")]
    [InlineData(@"C:\Windows\System32\svchost.exe -k LocalService", @"C:\Windows\System32\svchost.exe")]
    public void Extracts_executable_path(string imagePath, string expected)
        => Assert.Equal(expected, Win32LeftoverProbe.ExtractExecutablePath(imagePath));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void Returns_null_for_blank(string? imagePath)
        => Assert.Null(Win32LeftoverProbe.ExtractExecutablePath(imagePath));

    [Fact]
    public void IsUnder_matches_on_segment_boundary_only()
    {
        Assert.True(Win32LeftoverProbe.IsUnder(@"C:\Program Files\ABC\svc.exe", @"C:\Program Files\ABC"));
        // C:\Program Files\AB must NOT match a service under C:\Program Files\ABC
        Assert.False(Win32LeftoverProbe.IsUnder(@"C:\Program Files\ABC\svc.exe", @"C:\Program Files\AB"));
    }
}
