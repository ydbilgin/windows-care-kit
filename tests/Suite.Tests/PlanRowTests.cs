using WindowsCareKit.App.ViewModels;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// L7: a directory <c>CopyAction</c> (whole-tree recursive copy) must be flagged in the dry-run preview so it
/// is never hidden behind one opaque row. The directory probe happens off-thread in the view-model; the
/// <see cref="PlanRow.FromAction(WindowsCareKit.Core.Planning.PlannedAction, bool)"/> overload only renders
/// the hint.
/// </summary>
public class PlanRowTests
{
    [Fact]
    public void Whole_tree_copy_row_carries_a_warning()
    {
        var row = PlanRow.FromAction(TestData.Copy(@"C:\src\dir", @"D:\pay\dir"), isWholeTree: true);
        Assert.Contains("whole-tree copy", row.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Single_file_copy_row_has_no_whole_tree_warning()
    {
        var row = PlanRow.FromAction(TestData.Copy(@"C:\src\file.txt", @"D:\pay\file.txt"), isWholeTree: false);
        Assert.DoesNotContain("whole-tree copy", row.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Whole_tree_hint_is_ignored_for_non_copy_actions()
    {
        // The hint only ever applies to CopyAction; a flag set true for, say, a file delete is a no-op.
        var row = PlanRow.FromAction(TestData.FileDelete(@"C:\src\junk"), isWholeTree: true);
        Assert.DoesNotContain("whole-tree copy", row.Detail ?? string.Empty, StringComparison.OrdinalIgnoreCase);
    }
}
