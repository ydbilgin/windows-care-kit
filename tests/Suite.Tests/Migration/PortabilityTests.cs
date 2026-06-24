using WindowsCareKit.Core.Modules.Migration;
using Xunit;

namespace WindowsCareKit.Tests.Migration;

/// <summary>D: portability classification + badge fail-safe (machine-locked never shown as "works").</summary>
public class PortabilityTests
{
    [Fact]
    public void Profile_relative_no_preconditions_is_a_clean_works_badge()
    {
        var b = PortabilityBadge.Compute(PortabilityClass.ProfileRelative, hasPreconditions: false);
        Assert.Equal(BadgeKind.PortableClean, b.Kind);
        Assert.True(b.MayClaimWorks);
    }

    [Fact]
    public void Profile_relative_with_preconditions_is_works_with_a_step()
    {
        var b = PortabilityBadge.Compute(PortabilityClass.ProfileRelative, hasPreconditions: true);
        Assert.Equal(BadgeKind.PortableWithStep, b.Kind);
        Assert.True(b.MayClaimWorks);
    }

    [Fact]
    public void Machine_locked_never_claims_works()
    {
        var b = PortabilityBadge.Compute(PortabilityClass.MachineLocked, hasPreconditions: false);
        Assert.Equal(BadgeKind.MachineLocked, b.Kind);
        Assert.False(b.MayClaimWorks);
    }

    [Fact]
    public void Partial_never_claims_works()
        => Assert.False(PortabilityBadge.Compute(PortabilityClass.Partial, false).MayClaimWorks);

    [Fact]
    public void Observed_machine_lock_signal_overrides_an_optimistic_declaration()
    {
        MachineContextClassification c = MachineContextProbe.Classify(
            PortabilityClass.ProfileRelative, MachineLockReason.Dpapi);

        Assert.Equal(PortabilityClass.MachineLocked, c.PortabilityClass);
        Assert.Equal(MachineLockReason.Dpapi, c.Reasons);
        Assert.False(PortabilityBadge.Compute(c.PortabilityClass, false).MayClaimWorks);
    }

    [Fact]
    public void No_observed_signal_trusts_the_profile_relative_declaration()
    {
        var c = MachineContextProbe.Classify(PortabilityClass.ProfileRelative, MachineLockReason.None);
        Assert.Equal(PortabilityClass.ProfileRelative, c.PortabilityClass);
        Assert.Equal(MachineLockReason.None, c.Reasons);
    }

    [Theory]
    [InlineData(MachineLockReason.Sid)]
    [InlineData(MachineLockReason.Hardware)]
    [InlineData(MachineLockReason.AbsolutePath)]
    public void Any_lock_reason_forces_machine_locked(MachineLockReason reason)
        => Assert.Equal(PortabilityClass.MachineLocked,
            MachineContextProbe.Classify(PortabilityClass.ProfileRelative, reason).PortabilityClass);

    // --- B-1: declared-secret fail-safe is a first-class input to the pure function (decision §3A) ---

    [Fact]
    public void Profile_relative_with_excluded_secret_can_never_claim_works()
    {
        // Non-vacuous: revert the B-1 branch and this fails (ProfileRelative would render ✅ MayClaimWorks=true).
        var b = PortabilityBadge.Compute(PortabilityClass.ProfileRelative, hasPreconditions: false, hasExcludedSecret: true);
        Assert.Equal(BadgeKind.Partial, b.Kind);
        Assert.False(b.MayClaimWorks);
    }

    [Fact]
    public void Excluded_secret_overrides_even_the_with_step_path()
    {
        var b = PortabilityBadge.Compute(PortabilityClass.ProfileRelative, hasPreconditions: true, hasExcludedSecret: true);
        Assert.False(b.MayClaimWorks);
    }

    [Fact]
    public void Profile_relative_without_excluded_secret_is_still_a_clean_works_badge()
    {
        // Counter-test (no over-block): the fail-safe must NOT downgrade a genuinely clean profile-relative item.
        var b = PortabilityBadge.Compute(PortabilityClass.ProfileRelative, hasPreconditions: false, hasExcludedSecret: false);
        Assert.Equal(BadgeKind.PortableClean, b.Kind);
        Assert.True(b.MayClaimWorks);
    }

    [Fact]
    public void Meta_overload_reads_the_excluded_secret_signal()
    {
        var clean = new MigrationItemMeta("r", "r#0", PortabilityClass.ProfileRelative,
            RestoreStrategy.MergeAfterInstall, RestorePhase.ConfigWrite, System.Array.Empty<string>());
        var secret = clean with { HasExcludedSecret = true };

        Assert.True(PortabilityBadge.Compute(clean).MayClaimWorks);
        Assert.False(PortabilityBadge.Compute(secret).MayClaimWorks);
    }

    [Fact]
    public void Unknown_named_synthetic_dpapi_blob_can_never_claim_works()
    {
        byte[] syntheticUnknownNamedBlob =
        [
            0x01, 0x00, 0x00, 0x00,
            0xD0, 0x8C, 0x9D, 0xDF, 0x01, 0x15, 0xD1, 0x11,
            0x8C, 0x7A, 0x00, 0xC0, 0x4F, 0xC2, 0x97, 0xEB,
        ];
        ContentSignature signature = ContentSignatureClassifier.Classify(syntheticUnknownNamedBlob);
        var meta = new MigrationItemMeta("trusted.recipe", "trusted.recipe#0", PortabilityClass.ProfileRelative,
            RestoreStrategy.MergeAfterInstall, RestorePhase.ConfigWrite, Array.Empty<string>())
        {
            HasExcludedSecret = false,
            HasMachineBoundContent = signature.HasMachineBoundContent,
        };

        PortabilityBadgeResult badge = PortabilityBadge.Compute(meta);

        Assert.True(signature.HasDpapiBlob);
        Assert.Equal(BadgeKind.MachineLocked, badge.Kind);
        Assert.False(badge.MayClaimWorks);
    }

    [Fact]
    public void Back_compat_two_arg_overload_is_unchanged()
    {
        // The legacy (cls, hasPreconditions) overload must behave exactly as before (no secret signal).
        Assert.True(PortabilityBadge.Compute(PortabilityClass.ProfileRelative, false).MayClaimWorks);
        Assert.Equal(BadgeKind.PortableClean,
            PortabilityBadge.Compute(PortabilityClass.ProfileRelative, false).Kind);
    }
}
