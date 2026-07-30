using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
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

    [Fact]
    public void Non_vetoable_rows_are_always_retained_even_though_they_are_not_included()
    {
        CreateRestorePointAction restore = RestorePoint();
        CommandAction vendor = Vendor();
        FileDeleteAction leftover = Leftover(@"C:\ProgramData\SomeApp");
        OperationPlan source = Plan(restore, vendor, leftover);

        PlanRow restoreRow = Required(restore);
        PlanRow vendorRow = Required(vendor);
        Assert.False(restoreRow.IsIncluded); // display-only rows never carry an inclusion value
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
    public void A_row_that_names_no_action_is_ignored()
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

        OperationPlan subset = PlanSelection.Subset(
            source,
            [Required(vendor), legacyLiteral, Optional(leftover, included: true)]);

        Assert.Equal(new PlannedAction[] { vendor, leftover }, subset.Actions);
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

    [Fact]
    public void Rows_that_describe_nothing_in_the_plan_produce_an_empty_plan_rather_than_the_whole_one()
    {
        OperationPlan source = Plan(Vendor(), Leftover(@"C:\ProgramData\SomeApp"));

        OperationPlan subset = PlanSelection.Subset(source, []);

        Assert.True(subset.IsEmpty);
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

    [Fact]
    public void Changing_one_row_changes_the_hash_the_user_will_be_asked_to_approve()
    {
        CommandAction vendor = Vendor();
        FileDeleteAction leftover = Leftover(@"C:\ProgramData\SomeApp");
        OperationPlan source = Plan(vendor, leftover);
        PlanRow row = Optional(leftover, included: true);

        string before = PlanSelection.Subset(source, [Required(vendor), row]).ComputeHash();
        row.IsIncluded = false;
        string after = PlanSelection.Subset(source, [Required(vendor), row]).ComputeHash();

        Assert.NotEqual(before, after);
    }
}
