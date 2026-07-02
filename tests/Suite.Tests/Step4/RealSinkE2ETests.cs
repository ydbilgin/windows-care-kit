using Microsoft.Win32;
using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Tests.TestInfra;
using Xunit;
using CoreHive = WindowsCareKit.Core.Planning.RegistryHive;

namespace WindowsCareKit.Tests.Step4;

/// <summary>
/// Step 4 Tier A — host-safe end-to-end coverage of the destructive pipeline through the PRODUCTION gate
/// (<see cref="WindowsCareKit.Core.Safety.SafetyGate"/> over
/// <see cref="WindowsCareKit.Core.Safety.ProtectedResources.ForCurrentSystem"/> +
/// <see cref="WindowsCareKit.Win32.Win32PathCanonicalizer"/>) and the REAL adapters via
/// <see cref="RealExecutorFixture"/>. No prior test runs this combination: the existing 462 exercise the
/// adapters directly or use a fake gate/canonicalizer. Every target is a GUID temp file/dir or an
/// <c>HKCU\Software\WindowsCareKit.Tests\&lt;guid&gt;</c> key — same blast radius as the existing host-safe
/// tests, so these are plain <c>[Fact]</c> (always-on, CI included). Nothing destructive escapes temp/HKCU.
/// </summary>
public class RealSinkE2ETests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc);

    // ----------------------------------------------------------------------------------------------------
    // A1. File delete E2E (permanent) — real gate authorize → re-gate → real recycle-bin adapter → file gone.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A1_FileDelete_through_the_real_gate_and_adapter_removes_a_temp_file_permanently()
    {
        using var fx = new RealExecutorFixture();
        string target = fx.Workspace.WriteFile("junk/leftover.tmp", "delete me");

        // Pre-existence guard: fail loud if a prior crashed run left this path behind under a different name.
        // (The workspace generates a fresh GUID root per fixture, so a pre-existing path here means the fixture
        // itself is broken — surface it immediately rather than letting the test silently operate on stale state.)
        Assert.True(File.Exists(target), "workspace WriteFile must have created the temp file before the test begins");

        var action = new FileDeleteAction
        {
            Path = target,
            ToRecycleBin = false,                 // permanent delete (opt out of the recycle bin)
            Undo = UndoCapability.None,
            Risk = RiskLevel.Low,
            Description = "delete temp leftover",
            Reason = "host-safe E2E",
        };
        var plan = new OperationPlan("t", "step4", new[] { action }, T0);

        ExecutionReport report;
        try
        {
            report = fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());
            // Assert the real adapter removed the file BEFORE the finally's best-effort cleanup runs,
            // so this assertion still catches a silent fail-to-delete regression (cleanup would otherwise
            // delete the file first and mask it).
            Assert.False(File.Exists(target), "the real adapter must have permanently removed the file");
        }
        finally
        {
            // Guarantee cleanup even if an assertion above or the executor call throws: the temp file
            // lives inside the fixture workspace so the workspace Dispose already covers it, but an
            // explicit best-effort delete here makes the cleanup intent explicit and handles any edge
            // case where the adapter left the file in place.
            try { if (File.Exists(target)) File.Delete(target); } catch { /* best-effort */ }
        }

        Assert.True(report.Authorized, string.Join(",", report.Results.Select(r => r.Detail)));
        ActionResult result = Assert.Single(report.Results);
        Assert.Equal(ActionStatus.Done, result.Status);   // file-gone asserted inside the try, above

        string log = string.Join("\n", fx.LogLines());
        Assert.Contains("plan.start", log);
        Assert.Contains("action.done", log);
        Assert.Contains("plan.done", log);
    }

    // ----------------------------------------------------------------------------------------------------
    // A2. Copy + RestoreMerge round-trip + SHA-256 == backup_integrity record. Adapter+integrity round-trip
    // only (there is no product restore-orchestrator; none is claimed here).
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A2_Copy_then_RestoreMerge_round_trips_and_the_restored_sha256_matches_the_integrity_record()
    {
        using var fx = new RealExecutorFixture();
        const string original = "the original config bytes";
        string src = fx.Workspace.WriteFile("src/app.cfg", original);
        string payload = fx.Workspace.Combine("payload", "app.cfg");
        string dest = fx.Workspace.Combine("live", "app.cfg");

        // 1) Copy src → payload through the real gate+adapter.
        var copy = new CopyAction { Source = src, Destination = payload, Description = "backup copy", Reason = "E2E" };

        // 2) Seed the live destination with corrupted content (the merge must back THIS up to a .bak).
        fx.Workspace.WriteFile("live/app.cfg", "CORRUPTED");

        // 3) RestoreMerge payload → dest through the real gate+adapter (writes a .bak first).
        var restore = new RestoreMergeAction { Source = payload, Destination = dest, CreateBak = true, Description = "restore", Reason = "E2E" };

        var plan = new OperationPlan("t", "step4", new PlannedAction[] { copy, restore }, T0);
        ExecutionReport report = fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());

        Assert.True(report.Authorized, string.Join(",", report.Results.Select(r => r.Detail)));
        Assert.True(report.Results.All(r => r.Status == ActionStatus.Done),
            string.Join(",", report.Results.Select(r => $"{r.Kind}:{r.Status}:{r.Detail}")));

        // Payload received the original bytes; the destination was restored from the payload.
        Assert.Equal(original, File.ReadAllText(payload));
        Assert.Equal(original, File.ReadAllText(dest));

        // A timestamped .bak holds the pre-restore (corrupted) content.
        string? bak = Directory.GetFiles(Path.GetDirectoryName(dest)!, "app.cfg.bak.*").SingleOrDefault();
        Assert.NotNull(bak);
        Assert.Equal("CORRUPTED", File.ReadAllText(bak!));

        // The restored bytes' SHA-256 equals the integrity record built over the restored destination.
        string payloadRoot = fx.Workspace.Combine("live");
        var copied = new CopySkipReport(new[] { new CopyFileOutcome("restored", src, dest, true, null, "ok") });
        IReadOnlyList<BackupIntegrity> rows = new BackupIntegrityWriter()
            .BuildIntegrity(copied, payloadRoot, new PhysicalFileSystem(), new Sha256Hasher(), new FakeClock(T0));

        BackupIntegrity restoredRow = rows.Single(r => r.DestinationRelativePath == "app.cfg");
        Assert.Equal(new Sha256Hasher().ComputeFileSha256(dest), restoredRow.Sha256);
    }

    // ----------------------------------------------------------------------------------------------------
    // A3. Registry HKCU value+key delete E2E + a real .reg backup written by the real RegistryDeleteAdapter.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A3_Registry_value_then_key_delete_through_the_real_gate_writes_a_real_reg_backup_and_removes_the_target()
    {
        using var fx = new RealExecutorFixture();
        string guid = Guid.NewGuid().ToString("N");
        string subKey = $@"Software\WindowsCareKit.Tests\{guid}";

        // Pre-existence guard: if a prior crashed run left this exact GUID key behind, the test would operate
        // on stale state. A GUID collision is astronomically unlikely; if it happens, fail loud immediately.
        using (RegistryKey? preExisting = Registry.CurrentUser.OpenSubKey(subKey))
        {
            // xunit 2.9.x: Assert.Null takes only the object (no message overload); use Assert.False for a message.
            Assert.False(preExisting is not null,
                $"HKCU\\{subKey} already exists before the test creates it — leftover from a crashed run. " +
                "Delete it manually and re-run.");
        }

        // Provision a real HKCU key with a value and a child key.
        using (RegistryKey created = Registry.CurrentUser.CreateSubKey(subKey, writable: true))
        {
            created.SetValue("Marker", "to-be-deleted");
            using RegistryKey child = created.CreateSubKey("Child");
            child.SetValue("ChildVal", 1);
        }

        var valueDelete = new RegistryDeleteAction
        {
            Hive = CoreHive.CurrentUser, SubKeyPath = subKey, ValueName = "Marker",
            Description = "delete value", Reason = "E2E",
        };
        var keyDelete = new RegistryDeleteAction
        {
            Hive = CoreHive.CurrentUser, SubKeyPath = subKey,
            Description = "delete key", Reason = "E2E",
        };

        try
        {
            var plan = new OperationPlan("t", "step4", new PlannedAction[] { valueDelete, keyDelete }, T0);
            ExecutionReport report = fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());

            Assert.True(report.Authorized, string.Join(",", report.Results.Select(r => r.Detail)));
            Assert.True(report.Results.All(r => r.Status == ActionStatus.Done),
                string.Join(",", report.Results.Select(r => $"{r.Kind}:{r.Status}:{r.Detail}")));

            // The whole key (value + child) is gone.
            using RegistryKey? after = Registry.CurrentUser.OpenSubKey(subKey);
            Assert.Null(after);

            // The real adapter exported at least one standard .reg backup before deleting.
            string[] regFiles = Directory.GetFiles(fx.RegBackupDir, "*.reg");
            Assert.NotEmpty(regFiles);
            string anyBackup = File.ReadAllText(regFiles[0]);
            Assert.StartsWith("Windows Registry Editor Version 5.00", anyBackup);
        }
        finally
        {
            // Guarantee cleanup even if an assertion fails or the executor throws: delete the test subkey
            // so no leftover state affects a subsequent run.
            try { Registry.CurrentUser.DeleteSubKeyTree($@"Software\WindowsCareKit.Tests\{guid}", throwOnMissingSubKey: false); }
            catch { /* best-effort */ }
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // A4. Junction reparse BOUNDARY: the real gate ALLOWS the resolved (temp) target, but the delete adapter
    // still REFUSES a reparse-point leaf at the destructive boundary → Failed, and the real target survives.
    // Defense-in-depth interaction (gate-allows vs adapter-refuses); not a repeat of the adapter-only test.
    // ----------------------------------------------------------------------------------------------------

    [FactRequiresJunction]
    public void A4_FileDelete_of_a_junction_is_allowed_by_the_gate_but_refused_by_the_adapter_leaving_the_target_intact()
    {
        using var fx = new RealExecutorFixture();
        string realTarget = fx.Workspace.Combine("realTarget");
        Directory.CreateDirectory(realTarget);
        string keepFile = Path.Combine(realTarget, "keep.txt");
        File.WriteAllText(keepFile, "do not lose me");

        string junction = fx.Workspace.Combine("link");
        Assert.True(JunctionHelper.TryCreateJunction(junction, realTarget)); // gated by [FactRequiresJunction]

        try
        {
            // The gate, using the real canonicalizer, resolves the junction to its temp target (allowed). The
            // adapter then refuses the reparse-point leaf at the destructive boundary (fail-closed).
            var action = new FileDeleteAction
            {
                Path = junction, ToRecycleBin = false, Undo = UndoCapability.None, Risk = RiskLevel.Low,
                Description = "delete junction leaf", Reason = "E2E boundary",
            };
            var plan = new OperationPlan("t", "step4", new[] { action }, T0);

            ExecutionReport report = fx.Executor.ExecuteWithReport(plan, plan.ComputeHash());

            // Authorized (gate allowed the resolved path) but the action FAILED at the adapter's reparse re-check.
            Assert.True(report.Authorized, string.Join(",", report.Results.Select(r => r.Detail)));
            ActionResult result = Assert.Single(report.Results);
            Assert.Equal(ActionStatus.Failed, result.Status);
            Assert.Contains("reparse point", result.Detail, StringComparison.OrdinalIgnoreCase);

            // The real target — and its file — are untouched: nothing was unlinked through the junction.
            Assert.True(Directory.Exists(realTarget));
            Assert.True(File.Exists(keepFile));
            Assert.Equal("do not lose me", File.ReadAllText(keepFile));
        }
        finally
        {
            // Unlink the junction (non-recursive) so the workspace teardown does not recurse into the target.
            JunctionHelper.CleanupWithJunction(fx.Workspace.Root, junction);
        }
    }

    // ----------------------------------------------------------------------------------------------------
    // A5. BackupRunner real-copy integrity bridge: a hand-built BackupPlanResult (one real CopyAction) driven
    // through BackupRunner + BackupExecutorAdapter(realGatedExecutor) + BackupIntegrityWriter + Sha256Hasher.
    // Proves the Step3→Step4 bridge: backup_integrity.json + REPORT.md + the real copy + matching SHA-256.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void A5_BackupRunner_with_the_real_executor_copies_for_real_and_the_integrity_sha_matches_the_copied_bytes()
    {
        using var fx = new RealExecutorFixture();
        const string content = "backup payload bytes";
        string src = fx.Workspace.WriteFile("src/note.txt", content);
        string payloadRoot = fx.Workspace.Combine("payload");
        string dest = Path.Combine(payloadRoot, "note.txt");

        var copy = new CopyAction { Id = "note", Source = src, Destination = dest, Description = "copy", Reason = "E2E" };
        var plan = new OperationPlan("Back up", "backup", new[] { copy }, T0);
        var planResult = new BackupPlanResult(plan,
            Array.Empty<BackupEntry>(), Array.Empty<BackupSkip>(), Array.Empty<BackupEntry>());

        // The REAL bridge: BackupRunner over BackupExecutorAdapter(real GatedExecutor) + real fs/hasher/clock.
        var runner = new BackupRunner(
            new BackupExecutorAdapter(fx.Executor),
            new BackupIntegrityWriter(),
            new BackupReportWriter(new LogRedactor(null, null)),
            fx.Gate,
            new PhysicalFileSystem(),
            new Sha256Hasher(),
            new FakeClock(T0));

        BackupRunResult result = runner.Run(planResult, plan.ComputeHash(), payloadRoot);

        Assert.True(result.Authorized);
        Assert.Single(result.CopyReport.Copied);

        // The real copy actually landed the bytes at the destination.
        Assert.True(File.Exists(dest));
        Assert.Equal(content, File.ReadAllText(dest));

        // The integrity manifest + the human report were written into the payload root.
        Assert.True(File.Exists(Path.Combine(payloadRoot, BackupIntegrityFiles.Integrity)));
        Assert.True(File.Exists(Path.Combine(payloadRoot, BackupReportFiles.Report)));

        // The integrity SHA-256 matches the real copied bytes.
        BackupIntegrity row = result.Integrity.Single(r => r.DestinationRelativePath == "note.txt");
        Assert.Equal(new Sha256Hasher().ComputeFileSha256(dest), row.Sha256);
    }
}
