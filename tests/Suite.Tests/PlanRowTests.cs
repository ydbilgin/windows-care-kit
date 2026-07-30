using System.ComponentModel;
using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Tests.Security;
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

/// <summary>
/// Spec §2.1 / §2.2: the selection and recovery surface. The rule these tests defend is that a checkbox may
/// only appear where the engine can honour the veto, and that recovery is COUNTED from a typed value — never
/// parsed out of the rendered <see cref="PlanRow.Undo"/> string, which changes with the UI language.
/// </summary>
public class PlanRowSelectionTests
{
    private static PlannedAction Junk(UndoCapability undo = UndoCapability.None, string path = @"C:\junk\a.tmp")
        => TestData.FileDelete(path) with { Undo = undo };

    /// <summary>A bare object-initializer row: the legacy literal path, which carries rendered text only.</summary>
    private static PlanRow LiteralRow() => new()
    {
        Text = "Delete: C:\\junk\\a.tmp",
        RiskText = "Low",
        RiskBrush = RiskVisuals.For(RiskLevel.Low),
        Undo = "undo: Full",
    };

    // ---- IsVetoable defaults (MP-3 target) ----------------------------------------------------------------

    [Fact]
    public void A_row_is_not_vetoable_unless_the_caller_asks_for_it()
    {
        Assert.False(PlanRow.FromAction(Junk(), new I18n()).IsVetoable);
        Assert.False(PlanRow.FromAction(Junk()).IsVetoable);
        Assert.False(LiteralRow().IsVetoable);
    }

    [Fact]
    public void A_row_is_vetoable_when_the_caller_proves_the_engine_acts_on_that_single_action()
    {
        PlanRow row = PlanRow.FromAction(Junk(), new I18n(), isVetoable: true);

        Assert.True(row.IsVetoable);
        Assert.NotNull(row.Action);
    }

    [Fact]
    public void FromSkipped_is_never_vetoable()
    {
        Assert.False(PlanRow.FromSkipped(Junk(), "protected", new I18n()).IsVetoable);
        Assert.False(PlanRow.FromSkipped(Junk(), "protected").IsVetoable);
    }

    [Fact]
    public void A_row_that_cannot_name_its_action_is_never_vetoable()
    {
        // The legacy path renders text but keeps no action, so a veto could not be honoured at execution time.
        // Asking for one must not produce a checkbox the engine would ignore (spec §1.1).
        PlanRow row = PlanRow.FromAction(Junk(), i18n: null, isVetoable: true);

        Assert.Null(row.Action);
        Assert.False(row.IsVetoable);
    }

    // ---- IsIncluded contract (A1) -------------------------------------------------------------------------

    [Fact]
    public void IsIncluded_throws_when_set_on_a_non_vetoable_row()
    {
        PlanRow displayOnly = PlanRow.FromAction(Junk(), new I18n());

        var ex = Assert.Throws<InvalidOperationException>(() => displayOnly.IsIncluded = true);
        Assert.Contains("vetoable", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(displayOnly.IsIncluded);
    }

    [Fact]
    public void IsIncluded_throws_on_a_skipped_row_and_on_a_legacy_literal_row()
    {
        Assert.Throws<InvalidOperationException>(
            () => PlanRow.FromSkipped(Junk(), "protected", new I18n()).IsIncluded = true);
        Assert.Throws<InvalidOperationException>(() => LiteralRow().IsIncluded = true);
    }

    [Fact]
    public void IsIncluded_throws_even_when_set_to_false_on_a_display_only_row()
    {
        // "false" is still a recorded choice on a row that has none — refusing both directions keeps the
        // contract one rule rather than two.
        Assert.Throws<InvalidOperationException>(() => LiteralRow().IsIncluded = false);
    }

    [Fact]
    public void IsIncluded_is_settable_on_a_vetoable_row_and_notifies()
    {
        PlanRow row = PlanRow.FromAction(Junk(), new I18n(), isVetoable: true);
        var changed = new List<string?>();
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.IsIncluded = true;

        Assert.True(row.IsIncluded);
        Assert.Contains(nameof(PlanRow.IsIncluded), changed);
    }

    [Fact]
    public void A_vetoable_row_starts_excluded()
    {
        Assert.False(PlanRow.FromAction(Junk(), new I18n(), isVetoable: true).IsIncluded);
    }

    // ---- IsSkipped ----------------------------------------------------------------------------------------

    [Fact]
    public void IsSkipped_is_true_only_for_rows_built_by_FromSkipped()
    {
        Assert.True(PlanRow.FromSkipped(Junk(), "protected", new I18n()).IsSkipped);
        Assert.True(PlanRow.FromSkipped(Junk(), "protected").IsSkipped);
        Assert.False(PlanRow.FromAction(Junk(), new I18n()).IsSkipped);
        Assert.False(PlanRow.FromAction(Junk()).IsSkipped);
        Assert.False(LiteralRow().IsSkipped);
    }

    // ---- Recovery is typed (A4) --------------------------------------------------------------------------

    [Theory]
    [InlineData(UndoCapability.Full)]
    [InlineData(UndoCapability.Partial)]
    [InlineData(UndoCapability.None)]
    public void Recovery_is_the_actions_typed_capability_on_the_localized_path(UndoCapability undo)
    {
        Assert.Equal(undo, PlanRow.FromAction(Junk(undo), new I18n()).Recovery);
    }

    [Theory]
    [InlineData(UndoCapability.Full)]
    [InlineData(UndoCapability.Partial)]
    [InlineData(UndoCapability.None)]
    public void Recovery_carries_the_typed_capability_through_the_legacy_factory_path(UndoCapability undo)
    {
        // The legacy path renders "undo: Full" as text; Recovery must still be the typed value, or a counter
        // and the row the user reads would disagree.
        Assert.Equal(undo, PlanRow.FromAction(Junk(undo)).Recovery);
        Assert.Equal(undo, PlanRow.FromSkipped(Junk(undo), "protected").Recovery);
    }

    [Fact]
    public void A_legacy_literal_row_promises_no_recovery()
    {
        // It has no typed field to read. Falling back to None is the fail-safe direction: claiming
        // recoverability the engine never promised is the more dangerous error.
        Assert.Equal(UndoCapability.None, LiteralRow().Recovery);
    }

    // ---- MP-4: the wire-level proof ----------------------------------------------------------------------

    /// <summary>
    /// The composition seam, not a unit of a counter: rows are built through the REAL localized path against
    /// the SHIPPED string tables, and the language is actually switched underneath them. <see cref="PlanRow.Undo"/>
    /// is expected to change — that is its job — while every <see cref="PlanRow.Recovery"/> and every count
    /// derived from it must be identical. Deriving recovery by parsing the rendered string passes in English and
    /// fails here, which is exactly what spec §1.2 forbids.
    /// </summary>
    [Fact]
    public void A_language_switch_changes_the_rendered_undo_text_but_no_typed_recovery_value()
    {
        var i18n = new I18n(RepoSource.PathFor("src/Suite.App.Wpf/lang"));
        i18n.Load("en");

        PlanRow[] rows =
        [
            PlanRow.FromAction(Junk(UndoCapability.Full, @"C:\junk\full.tmp"), i18n),
            PlanRow.FromAction(Junk(UndoCapability.Partial, @"C:\junk\partial.tmp"), i18n),
            PlanRow.FromAction(Junk(UndoCapability.None, @"C:\junk\none.tmp"), i18n),
        ];

        UndoCapability[] recoveryInEnglish = rows.Select(r => r.Recovery).ToArray();
        string[] undoInEnglish = rows.Select(r => r.Undo).ToArray();
        int[] countsInEnglish = CountByRecovery(rows);

        Assert.Equal(
            new[] { UndoCapability.Full, UndoCapability.Partial, UndoCapability.None },
            recoveryInEnglish);
        Assert.Equal(new[] { 1, 1, 1 }, countsInEnglish);

        i18n.Load("tr");

        string[] undoInTurkish = rows.Select(r => r.Undo).ToArray();

        // Guard against a vacuous pass: if the switch did not actually re-render the rows, the rest of this
        // test would hold for the wrong reason.
        Assert.NotEqual(undoInEnglish, undoInTurkish);
        foreach (string turkish in undoInTurkish)
        {
            Assert.DoesNotContain("Full", turkish, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Partial", turkish, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("None", turkish, StringComparison.OrdinalIgnoreCase);
        }

        Assert.Equal(recoveryInEnglish, rows.Select(r => r.Recovery).ToArray());
        Assert.Equal(countsInEnglish, CountByRecovery(rows));
    }

    /// <summary>Recovery counting exactly as the ReviewSummary will do it (spec §1.2): arithmetic over the
    /// typed value, never over rendered text.</summary>
    private static int[] CountByRecovery(IEnumerable<PlanRow> rows)
    {
        PlanRow[] snapshot = rows.ToArray();
        return
        [
            snapshot.Count(r => r.Recovery == UndoCapability.Full),
            snapshot.Count(r => r.Recovery == UndoCapability.Partial),
            snapshot.Count(r => r.Recovery == UndoCapability.None),
        ];
    }
}
