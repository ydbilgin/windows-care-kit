using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// <see cref="Win32DriverGuard"/> reads the real (read-only) network device class node. These tests assert
/// its conservative, fail-closed contract; they do not require any specific hardware to be present.
/// </summary>
public class DriverGuardTests
{
    private static readonly Win32DriverGuard Guard = new();

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Blank_or_null_identifier_is_never_net(string? id)
        => Assert.False(Guard.IsNetClass(id!));

    [Fact]
    public void The_net_class_guid_itself_is_recognized_as_net()
        => Assert.True(Guard.IsNetClass(Win32DriverGuard.NetClassGuid));

    [Fact]
    public void An_obviously_non_existent_driver_is_not_net()
        => Assert.False(Guard.IsNetClass("Totally.Not.A.Real.Driver." + Guid.NewGuid().ToString("N")));

    [Fact]
    public void Fails_closed_for_a_non_network_winget_id()
    {
        // Steam is not a network driver; the guard must not green-light it.
        Assert.False(Guard.IsNetClass("Valve.Steam"));
    }
}
