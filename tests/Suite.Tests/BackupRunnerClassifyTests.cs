using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Planning;
using Xunit;

namespace WindowsCareKit.Tests;

public class BackupRunnerClassifyTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(BackupFailureCode.Missing, CopySkipReason.Missing)]
    [InlineData(BackupFailureCode.TooLong, CopySkipReason.TooLong)]
    [InlineData(BackupFailureCode.Forbidden, CopySkipReason.Forbidden)]
    [InlineData(BackupFailureCode.Locked, CopySkipReason.Locked)]
    [InlineData(BackupFailureCode.Unknown, CopySkipReason.Other)]
    public void Skip_reason_comes_from_the_typed_failure_code_not_the_detail_string(
        BackupFailureCode code, CopySkipReason expected)
    {
        var copy = new CopyAction
        {
            Id = "c1",
            Source = @"C:\src\a.txt",
            Destination = @"C:\dst\a.txt",
            Description = "copy a",
            Reason = "backup",
        };
        var plan = new OperationPlan("b", "backup", new PlannedAction[] { copy }, T0);
        // Failed copy with NO structured CopyOutcomes and a Detail that contains NONE of the old substrings.
        var report = new BackupExecutionReport(true, new[]
        {
            new BackupActionResult("c1", BackupActionStatus.Failed, "opaque adapter detail")
            {
                FailureCode = code,
            },
        });

        CopySkipReport copyReport = BackupRunner.BuildCopyReport(plan, report);

        CopyFileOutcome outcome = Assert.Single(copyReport.Outcomes);
        Assert.False(outcome.Copied);
        Assert.Equal(expected, outcome.Reason);
    }
}
