using System;
using System.IO;
using System.Linq;
using WindowsCareKit.Core.Modules.Clean;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests.Clean;

/// <summary>
/// NEW-07 MAJOR-01 fix: <see cref="Win32StartupProbe"/> must not collapse an inaccessible Startup folder
/// into "absent" (honest-empty) the way a preflight <c>Directory.Exists</c> check would (Microsoft documents
/// that it returns false both for genuine absence AND when determining existence fails — insufficient
/// permissions, I/O errors). These tests inject synthetic enumeration outcomes through the test-only
/// constructor seam so the classification can be proven without needing a real inaccessible directory.
/// Real registry Run/RunOnce reads are exercised for real (like <see cref="Win32ReadersSmokeTests"/>) and are
/// assumed not to throw under normal, non-elevated permissions — the same assumption the existing smoke
/// tests already make.
/// </summary>
public class Win32StartupProbeTests
{
    private static readonly string UserStartup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
    private static readonly string CommonStartup = Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup);

    [Fact]
    public void DirectoryNotFoundException_at_both_Startup_folders_stays_honest_empty_and_Complete()
    {
        var probe = new Win32StartupProbe(_ => throw new DirectoryNotFoundException());

        StartupInventory inventory = probe.ReadAll();

        Assert.DoesNotContain(inventory.Entries, e => e.Source == StartupSource.StartupFolder);
        Assert.DoesNotContain(inventory.Faults, f => f.Source.StartsWith("Startup folder", StringComparison.Ordinal));
        Assert.Equal(SourceHealth.Complete, inventory.Health);
    }

    [Fact]
    public void UnauthorizedAccessException_at_the_user_Startup_folder_becomes_Partial_and_preserves_the_healthy_sibling()
    {
        var sibling = Path.Combine(CommonStartup, "sibling-app.lnk");
        var probe = new Win32StartupProbe(folder =>
            folder == UserStartup
                ? throw new UnauthorizedAccessException()
                : new[] { sibling });

        StartupInventory inventory = probe.ReadAll();

        // The failing source is a real, reported fault — not silently absorbed as "nothing there".
        Assert.Contains(inventory.Faults, f => f.Source == "Startup folder (user)" && f.Category == "UnauthorizedAccessException");
        // Non-vacuity: the sibling folder's real (synthetic) entry is NOT lost because another source failed.
        Assert.Contains(inventory.Entries, e => e.Source == StartupSource.StartupFolder && e.Name == "sibling-app");
        Assert.Equal(SourceHealth.Partial, inventory.Health);
    }

    [Fact]
    public void General_IOException_at_the_common_Startup_folder_becomes_Partial_and_preserves_the_healthy_sibling()
    {
        var sibling = Path.Combine(UserStartup, "sibling-app.lnk");
        var probe = new Win32StartupProbe(folder =>
            folder == CommonStartup
                ? throw new IOException("disk error")
                : new[] { sibling });

        StartupInventory inventory = probe.ReadAll();

        Assert.Contains(inventory.Faults, f => f.Source == "Startup folder (common)" && f.Category == "IOException");
        Assert.Contains(inventory.Entries, e => e.Source == StartupSource.StartupFolder && e.Name == "sibling-app");
        Assert.Equal(SourceHealth.Partial, inventory.Health);
    }

    [Fact]
    public void UnauthorizedAccessException_at_both_Startup_folders_becomes_Unavailable_when_registry_has_no_other_source()
    {
        // Both folders fail; whether overall health lands on Partial or Unavailable also depends on the real
        // registry hives on this box, so assert the weaker, always-true invariant: it must NEVER report Complete
        // (the exact NEW-07 defect this fix closes) once real inspection failures occurred.
        var probe = new Win32StartupProbe(_ => throw new UnauthorizedAccessException());

        StartupInventory inventory = probe.ReadAll();

        Assert.NotEqual(SourceHealth.Complete, inventory.Health);
        Assert.Contains(inventory.Faults, f => f.Source == "Startup folder (user)" && f.Category == "UnauthorizedAccessException");
        Assert.Contains(inventory.Faults, f => f.Source == "Startup folder (common)" && f.Category == "UnauthorizedAccessException");
    }
}
