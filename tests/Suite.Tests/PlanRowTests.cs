using System.ComponentModel;
using System.Reflection;
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
        Risk = RiskLevel.Low,
        Undo = "undo: Full",
    };

    // ---- Selection is ONE state (spec §2.0) ---------------------------------------------------------------

    /// <summary>
    /// Every construction path lands on exactly one <see cref="RowSelection"/>, and the three convenience
    /// predicates are read OFF that value rather than kept beside it. This is the contract the boolean pair
    /// could not hold: "blocked" and "included" were independently settable, so they could disagree.
    /// </summary>
    [Fact]
    public void Every_construction_path_lands_on_exactly_one_selection_state()
    {
        Assert.Equal(RowSelection.Required, PlanRow.FromAction(Junk(), new I18n()).Selection);
        Assert.Equal(RowSelection.OptionalExcluded, PlanRow.FromAction(Junk(), new I18n(), isVetoable: true).Selection);
        Assert.Equal(RowSelection.Blocked, PlanRow.FromSkipped(Junk(), "protected", new I18n()).Selection);
        Assert.Equal(RowSelection.Blocked, PlanRow.FromSkipped(Junk(), "protected").Selection);

        // The legacy literal paths name no action, so they assert NOTHING about execution — neither required
        // nor blocked. Reporting them as Required would claim the engine always runs a row it cannot even name.
        Assert.Equal(RowSelection.Unbound, PlanRow.FromAction(Junk()).Selection);
        Assert.Equal(RowSelection.Unbound, LiteralRow().Selection);
    }

    [Fact]
    public void The_selection_predicates_are_derived_from_the_state_and_cannot_disagree_with_it()
    {
        PlanRow[] rows =
        [
            PlanRow.FromAction(Junk(), new I18n()),
            PlanRow.FromAction(Junk(), new I18n(), isVetoable: true),
            PlanRow.FromSkipped(Junk(), "protected", new I18n()),
            PlanRow.FromSkipped(Junk(), "protected"),
            PlanRow.FromAction(Junk()),
            LiteralRow(),
        ];

        foreach (PlanRow row in rows)
        {
            Assert.Equal(row.Selection is RowSelection.Blocked, row.IsSkipped);

            // Disposition is the display-only path's view of the SAME value, so it can never disagree with
            // IsSkipped. A typed skipped row reports WillNotRun without any caller setting it.
            Assert.Equal(row.Disposition is RowDisposition.WillNotRun, row.IsSkipped);
            Assert.Equal(
                row.Selection is RowSelection.OptionalIncluded or RowSelection.OptionalExcluded,
                row.IsVetoable);
            Assert.Equal(row.Selection is RowSelection.OptionalIncluded, row.IsIncluded);

            // A blocked row is never vetoable and a vetoable row is never blocked — not by convention between
            // two fields, but because one value cannot be two things.
            Assert.False(row.IsSkipped && row.IsVetoable);
        }
    }

    /// <summary>
    /// The state is unforgeable: there is no public write path to <see cref="PlanRow.Selection"/> or
    /// <see cref="PlanRow.IsVetoable"/>, so an object-initializer caller cannot assert a selection the engine
    /// could not honour. Previously <c>IsVetoable</c> had a public <c>init</c> and only a getter's invariant
    /// stood between a caller and a checkbox on a row nothing would execute.
    /// </summary>
    [Fact]
    public void Selection_state_has_no_public_write_path()
    {
        Assert.Null(typeof(PlanRow).GetProperty(nameof(PlanRow.Selection))!.SetMethod);
        Assert.Null(typeof(PlanRow).GetProperty(nameof(PlanRow.IsVetoable))!.SetMethod);
        Assert.Null(typeof(PlanRow).GetProperty(nameof(PlanRow.IsSkipped))!.SetMethod);
    }

    /// <summary>
    /// The display-only path may say "this did not happen" and NOTHING else. Swept over the whole enum rather
    /// than over the one member that exists today, so a future member mapped to <c>Required</c> — a row that
    /// cannot name its action claiming the engine will run it — fails here and names itself.
    /// </summary>
    [Fact]
    public void No_row_disposition_value_can_assert_an_engine_claim()
    {
        RowDisposition[] values = Enum.GetValues<RowDisposition>();

        // Non-vacuity: an empty sweep would satisfy every assertion below for the wrong reason.
        Assert.NotEmpty(values);

        foreach (RowDisposition value in values)
        {
            var row = new PlanRow
            {
                Text = string.Empty,
                RiskText = string.Empty,
                Risk = RiskLevel.Info,
                Undo = string.Empty,
                Disposition = value,
            };

            Assert.True(
                row.Selection is RowSelection.Unbound or RowSelection.Blocked,
                $"RowDisposition.{value} produced Selection={row.Selection} on a row that names no Action. A "
                + "display-only row cannot make an engine claim: the only states it may reach are Unbound "
                + "(said nothing) and Blocked (said it will not run).");
        }
    }

    /// <summary>
    /// <c>Disposition</c> replaced <c>LegacySelection</c>; both must not survive. Two private writers for one
    /// state is the duplication this round removes everywhere else, and leaving one inside the type itself
    /// would be the same defect at the shortest possible range. Asserted by reflection over the private
    /// surface, which is where the member lived.
    /// </summary>
    [Fact]
    public void The_legacy_selection_writer_no_longer_exists()
    {
        Assert.Null(typeof(PlanRow).GetProperty(
            "LegacySelection",
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public));
    }

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

        // The checkbox moves the ONE state, so a view bound to Selection (lock badge vs checkbox vs WON'T RUN)
        // is told about it too — a stale badge beside a fresh checkbox is the same class of lie.
        Assert.Equal(RowSelection.OptionalIncluded, row.Selection);
        Assert.Contains(nameof(PlanRow.Selection), changed);

        row.IsIncluded = false;
        Assert.Equal(RowSelection.OptionalExcluded, row.Selection);
    }

    [Fact]
    public void A_vetoable_row_starts_excluded()
    {
        PlanRow row = PlanRow.FromAction(Junk(), new I18n(), isVetoable: true);

        Assert.False(row.IsIncluded);
        Assert.Equal(RowSelection.OptionalExcluded, row.Selection);
    }

    // ---- IsSkipped ----------------------------------------------------------------------------------------

    [Fact]
    public void IsSkipped_is_true_only_for_rows_built_by_FromSkipped()
    {
        PlanRow typedSkipped = PlanRow.FromSkipped(Junk() with { Risk = RiskLevel.Info }, "protected", new I18n());
        PlanRow legacySkipped = PlanRow.FromSkipped(Junk() with { Risk = RiskLevel.Low }, "protected");

        Assert.True(typedSkipped.IsSkipped);
        Assert.Equal(RiskLevel.Info, typedSkipped.Risk);
        Assert.True(legacySkipped.IsSkipped);
        Assert.Equal(RiskLevel.Low, legacySkipped.Risk);
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

    /// <summary>
    /// A bare literal row has no typed source, so its recovery is UNKNOWN and says so (spec §2.0b). The
    /// previous contract collapsed it to a definite <see cref="UndoCapability.None"/>: fail-safe, but not
    /// honest — this very fixture renders "undo: Full" to the user while a counter scored it permanent, on no
    /// evidence in either direction (P20-R1).
    /// </summary>
    [Fact]
    public void A_bare_literal_row_reports_unknown_recovery_rather_than_a_definite_None()
    {
        PlanRow row = LiteralRow();

        Assert.Null(row.Recovery);
        Assert.NotEqual(UndoCapability.None, row.Recovery);
        Assert.Contains("Full", row.Undo, StringComparison.Ordinal); // what the user is actually reading
    }

    /// <summary>
    /// The counting rule the ReviewSummary must follow: unknown is its own tally, never folded into the
    /// permanent one. Collapsing unknown back to <see cref="UndoCapability.None"/> turns this red, because the
    /// permanent count would then claim two actions it cannot support.
    /// </summary>
    [Fact]
    public void A_counter_reports_an_unknown_row_as_unknown_and_never_as_permanent()
    {
        PlanRow[] rows =
        [
            PlanRow.FromAction(Junk(UndoCapability.None, @"C:\junk\none.tmp"), new I18n()),
            LiteralRow(),
        ];

        int[] counts = CountByRecovery(rows);

        Assert.Equal(new[] { 0, 0, 1, 1 }, counts); // full, partial, permanent, unknown
        Assert.Equal(1, counts[PermanentBucket]);
        Assert.Equal(1, counts[UnknownBucket]);
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

        UndoCapability?[] recoveryInEnglish = rows.Select(r => r.Recovery).ToArray();
        string[] undoInEnglish = rows.Select(r => r.Undo).ToArray();
        int[] countsInEnglish = CountByRecovery(rows);

        Assert.Equal(
            new UndoCapability?[] { UndoCapability.Full, UndoCapability.Partial, UndoCapability.None },
            recoveryInEnglish);
        Assert.Equal(new[] { 1, 1, 1, 0 }, countsInEnglish);

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

    private const int PermanentBucket = 2;
    private const int UnknownBucket = 3;

    /// <summary>Recovery counting exactly as the ReviewSummary will do it (spec §1.2): arithmetic over the
    /// typed value, never over rendered text — and with an explicit UNKNOWN bucket (spec §2.0b), because a row
    /// whose capability could not be determined is not evidence of permanence.</summary>
    private static int[] CountByRecovery(IEnumerable<PlanRow> rows)
    {
        PlanRow[] snapshot = rows.ToArray();
        return
        [
            snapshot.Count(r => r.Recovery == UndoCapability.Full),
            snapshot.Count(r => r.Recovery == UndoCapability.Partial),
            snapshot.Count(r => r.Recovery == UndoCapability.None),
            snapshot.Count(r => r.Recovery is null),
        ];
    }
}
