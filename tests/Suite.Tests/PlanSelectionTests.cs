using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using WindowsCareKit.Core.Execution;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Spec §2.3 / §1.1: a per-row veto is honoured by composing a NEW <see cref="OperationPlan"/> from the
/// surviving actions, so the plan that is previewed, hashed, approved and executed are the same object.
/// The veto is then enforced by the existing TOCTOU contract rather than by trust — which is only true if the
/// subset's hash really does move when the selection moves, and if the source plan's action ORDER survives
/// (restore point first, vendor uninstall second is an execution-safety property, not a display choice).
/// <para>
/// Spec §2.0 adds what the first attempt lacked: rows bind to plan OCCURRENCES rather than to action
/// references, <see cref="RowSelection.Blocked"/> dominates every other row for the same action, and a row set
/// that does not describe the plan is REFUSED instead of being composed into a shorter one.
/// </para>
/// </summary>
public class PlanSelectionTests
{
    private static readonly DateTime PlannedAt = new(2026, 7, 30, 12, 0, 0, DateTimeKind.Utc);

    private const string Title = "Remove SomeApp";
    private const string Module = "uninstall";

    private static CreateRestorePointAction RestorePoint() => new()
    {
        RestorePointName = "Windows Care Kit — before uninstall",
        Description = "restore point",
        Reason = "rollback state",
        Undo = UndoCapability.Full,
    };

    private static CommandAction Vendor() => new()
    {
        FileName = @"C:\Program Files\SomeApp\uninstall.exe",
        Arguments = new[] { "/S" },
        Description = "vendor uninstaller",
        Reason = "official removal",
        Undo = UndoCapability.None,
    };

    private static FileDeleteAction Leftover(string path) => new()
    {
        Path = path,
        Description = "delete " + path,
        Reason = "leftover",
        Undo = UndoCapability.Partial,
    };

    private static OperationPlan Plan(params PlannedAction[] actions)
        => new(Title, Module, actions, PlannedAt);

    /// <summary>A required step: the engine runs it whatever the user does, so it carries no veto control.</summary>
    private static PlanRow Required(PlannedAction a) => PlanRow.FromAction(a, new I18n());

    /// <summary>An optional step: vetoable, and included or not as stated.</summary>
    private static PlanRow Optional(PlannedAction a, bool included)
    {
        PlanRow row = PlanRow.FromAction(a, new I18n(), isVetoable: true);
        row.IsIncluded = included;
        return row;
    }

    /// <summary>A blocked step: the gate prints <c>WON'T RUN</c> over it, so the engine must not run it.</summary>
    private static PlanRow Blocked(PlannedAction a, string reason = "shared with another app")
        => PlanRow.FromSkipped(a, reason, new I18n());

    // ---- A2: order and retention ---------------------------------------------------------------------------

    [Fact]
    public void Subset_keeps_the_source_plans_order_not_the_views()
    {
        CreateRestorePointAction restore = RestorePoint();
        CommandAction vendor = Vendor();
        FileDeleteAction cache = Leftover(@"C:\Users\alice\AppData\Local\SomeApp");
        FileDeleteAction docs = Leftover(@"C:\Users\alice\Documents\SomeApp");
        OperationPlan source = Plan(restore, vendor, cache, docs);

        // The view sorted its rows by name; that must not reach the execution order.
        PlanRow[] rowsInViewOrder =
        [
            Optional(cache, included: true),
            Optional(docs, included: true),
            Required(restore),
            Required(vendor),
        ];

        OperationPlan subset = PlanSelection.Subset(source, rowsInViewOrder);

        Assert.Equal(
            new PlannedAction[] { restore, vendor, cache, docs },
            subset.Actions);
    }

    /// <summary>RENAMED from "non-vetoable rows are always retained": "not vetoable" collapses two opposite
    /// meanings — a REQUIRED step the engine always runs and a BLOCKED one it never runs — and that conflation
    /// is the defect §2.3 was corrected for. Only <see cref="RowSelection.Required"/> is retained.</summary>
    [Fact]
    public void Required_rows_are_retained_even_though_they_are_not_included()
    {
        CreateRestorePointAction restore = RestorePoint();
        CommandAction vendor = Vendor();
        FileDeleteAction leftover = Leftover(@"C:\ProgramData\SomeApp");
        OperationPlan source = Plan(restore, vendor, leftover);

        PlanRow restoreRow = Required(restore);
        PlanRow vendorRow = Required(vendor);
        Assert.Equal(RowSelection.Required, restoreRow.Selection);
        Assert.False(restoreRow.IsIncluded); // a required row never carries an inclusion value
        Assert.False(vendorRow.IsIncluded);

        OperationPlan subset = PlanSelection.Subset(
            source,
            [restoreRow, vendorRow, Optional(leftover, included: false)]);

        Assert.Equal(new PlannedAction[] { restore, vendor }, subset.Actions);
    }

    [Fact]
    public void An_unchecked_optional_row_removes_exactly_its_own_action()
    {
        CommandAction vendor = Vendor();
        FileDeleteAction keep = Leftover(@"C:\ProgramData\SomeApp\keep");
        FileDeleteAction drop = Leftover(@"C:\ProgramData\SomeApp\drop");
        OperationPlan source = Plan(vendor, keep, drop);

        OperationPlan subset = PlanSelection.Subset(
            source,
            [Required(vendor), Optional(keep, included: true), Optional(drop, included: false)]);

        Assert.Equal(new PlannedAction[] { vendor, keep }, subset.Actions);
    }

    [Fact]
    public void Two_structurally_identical_actions_are_vetoed_independently()
    {
        // Records compare by value; rows preview instances. Vetoing one must not silently drop its twin.
        FileDeleteAction first = Leftover(@"C:\ProgramData\SomeApp\dup");
        FileDeleteAction second = first with { };
        OperationPlan source = Plan(first, second);

        OperationPlan subset = PlanSelection.Subset(
            source,
            [Optional(first, included: false), Optional(second, included: true)]);

        PlannedAction survivor = Assert.Single(subset.Actions);
        Assert.Same(second, survivor);
    }

    [Fact]
    public void A_row_that_names_no_action_is_ignored_and_covers_nothing()
    {
        CommandAction vendor = Vendor();
        FileDeleteAction leftover = Leftover(@"C:\ProgramData\SomeApp");
        OperationPlan source = Plan(vendor, leftover);

        PlanRow legacyLiteral = new()
        {
            Text = "Delete: C:\\ProgramData\\SomeApp",
            RiskText = "Low",
            RiskBrush = RiskVisuals.For(RiskLevel.Low),
            Undo = "undo: Partial",
        };
        Assert.Null(legacyLiteral.Action);
        Assert.Equal(RowSelection.Unbound, legacyLiteral.Selection);

        OperationPlan subset = PlanSelection.Subset(
            source,
            [Required(vendor), legacyLiteral, Optional(leftover, included: true)]);

        Assert.Equal(new PlannedAction[] { vendor, leftover }, subset.Actions);

        // …and it cannot stand in for the row that action needs. Without this half the test passes for the
        // wrong reason: an implementation where an unbound row includes everything, or where merely BEING a row
        // counts as coverage, produces the same plan above and is caught only here.
        Assert.Throws<ArgumentException>(
            () => PlanSelection.Subset(source, [Required(vendor), legacyLiteral]));
    }

    [Fact]
    public void A_skipped_row_never_carries_its_action_into_the_subset()
    {
        CommandAction vendor = Vendor();
        FileDeleteAction blocked = Leftover(@"C:\Windows\System32\shared");
        OperationPlan source = Plan(vendor, blocked);

        OperationPlan subset = PlanSelection.Subset(
            source,
            [Required(vendor), PlanRow.FromSkipped(blocked, "shared with another app", new I18n())]);

        // FromSkipped is display-only and NOT vetoable, so "always retained" would be exactly wrong here:
        // the row states the action will not run, and the composed plan must agree with the row.
        Assert.Equal(new PlannedAction[] { vendor }, subset.Actions);
    }

    /// <summary>
    /// DELIBERATELY CHANGED from "an empty row set produces an empty plan". That assertion blessed the exact
    /// fail-open shape finding 3 is about: a caller that composed no rows at all got a valid, validly-hashed
    /// plan back, and the same arithmetic silently drops a restore point when only its row is missing. Nothing
    /// legitimate is lost — a user who unchecks everything still HAS rows, and they still compose an empty plan
    /// (the test below).
    /// </summary>
    [Fact]
    public void Composing_a_non_empty_plan_from_no_rows_at_all_is_refused()
    {
        OperationPlan source = Plan(Vendor(), Leftover(@"C:\ProgramData\SomeApp"));

        Assert.Throws<ArgumentException>(() => PlanSelection.Subset(source, []));
    }

    [Fact]
    public void Unchecking_every_optional_row_composes_an_empty_plan()
    {
        FileDeleteAction first = Leftover(@"C:\ProgramData\SomeApp\a");
        FileDeleteAction second = Leftover(@"C:\ProgramData\SomeApp\b");
        OperationPlan source = Plan(first, second);

        OperationPlan subset = PlanSelection.Subset(
            source,
            [Optional(first, included: false), Optional(second, included: false)]);

        Assert.True(subset.IsEmpty);
    }

    [Fact]
    public void An_empty_source_plan_needs_no_rows()
    {
        OperationPlan subset = PlanSelection.Subset(Plan(), []);

        Assert.True(subset.IsEmpty);
    }

    // ---- A1: Blocked dominates -----------------------------------------------------------------------------

    /// <summary>
    /// The gate renders required rows and <c>WON'T RUN</c> rows in ONE list, so two rows can name the same
    /// action. A row that says "included" must not resurrect an action another row says will not run — that is
    /// the <c>WON'T RUN</c> defect reached through a second door, and it is order-independent.
    /// </summary>
    [Fact]
    public void A_blocked_row_dominates_an_included_alias_for_the_same_action()
    {
        CreateRestorePointAction restore = RestorePoint();
        CommandAction vendor = Vendor();
        FileDeleteAction shared = Leftover(@"C:\Program Files\Common Files\SomeApp");
        OperationPlan source = Plan(restore, vendor, shared);

        OperationPlan blockedFirst = PlanSelection.Subset(
            source,
            [Required(restore), Required(vendor), Blocked(shared), Optional(shared, included: true)]);

        OperationPlan blockedLast = PlanSelection.Subset(
            source,
            [Required(restore), Required(vendor), Optional(shared, included: true), Blocked(shared)]);

        Assert.Equal(new PlannedAction[] { restore, vendor }, blockedFirst.Actions);
        Assert.DoesNotContain(blockedFirst.Actions, a => ReferenceEquals(a, shared));
        Assert.Equal(blockedFirst.ComputeHash(), blockedLast.ComputeHash());
    }

    [Fact]
    public void A_blocked_row_dominates_a_required_alias_too()
    {
        CommandAction vendor = Vendor();
        FileDeleteAction shared = Leftover(@"C:\Program Files\Common Files\SomeApp");
        OperationPlan source = Plan(vendor, shared);

        OperationPlan subset = PlanSelection.Subset(source, [Required(vendor), Blocked(shared), Required(shared)]);

        Assert.Equal(new PlannedAction[] { vendor }, subset.Actions);
    }

    /// <summary>A blocked row may name an action the plan never contained: modules show their WON'T RUN rows
    /// beside the plan even when the planner already left those actions out. A row that can only subtract
    /// cannot smuggle anything in, so it is not treated as foreign.</summary>
    [Fact]
    public void A_blocked_row_for_an_action_outside_the_plan_is_harmless()
    {
        CommandAction vendor = Vendor();
        OperationPlan source = Plan(vendor);
        FileDeleteAction neverPlanned = Leftover(@"C:\Windows\System32\shared");

        OperationPlan subset = PlanSelection.Subset(source, [Required(vendor), Blocked(neverPlanned)]);

        Assert.Equal(new PlannedAction[] { vendor }, subset.Actions);
    }

    // ---- A2: one veto controls one occurrence --------------------------------------------------------------

    /// <summary>
    /// The SAME instance twice in one plan. Reference identity gives no OCCURRENCE identity: a single
    /// membership set retained every occurrence of an included reference, so one unchecked row could not remove
    /// its own execution. Rows now claim occurrences in source order, one each.
    /// </summary>
    [Fact]
    public void Two_occurrences_of_one_action_reference_are_independently_selectable()
    {
        FileDeleteAction repeated = Leftover(@"C:\ProgramData\SomeApp\cache");
        CommandAction vendor = Vendor();
        OperationPlan source = Plan(repeated, vendor, repeated);

        OperationPlan subset = PlanSelection.Subset(
            source,
            [Optional(repeated, included: false), Required(vendor), Optional(repeated, included: true)]);

        // Exactly one of the two executions survives — a reference-matched implementation keeps both or neither…
        Assert.Equal(2, subset.Actions.Count);
        // …and the survivor keeps ITS OWN place in the plan, which the hash is sensitive to.
        Assert.Equal(Plan(vendor, repeated).ComputeHash(), subset.ComputeHash());
        Assert.NotEqual(Plan(repeated, vendor).ComputeHash(), subset.ComputeHash());
    }

    // ---- A3/A5: a row set that does not describe the plan is refused ---------------------------------------

    /// <summary>
    /// The sharpest defect. Rows name only the vendor uninstall; the old contract composed <c>[vendor]</c> — a
    /// destructive removal whose protective prerequisite has silently vanished — and that malformed plan hashes
    /// validly, so the executor authorizes it and no downstream gate can catch it. Refusal is the only safe
    /// answer, and the positive control below shows the refusal is about the missing protective step rather
    /// than a composer that refuses everything.
    /// </summary>
    [Fact]
    public void Subset_refuses_when_the_protective_step_has_no_row_instead_of_composing_without_it()
    {
        CreateRestorePointAction restore = RestorePoint();
        CommandAction vendor = Vendor();
        OperationPlan source = Plan(restore, vendor);

        var refusal = Assert.Throws<ArgumentException>(
            () => PlanSelection.Subset(source, [Required(vendor)]));

        Assert.Equal("rows", refusal.ParamName);
        Assert.Contains("restore.create", refusal.Message, StringComparison.Ordinal);

        OperationPlan composed = PlanSelection.Subset(source, [Required(restore), Required(vendor)]);
        Assert.Equal(new PlannedAction[] { restore, vendor }, composed.Actions);
    }

    /// <summary>
    /// The same defect through mislabelling rather than omission: a caller marks the restore point vetoable and
    /// leaves it unchecked, and the destructive neighbour runs unprotected. <c>IsProtective</c> is type-bound in
    /// Core (only <see cref="CreateRestorePointAction"/> can be it), so a checkbox on such an action is a
    /// composition error the model can catch — "required" must not stay a caller convention.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void A_protective_action_cannot_be_offered_as_a_vetoable_row(bool included)
    {
        CreateRestorePointAction restore = RestorePoint();
        CommandAction vendor = Vendor();
        OperationPlan source = Plan(restore, vendor);

        var refusal = Assert.Throws<ArgumentException>(
            () => PlanSelection.Subset(source, [Optional(restore, included), Required(vendor)]));

        Assert.Contains("restore.create", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>A protective action the ENGINE refuses is a different matter: the row says WON'T RUN, the user
    /// reads it, and the composed plan agrees with the row. That is honest, so it composes.</summary>
    [Fact]
    public void A_blocked_protective_action_still_composes_because_the_user_was_told()
    {
        CreateRestorePointAction restore = RestorePoint();
        CommandAction vendor = Vendor();
        OperationPlan source = Plan(restore, vendor);

        OperationPlan subset = PlanSelection.Subset(
            source,
            [Blocked(restore, "System Restore is disabled on this machine"), Required(vendor)]);

        Assert.Equal(new PlannedAction[] { vendor }, subset.Actions);
    }

    [Fact]
    public void A_row_from_another_plan_cannot_compose_this_one()
    {
        CommandAction vendor = Vendor();
        FileDeleteAction leftover = Leftover(@"C:\ProgramData\SomeApp");
        OperationPlan source = Plan(vendor, leftover);
        FileDeleteAction foreign = Leftover(@"C:\ProgramData\OtherApp");

        var refusal = Assert.Throws<ArgumentException>(() => PlanSelection.Subset(
            source,
            [Required(vendor), Optional(leftover, included: true), Optional(foreign, included: true)]));

        Assert.Contains("OtherApp", refusal.Message, StringComparison.Ordinal);
    }

    /// <summary>Two rows for ONE occurrence say two things about one execution. There is no defensible tie-break
    /// — including one would let a duplicated alias decide — so composition refuses.</summary>
    [Fact]
    public void Two_rows_for_one_occurrence_are_ambiguous_and_refused()
    {
        CommandAction vendor = Vendor();
        FileDeleteAction leftover = Leftover(@"C:\ProgramData\SomeApp");
        OperationPlan source = Plan(vendor, leftover);

        Assert.Throws<ArgumentException>(() => PlanSelection.Subset(
            source,
            [Required(vendor), Optional(leftover, included: true), Optional(leftover, included: false)]));
    }

    [Fact]
    public void A_null_row_is_refused_rather_than_skipped()
    {
        CommandAction vendor = Vendor();
        OperationPlan source = Plan(vendor);

        Assert.Throws<ArgumentException>(
            () => PlanSelection.Subset(source, [Required(vendor), null!]));
    }

    [Fact]
    public void Subset_carries_the_source_plans_identity_fields()
    {
        OperationPlan source = Plan(Vendor());

        OperationPlan subset = PlanSelection.Subset(source, [Required(source.Actions[0])]);

        Assert.Equal(Title, subset.Title);
        Assert.Equal(Module, subset.ModuleName);
        Assert.Equal(PlannedAt, subset.CreatedAtUtc);
    }

    [Fact]
    public void Subset_rejects_null_arguments()
    {
        OperationPlan source = Plan(Vendor());

        Assert.Throws<ArgumentNullException>(() => { _ = PlanSelection.Subset(null!, []); });
        Assert.Throws<ArgumentNullException>(() => { _ = PlanSelection.Subset(source, null!); });
    }

    // ---- A3: the hash is the veto's enforcement ------------------------------------------------------------

    [Fact]
    public void A_proper_subsets_hash_differs_from_the_source_plans()
    {
        CommandAction vendor = Vendor();
        FileDeleteAction dropped = Leftover(@"C:\ProgramData\SomeApp\drop");
        OperationPlan source = Plan(vendor, dropped);

        OperationPlan subset = PlanSelection.Subset(
            source,
            [Required(vendor), Optional(dropped, included: false)]);

        Assert.NotEqual(source.Actions.Count, subset.Actions.Count);
        Assert.NotEqual(source.ComputeHash(), subset.ComputeHash());
    }

    /// <summary>
    /// The TOCTOU contract spelled out (spec §1.1): a consumer that approved the FULL plan's hash and then
    /// handed the subset to the executor is caught by the gate, because the two hashes cannot agree. This is
    /// what makes the veto enforcement rather than trust — and it is the assertion that goes red if the hash
    /// shown to the user is ever computed from a different plan than the one that runs.
    /// </summary>
    [Fact]
    public void Approving_the_full_plan_and_running_the_subset_is_detectable_by_hash()
    {
        CommandAction vendor = Vendor();
        FileDeleteAction dropped = Leftover(@"C:\ProgramData\SomeApp\drop");
        OperationPlan source = Plan(vendor, dropped);

        string hashApprovedByMistake = source.ComputeHash();
        OperationPlan whatWouldActuallyRun = PlanSelection.Subset(
            source,
            [Required(vendor), Optional(dropped, included: false)]);

        Assert.NotEqual(hashApprovedByMistake, whatWouldActuallyRun.ComputeHash());
    }

    [Fact]
    public void The_subsets_hash_is_the_hash_of_exactly_the_surviving_actions_in_plan_order()
    {
        CreateRestorePointAction restore = RestorePoint();
        CommandAction vendor = Vendor();
        FileDeleteAction keep = Leftover(@"C:\ProgramData\SomeApp\keep");
        FileDeleteAction drop = Leftover(@"C:\ProgramData\SomeApp\drop");
        OperationPlan source = Plan(restore, vendor, keep, drop);

        // Deliberately NOT in plan order: if the composed plan followed the rows, the hash the user approves
        // would describe a different execution sequence from the one the preview promised.
        OperationPlan subset = PlanSelection.Subset(
            source,
            [Optional(keep, included: true), Optional(drop, included: false), Required(vendor), Required(restore)]);

        OperationPlan whatTheUserWasShown = Plan(restore, vendor, keep);
        Assert.Equal(whatTheUserWasShown.ComputeHash(), subset.ComputeHash());

        // …and the same set in the view's order is a DIFFERENT plan, which is why order is not the view's to pick.
        Assert.NotEqual(Plan(keep, restore, vendor).ComputeHash(), subset.ComputeHash());
    }

    [Fact]
    public void The_same_selection_hashes_the_same_every_time_it_is_composed()
    {
        CommandAction vendor = Vendor();
        FileDeleteAction keep = Leftover(@"C:\ProgramData\SomeApp\keep");
        FileDeleteAction drop = Leftover(@"C:\ProgramData\SomeApp\drop");
        OperationPlan source = Plan(vendor, keep, drop);

        string first = PlanSelection.Subset(
            source,
            [Required(vendor), Optional(keep, included: true), Optional(drop, included: false)]).ComputeHash();
        string second = PlanSelection.Subset(
            source,
            [Required(vendor), Optional(keep, included: true), Optional(drop, included: false)]).ComputeHash();

        Assert.Equal(first, second);
    }

    [Fact]
    public void A_selection_that_vetoes_nothing_hashes_the_same_as_the_source()
    {
        CommandAction vendor = Vendor();
        FileDeleteAction leftover = Leftover(@"C:\ProgramData\SomeApp");
        OperationPlan source = Plan(vendor, leftover);

        OperationPlan subset = PlanSelection.Subset(
            source,
            [Required(vendor), Optional(leftover, included: true)]);

        Assert.Equal(source.ComputeHash(), subset.ComputeHash());
        Assert.NotSame(source, subset);
    }

    /// <summary>
    /// STRENGTHENED: the previous version recomposed a subset after the edit and compared two hash strings,
    /// which proves the hash moves but not that an ALREADY APPROVED subset is dead. This states it at the
    /// boundary that enforces it — the user approves a subset, moves one checkbox, and the plan that would now
    /// run is refused against the hash they approved.
    /// </summary>
    [Fact]
    public void An_already_approved_subset_is_invalidated_when_a_row_changes()
    {
        CommandAction vendor = Vendor();
        FileDeleteAction leftover = Leftover(@"C:\ProgramData\SomeApp");
        OperationPlan source = Plan(vendor, leftover);
        PlanRow row = Optional(leftover, included: true);
        var gate = new AllowEverythingGate();

        OperationPlan approved = PlanSelection.Subset(source, [Required(vendor), row]);
        string approvedHash = approved.ComputeHash();
        Assert.True(ExecutionAuthorizer.Authorize(approved, approvedHash, gate).Authorized);

        row.IsIncluded = false;
        OperationPlan whatWouldRunNow = PlanSelection.Subset(source, [Required(vendor), row]);

        Assert.NotEqual(approvedHash, whatWouldRunNow.ComputeHash());
        ExecutionAuthorization refused = ExecutionAuthorizer.Authorize(whatWouldRunNow, approvedHash, gate);
        Assert.False(refused.Authorized);
        Assert.Contains("TOCTOU", refused.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Approves every action: this test is about approval FRESHNESS, not protected-path policy, and a
    /// real gate would make the assertion depend on rules that have nothing to do with selection.</summary>
    private sealed class AllowEverythingGate : ISafetyGate
    {
        public SafetyVerdict Evaluate(PlannedAction action) => SafetyVerdict.Allow();

        public PlanValidationResult Validate(OperationPlan plan)
            => new(true, plan.Actions.Select(a => new ActionVerdict(a, SafetyVerdict.Allow())).ToArray());
    }
}
