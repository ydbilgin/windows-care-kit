using System.IO;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Win32;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Tests the <see cref="Win32AppxRemover"/> guards that run BEFORE any COM call (so they execute on any
/// box, CI included). Framework/system packages and packages with no full name are refused without ever
/// constructing a <c>PackageManager</c> — the fail-closed, per-user-only contract (spec §1.1).
/// </summary>
public class AppxRemoverTests
{
    private static InstalledAppx Appx(string fullName, bool frameworkOrSystem) => new()
    {
        PackageFullName = fullName,
        DisplayName = "Some Store App",
        IsFrameworkOrSystem = frameworkOrSystem,
    };

    [Fact]
    public async Task Refuses_framework_or_system_package_without_touching_com()
    {
        var remover = new Win32AppxRemover();

        var result = await remover.RemoveCurrentUserAsync(Appx("Contoso.Framework_1.0.0.0_x64__abc", frameworkOrSystem: true));

        Assert.False(result.Removed);
        Assert.Contains("framework", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Refuses_when_package_full_name_is_missing()
    {
        var remover = new Win32AppxRemover();

        var result = await remover.RemoveCurrentUserAsync(Appx("   ", frameworkOrSystem: false));

        Assert.False(result.Removed);
        Assert.Contains("full name", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Null_package_throws_argument_null()
    {
        var remover = new Win32AppxRemover();
        await Assert.ThrowsAsync<ArgumentNullException>(() => remover.RemoveCurrentUserAsync(null!));
    }

    [Fact]
    public async Task Logs_refused_with_the_package_full_name_when_framework_is_blocked()
    {
        // M4: a refused (framework) removal is logged before any COM call. Real ExecutionLog over a temp file
        // with a non-redacting LogRedactor(null, null) so the asserted strings appear verbatim.
        string logPath = Path.Combine(Path.GetTempPath(), "wck-appxlog-" + Guid.NewGuid().ToString("N") + ".jsonl");
        try
        {
            var log = new ExecutionLog(logPath, new LogRedactor(null, null));
            var remover = new Win32AppxRemover(log);
            const string fullName = "Contoso.Framework_1.0.0.0_x64__abc";

            var result = await remover.RemoveCurrentUserAsync(Appx(fullName, frameworkOrSystem: true));

            Assert.False(result.Removed);
            string written = File.ReadAllText(logPath);
            Assert.Contains("appx.remove.refused", written);
            Assert.Contains(fullName, written);
        }
        finally
        {
            if (File.Exists(logPath)) File.Delete(logPath);
        }
    }

    [Fact]
    public async Task A_non_installed_user_package_is_never_removed()
    {
        // A made-up full name that cannot belong to the current user → the per-user membership guard refuses it.
        // (This DOES touch the packaging API; if the API is unavailable on this SKU we still get a non-removed
        // result with a reason, never a throw — so the assertion holds either way.)
        var remover = new Win32AppxRemover();

        var result = await remover.RemoveCurrentUserAsync(
            Appx("WindowsCareKit.NonExistent_9.9.9.9_x64__zzzzzzzzzzzzz", frameworkOrSystem: false));

        Assert.False(result.Removed);
        Assert.False(string.IsNullOrWhiteSpace(result.Reason));
    }
}
