using WindowsCareKit.App.Localization;
using WindowsCareKit.App.ViewModels;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Item 2 — the type-to-confirm CEREMONY of the reusable <see cref="ConfirmGateViewModel"/>. Before this
/// suite, <c>TypedMatches</c> / <c>CanApprove</c> on the Irreversible tier had ZERO test references, so the
/// hardest gate the suite has was effectively unverified. These tests FORCE the Irreversible tier (else the
/// ceremony is trivially bypassed — lower tiers approve on open) and prove: Approve stays locked until the
/// localized confirm word is typed, the match is case/space-insensitive, the wrong word never unlocks, lower
/// tiers do NOT require typing, and <see cref="ConfirmGateViewModel.Close"/> resets the typed text so a re-open
/// re-requires it. Host-safe: pure view-model, no OS, no app launch; rows are empty (the ceremony is independent
/// of the row content).
/// </summary>
public class ConfirmGateViewModelTests
{
    private static ConfirmGateViewModel BuildGate(string culture = "tr")
    {
        I18n i18n = TestI18n.Full(culture);
        return new ConfirmGateViewModel(i18n, onApprove: () => { }, onCancel: () => { }, isBusy: () => false);
    }

    [Fact]
    public void Irreversible_keeps_Approve_locked_until_the_confirm_word_is_typed()
    {
        var gate = BuildGate();
        gate.Open(ConfirmTier.Irreversible, "title", "body", Array.Empty<PlanRow>());

        // FORCE the Irreversible tier: Approve is locked on open (the ceremony is in effect).
        Assert.Equal(ConfirmTier.Irreversible, gate.Tier);
        Assert.False(gate.CanApprove);
        Assert.False(gate.TypedMatches);

        // Type the exact localized confirm word → unlocks.
        gate.TypedConfirm = gate.ConfirmWord;
        Assert.True(gate.TypedMatches);
        Assert.True(gate.CanApprove);
    }

    [Fact]
    public void Irreversible_match_is_case_and_space_insensitive()
    {
        var gate = BuildGate();
        gate.Open(ConfirmTier.Irreversible, "t", "b", Array.Empty<PlanRow>());

        // Surround with whitespace and flip case — the trim + CurrentCultureIgnoreCase compare still matches.
        gate.TypedConfirm = "  " + gate.ConfirmWord.ToLowerInvariant() + "  ";
        Assert.True(gate.TypedMatches);
        Assert.True(gate.CanApprove);
    }

    [Fact]
    public void Irreversible_wrong_word_never_unlocks_Approve()
    {
        var gate = BuildGate();
        gate.Open(ConfirmTier.Irreversible, "t", "b", Array.Empty<PlanRow>());

        gate.TypedConfirm = gate.ConfirmWord + "X"; // close but not equal
        Assert.False(gate.TypedMatches);
        Assert.False(gate.CanApprove);

        gate.TypedConfirm = "anything-else";
        Assert.False(gate.TypedMatches);
        Assert.False(gate.CanApprove);
    }

    [Theory]
    [InlineData(ConfirmTier.Reversible)]
    [InlineData(ConfirmTier.Medium)]
    public void Lower_tiers_allow_Approve_on_open_without_typing(ConfirmTier tier)
    {
        var gate = BuildGate();
        gate.Open(tier, "t", "b", Array.Empty<PlanRow>());

        // No typing required for Reversible / Medium — Approve is enabled the moment the gate is open.
        Assert.Equal(tier, gate.Tier);
        Assert.Equal(string.Empty, gate.TypedConfirm);
        Assert.True(gate.CanApprove);
    }

    [Fact]
    public void Close_resets_typed_confirm_so_a_reopen_re_requires_typing()
    {
        var gate = BuildGate();
        gate.Open(ConfirmTier.Irreversible, "t", "b", Array.Empty<PlanRow>());
        gate.TypedConfirm = gate.ConfirmWord;
        Assert.True(gate.CanApprove);

        gate.Close();
        Assert.Equal(string.Empty, gate.TypedConfirm); // reset on close
        Assert.False(gate.CanApprove);                 // gate closed → cannot approve

        // Re-open at the Irreversible tier: the ceremony is required AGAIN (typed text did not persist).
        gate.Open(ConfirmTier.Irreversible, "t", "b", Array.Empty<PlanRow>());
        Assert.Equal(string.Empty, gate.TypedConfirm);
        Assert.False(gate.CanApprove);
    }

    [Fact]
    public void Open_at_a_lower_tier_after_a_typed_irreversible_does_not_leak_the_typed_text()
    {
        var gate = BuildGate();
        gate.Open(ConfirmTier.Irreversible, "t", "b", Array.Empty<PlanRow>());
        gate.TypedConfirm = gate.ConfirmWord;

        // Re-open at Reversible: Open() clears TypedConfirm, so the lower tier is approvable on its own merit,
        // not because stale Irreversible text remained.
        gate.Open(ConfirmTier.Reversible, "t", "b", Array.Empty<PlanRow>());
        Assert.Equal(string.Empty, gate.TypedConfirm);
        Assert.True(gate.CanApprove);
    }
}
