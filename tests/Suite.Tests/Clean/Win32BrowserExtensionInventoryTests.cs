using System;
using System.IO;
using WindowsCareKit.Core.Modules.Clean;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests.Clean;

/// <summary>
/// NEW-07 MAJOR-01 fix: <see cref="Win32BrowserExtensionInventory"/> must not collapse an inaccessible
/// browser <c>User Data</c> root or profile <c>Extensions</c> folder into "not installed" / "no Extensions
/// folder" (honest-empty) the way a preflight <c>Directory.Exists</c> check would (Microsoft documents that
/// it returns false both for genuine absence AND when determining existence fails). These tests inject
/// synthetic enumeration outcomes through the test-only constructor seam, keyed on the real vendor paths
/// (real <c>Environment.SpecialFolder.LocalApplicationData</c>) so no real filesystem access ever occurs.
/// NEW-07 MINOR-01 fix: the emitted <see cref="InventorySourceFault.Source"/> must be a fixed descriptor,
/// never the actual captured profile directory name.
/// </summary>
public class Win32BrowserExtensionInventoryTests
{
    private static readonly string LocalAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static readonly string ChromeUserData = Path.Combine(LocalAppData, "Google", "Chrome", "User Data");
    private static readonly string EdgeUserData = Path.Combine(LocalAppData, "Microsoft", "Edge", "User Data");

    [Fact]
    public void DirectoryNotFoundException_at_every_browser_root_is_absent_not_a_fault()
    {
        var inventory = new Win32BrowserExtensionInventory(_ => throw new DirectoryNotFoundException()).ReadAll();

        Assert.Empty(inventory.Extensions);
        Assert.Empty(inventory.Faults);
        Assert.Equal(SourceHealth.Complete, inventory.Health);
    }

    [Fact]
    public void UnauthorizedAccessException_at_one_browser_root_becomes_Partial_and_preserves_the_healthy_sibling_browser()
    {
        string edgeDefault = Path.Combine(EdgeUserData, "Default");
        string edgeExtRoot = Path.Combine(edgeDefault, "Extensions");
        string edgeExtId = Path.Combine(edgeExtRoot, "ext1");

        var inventory = new Win32BrowserExtensionInventory(path =>
                path == ChromeUserData ? throw new UnauthorizedAccessException()
                : path == EdgeUserData ? new[] { edgeDefault }
                : path == edgeExtRoot ? new[] { edgeExtId }
                : throw new DirectoryNotFoundException())
            .ReadAll();

        Assert.Contains(inventory.Faults, f => f.Source == "Chrome" && f.Category == "UnauthorizedAccessException");
        Assert.Contains(inventory.Extensions, e => e.Browser == "Edge" && e.Profile == "Default" && e.Id == "ext1");
        Assert.Equal(SourceHealth.Partial, inventory.Health);
    }

    [Fact]
    public void General_IOException_at_one_browser_root_becomes_Partial_and_preserves_the_healthy_sibling_browser()
    {
        string edgeDefault = Path.Combine(EdgeUserData, "Default");
        string edgeExtRoot = Path.Combine(edgeDefault, "Extensions");
        string edgeExtId = Path.Combine(edgeExtRoot, "ext1");

        var inventory = new Win32BrowserExtensionInventory(path =>
                path == ChromeUserData ? throw new IOException("disk error")
                : path == EdgeUserData ? new[] { edgeDefault }
                : path == edgeExtRoot ? new[] { edgeExtId }
                : throw new DirectoryNotFoundException())
            .ReadAll();

        Assert.Contains(inventory.Faults, f => f.Source == "Chrome" && f.Category == "IOException");
        Assert.Contains(inventory.Extensions, e => e.Browser == "Edge" && e.Profile == "Default" && e.Id == "ext1");
        Assert.Equal(SourceHealth.Partial, inventory.Health);
    }

    [Fact]
    public void DirectoryNotFoundException_at_a_profile_Extensions_folder_is_absent_not_a_fault()
    {
        string chromeDefault = Path.Combine(ChromeUserData, "Default");

        var inventory = new Win32BrowserExtensionInventory(path =>
                path == ChromeUserData ? new[] { chromeDefault }
                : throw new DirectoryNotFoundException()) // covers the Default\Extensions probe and every other browser root
            .ReadAll();

        Assert.Empty(inventory.Extensions);
        Assert.Empty(inventory.Faults);
        Assert.Equal(SourceHealth.Complete, inventory.Health);
    }

    [Fact]
    public void UnauthorizedAccessException_at_one_profile_Extensions_folder_becomes_Partial_and_preserves_the_healthy_sibling_profile()
    {
        string chromeDefault = Path.Combine(ChromeUserData, "Default");
        string chromeDefaultExt = Path.Combine(chromeDefault, "Extensions");
        string chromeProfile1 = Path.Combine(ChromeUserData, "Profile 1");
        string chromeProfile1Ext = Path.Combine(chromeProfile1, "Extensions");
        string chromeProfile1ExtId = Path.Combine(chromeProfile1Ext, "abc123");

        var inventory = new Win32BrowserExtensionInventory(path =>
                path == ChromeUserData ? new[] { chromeDefault, chromeProfile1 }
                : path == chromeDefaultExt ? throw new UnauthorizedAccessException()
                : path == chromeProfile1Ext ? new[] { chromeProfile1ExtId }
                : throw new DirectoryNotFoundException())
            .ReadAll();

        Assert.Contains(inventory.Faults, f => f.Source == "Chrome/Default" && f.Category == "UnauthorizedAccessException");
        Assert.Contains(inventory.Extensions, e => e.Browser == "Chrome" && e.Profile == "Profile 1" && e.Id == "abc123");
        Assert.Equal(SourceHealth.Partial, inventory.Health);
    }

    [Fact]
    public void General_IOException_at_one_profile_Extensions_folder_becomes_Partial_and_preserves_the_healthy_sibling_profile()
    {
        string chromeDefault = Path.Combine(ChromeUserData, "Default");
        string chromeDefaultExt = Path.Combine(chromeDefault, "Extensions");
        string chromeProfile1 = Path.Combine(ChromeUserData, "Profile 1");
        string chromeProfile1Ext = Path.Combine(chromeProfile1, "Extensions");
        string chromeProfile1ExtId = Path.Combine(chromeProfile1Ext, "abc123");

        var inventory = new Win32BrowserExtensionInventory(path =>
                path == ChromeUserData ? new[] { chromeDefault, chromeProfile1 }
                : path == chromeDefaultExt ? throw new IOException("disk error")
                : path == chromeProfile1Ext ? new[] { chromeProfile1ExtId }
                : throw new DirectoryNotFoundException())
            .ReadAll();

        Assert.Contains(inventory.Faults, f => f.Source == "Chrome/Default" && f.Category == "IOException");
        Assert.Contains(inventory.Extensions, e => e.Browser == "Chrome" && e.Profile == "Profile 1" && e.Id == "abc123");
        Assert.Equal(SourceHealth.Partial, inventory.Health);
    }

    [Fact]
    public void Fault_source_never_leaks_the_real_numbered_profile_directory_name()
    {
        const string marker = "MARKERXYZ12345";
        string profileDir = Path.Combine(ChromeUserData, $"Profile {marker}");
        string extensionsRoot = Path.Combine(profileDir, "Extensions");

        var inventory = new Win32BrowserExtensionInventory(path =>
                path == ChromeUserData ? new[] { profileDir }
                : path == extensionsRoot ? throw new UnauthorizedAccessException()
                : throw new DirectoryNotFoundException())
            .ReadAll();

        Assert.All(inventory.Faults, f => Assert.DoesNotContain(marker, f.Source, StringComparison.Ordinal));
        Assert.Contains(inventory.Faults, f => f.Source == "Chrome/numbered-profile" && f.Category == "UnauthorizedAccessException");
        // The safe exception category and the fixed browser identity still come through.
        Assert.Contains(inventory.Faults, f => f.Source.StartsWith("Chrome/", StringComparison.Ordinal));
    }
}
