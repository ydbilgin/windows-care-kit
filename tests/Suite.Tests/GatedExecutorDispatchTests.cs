using WindowsCareKit.Core.Planning;
using WindowsCareKit.Tests.Execution;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>Each action type routes to the matching adapter, in plan order, with the expected log trail.</summary>
public class GatedExecutorDispatchTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Routes_each_action_type_to_the_right_adapter_in_plan_order()
    {
        using var fx = new ExecutorFixture();

        var file = TestData.FileDelete(@"C:\Program Files\SomeApp\junk.tmp");
        var reg = TestData.RegValue(RegistryHive.CurrentUser,
            @"Software\Microsoft\Windows\CurrentVersion\Run", "SomeApp"); // value-delete on an allowed Run key
        var svc = TestData.Service("SomeVendorSvc", ServiceOperation.Stop);
        var task = TestData.Task(@"\SomeVendor\Updater", TaskOperation.Disable);
        var cmd = TestData.Command(@"C:\Program Files\SomeApp\unins000.exe", "/S"); // rooted, gate-allowed
        var copy = TestData.Copy(@"C:\src\a.txt", @"D:\backup\a.txt");
        var merge = TestData.Restore(@"C:\src\b.txt", @"D:\dest\b.txt");

        var plan = new OperationPlan("t", "uninstall",
            new PlannedAction[] { file, reg, svc, task, cmd, copy, merge }, T0);

        var report = fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        Assert.True(report.Authorized, string.Join(",", report.Results.Select(r => r.Detail)));
        Assert.True(report.AllDone);
        Assert.Equal(new[]
        {
            $"file:{file.Id}",
            $"registry:{reg.Id}",
            $"service:{svc.Id}",
            $"task:{task.Id}",
            $"command:{cmd.Id}",
            $"copy:{copy.Id}",
            $"merge:{merge.Id}",
        }, fx.Adapters.Calls);
    }

    [Fact]
    public void Writes_plan_start_action_allowed_action_done_and_plan_done()
    {
        using var fx = new ExecutorFixture();
        var file = TestData.FileDelete(@"C:\Program Files\SomeApp\junk.tmp");
        var plan = new OperationPlan("t", "uninstall", new[] { file }, T0);

        fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        var lines = string.Join("\n", fx.LogLines());
        Assert.Contains("plan.start", lines);
        Assert.Contains("action.allowed", lines);
        Assert.Contains("action.done", lines);
        Assert.Contains("plan.done", lines);
    }
}
