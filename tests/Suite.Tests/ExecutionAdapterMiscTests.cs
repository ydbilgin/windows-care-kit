using WindowsCareKit.Core.Planning;
using WindowsCareKit.Execution.Adapters;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>Pure helpers + the process adapter's exit-code contract.</summary>
public class ExecutionAdapterMiscTests
{
    [Theory]
    [InlineData(@"\Vendor\Updater", @"\Vendor", "Updater")]
    [InlineData(@"Vendor\Updater", @"\Vendor", "Updater")]   // leading backslash is normalized in
    [InlineData(@"\TopLevelTask", @"\", "TopLevelTask")]
    [InlineData(@"TopLevelTask", @"\", "TopLevelTask")]
    public void SplitTaskPath_separates_folder_and_name(string input, string expectedFolder, string expectedName)
    {
        ScheduledTaskAdapter.SplitTaskPath(input, out string folder, out string name);
        Assert.Equal(expectedFolder, folder);
        Assert.Equal(expectedName, name);
    }

    [Fact]
    public void SplitTaskPath_rejects_a_path_with_no_task_name()
        => Assert.Throws<ArgumentException>(() =>
            ScheduledTaskAdapter.SplitTaskPath(@"\Vendor\", out _, out _));

    [Fact]
    public void Process_adapter_surfaces_a_nonzero_exit_as_ProcessExitCodeException()
    {
        // The adapter itself does not gate (the SafetyGate does, elsewhere); cmd is fine to invoke directly here.
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!File.Exists(cmd))
            return; // non-Windows CI: skip gracefully

        var action = new CommandAction
        {
            FileName = cmd,
            Arguments = new[] { "/c", "exit", "5" },
            Description = "exit 5",
            Reason = "test",
        };

        var ex = Assert.Throws<ProcessExitCodeException>(() => new ProcessAdapter().Run(action));
        Assert.Equal(5, ex.ExitCode);
    }

    [Fact]
    public void Process_adapter_returns_normally_on_a_zero_exit()
    {
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        if (!File.Exists(cmd))
            return;

        new ProcessAdapter().Run(new CommandAction
        {
            FileName = cmd,
            Arguments = new[] { "/c", "exit", "0" },
            Description = "exit 0",
            Reason = "test",
        });
        // no throw == pass
    }

    [Theory]
    [InlineData(@"C:\short", @"C:\short")]
    [InlineData(@"\\?\C:\ext", @"\\?\C:\ext")]
    public void CopyAdapter_LongPath_leaves_short_and_extended_alone(string input, string expected)
        => Assert.Equal(expected, CopyAdapter.LongPath(input));
}
