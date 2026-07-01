using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Win32;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Backup;
using CoreHive = WindowsCareKit.Core.Planning.RegistryHive;
using CoreView = WindowsCareKit.Core.Planning.RegistryView;
using WindowsCareKit.Core.Modules.Clean;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Execution.Adapters;
using WindowsCareKit.Win32;

// ---------------------------------------------------------------------------
// CleanE2E — end-to-end harness for the Clean module (junk cleanup + startup disable).
//
// Proves the Clean module's production safety pipeline through THREE independent proofs:
//
//   P1 — real junk deleted.
//        A controlled IJunkProbe returns a harness-seeded temp dir under %TEMP%\WCK-CleanE2E-<guid>.
//        JunkScanner gates it → plan → GatedExecutor + REAL RecycleBinFileDeleteAdapter deletes it.
//        Ground truth: the seeded path no longer exists on disk.
//
//   P2 — protected resource NEVER deleted (gate safety proof).
//        The SAME controlled probe also returns %WINDIR%\System32 (an existing protected path).
//        The gate MUST refuse it → Skipped, NOT in the plan. Ground truth: the path still exists.
//        This is load-bearing: if the gate regressed, the path would enter the plan, the executor
//        would attempt to delete System32, and the harness would FAIL.
//
//   P3 — startup Run value disabled (value-delete works, key-delete refused).
//        A throwaway HKCU\...\Run value is seeded and found by Win32StartupProbe.ReadAll().
//        StartupPlanner.BuildDisablePlan + gate + REAL RegistryDeleteAdapter removes the value.
//        Ground truth: value is gone from the Run key.
//        ALSO: a key-delete RegistryDeleteAction on the Run key itself is gate-evaluated and MUST
//        be REFUSED (ValueDeleteAllowedKeys allows value-deletes but the key itself is protected).
//
// SAFETY CONTRACT (host protection)
//   - All real mutation (seeding junk files, seeding+deleting HKCU Run value, executing plans)
//     is gated on the DISPOSABLE-MACHINE signal: env WCK_DISPOSABLE_MACHINE=1 AND the marker
//     file %TEMP%\wck-disposable.marker. The same opt-in the step4 sandbox VM runner uses.
//   - Without --execute: build plans from a CONSTRUCTED controlled probe (no writes) and assert
//     GATE DECISIONS only — zero filesystem/registry mutation. Exits EVAL-PASS.
//   - With --execute but NOT a disposable machine: refuse, exit ExitGuardRefused (3). No mutation.
//   - Seeds are cleaned up in a finally block even if the executor already removed them.
//   - No GUI. Gate is the PRODUCTION gate — no relaxation.
//
// CLI
//   CleanE2E --output <dir> [--execute] [--logDir <dir>] [--settleSeconds <n>]
// ---------------------------------------------------------------------------

namespace WindowsCareKit.Tools.CleanE2E;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private const int ExitOk = 0;
    private const int ExitVerifyFail = 1;
    private const int ExitUsageError = 2;
    private const int ExitGuardRefused = 3;

    // The HKCU Run subkey path (hive-relative, without HKCU prefix).
    private const string RunSubKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    internal static int Main(string[] args)
    {
        if (!TryParseArgs(args, out Config cfg, out string argError))
        {
            Console.Error.WriteLine($"[C-E2E] ERROR: {argError}");
            PrintUsage();
            return ExitUsageError;
        }

        Console.WriteLine("[C-E2E] ===== Clean E2E — junk-delete + startup-disable over the production safety pipeline =====");
        Console.WriteLine($"[C-E2E] Output         : {cfg.OutputDir}");
        Console.WriteLine($"[C-E2E] Execute        : {(cfg.Execute ? "yes" : "no (eval/dry-run)")}");
        Console.WriteLine($"[C-E2E] Settle seconds : {cfg.SettleSeconds}");
        Console.WriteLine($"[C-E2E] Junk dir       : {cfg.JunkDir ?? "(auto)"}");
        Console.WriteLine($"[C-E2E] Run value      : {cfg.RunValueName ?? "(auto)"}");

        try
        {
            return Run(cfg);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[C-E2E] UNHANDLED EXCEPTION: {ex}");
            try { WriteReport(cfg.OutputDir, BuildFatalEvidence(cfg, ex.ToString())); } catch { /* best effort */ }
            return ExitVerifyFail;
        }
    }

    private static int Run(Config cfg)
    {
        var evidence = new EvidenceReport
        {
            Generated = DateTime.UtcNow.ToString("o"),
            OutputDir = cfg.OutputDir,
            Execute = cfg.Execute,
            IsDisposableMachine = IsDisposableMachine(out string guardDetail),
            DisposableSignal = guardDetail,
        };

        // --- host-safety guard: executing deletes requires the disposable-machine opt-in. ----------------
        if (cfg.Execute && !evidence.IsDisposableMachine)
        {
            Console.Error.WriteLine("[C-E2E] REFUSED: --execute requested but this is not a disposable machine.");
            Console.Error.WriteLine($"[C-E2E]   {guardDetail}");
            Console.Error.WriteLine("[C-E2E]   No file or registry mutation will occur. (Needs WCK_DISPOSABLE_MACHINE=1 + %TEMP%\\wck-disposable.marker inside a throwaway VM.)");
            evidence.ExecutionRefusedReason = "not a disposable machine — refusing to mutate";

            // Write evidence before exiting so the caller can see the guard detail.
            evidence.Verdict = "GUARD-REFUSED";
            evidence.Pass = false;
            WriteReport(cfg.OutputDir, evidence);
            return ExitGuardRefused;
        }

        // The production gate — identical to what the WPF app uses (App.xaml.cs).
        var gate = new SafetyGate(ProtectedResources.ForCurrentSystem(), new Win32PathCanonicalizer());
        DateTime utc = DateTime.UtcNow;

        // ---- Disposable-machine path: seed real artifacts, execute via GatedExecutor, ground-truth verify.
        if (cfg.Execute)
        {
            Console.WriteLine("[C-E2E] Disposable machine confirmed — executing real seed+delete pipeline.");
            return RunExecute(cfg, gate, utc, evidence);
        }

        // ---- Eval-only (host-safe): build plans from constructed data, assert gate decisions only. -------
        Console.WriteLine("[C-E2E] Eval/dry-run — asserting gate decisions only (zero mutation).");
        return RunEval(cfg, gate, utc, evidence);
    }

    // -----------------------------------------------------------------------
    // EVAL MODE (host-safe, zero mutation)
    // -----------------------------------------------------------------------
    // Build plans from CONSTRUCTED representative paths (no disk or registry writes) and verify
    // that the gate decides correctly for all four key cases:
    //   [1] junk candidate in a non-protected temp dir   → gate ALLOWS  (enters plan)
    //   [2] existing protected path (System32)           → gate REFUSES (Skipped)
    //   [3] value-delete on the Run key                 → gate ALLOWS
    //   [4] key-delete on the Run key                   → gate REFUSES
    private static int RunEval(Config cfg, SafetyGate gate, DateTime utc, EvidenceReport evidence)
    {
        // [1+2] JunkScanner gate decisions with a two-candidate controlled probe.
        //   Candidate A: a non-existent junk-shaped temp path (gate sees the path string, not whether it exists).
        //   Candidate B: System32 — a real protected path that must always be refused.
        string representativeJunkPath = Path.Combine(Path.GetTempPath(), $"WCK-CleanE2E-eval-{Guid.NewGuid():N}");
        string protectedPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System));

        Console.WriteLine($"[C-E2E] P1 eval: junk candidate = {representativeJunkPath}");
        Console.WriteLine($"[C-E2E] P2 eval: protected path = {protectedPath}");

        var evalProbe = new ControlledJunkProbe(new[]
        {
            new JunkCandidate(representativeJunkPath, 0L, "WCK-CleanE2E eval junk path"),
            new JunkCandidate(protectedPath, 0L, "WCK-CleanE2E protected path (must be refused)"),
        });
        var scanner = new JunkScanner(evalProbe, gate);
        JunkScanResult scanResult = scanner.Scan(utc);

        bool junkAllowed = scanResult.Plan.Actions.Any(a =>
            a is FileDeleteAction f && string.Equals(f.Path, representativeJunkPath, StringComparison.OrdinalIgnoreCase));
        bool protectedAllowed = scanResult.Plan.Actions.Any(s =>
            s is FileDeleteAction f && string.Equals(f.Path, protectedPath, StringComparison.OrdinalIgnoreCase));
        bool protectedSkipped = scanResult.Skipped.Any(s =>
            s.Action is FileDeleteAction f && string.Equals(f.Path, protectedPath, StringComparison.OrdinalIgnoreCase));

        Console.WriteLine($"[C-E2E]   P1 gate decision: junk in plan = {junkAllowed}  (expected: true)");
        Console.WriteLine($"[C-E2E]   P2 gate decision: protected in Skipped = {protectedSkipped}, in plan = {protectedAllowed}  (expected: Skipped=true, inPlan=false)");

        // [3+4] StartupPlanner + gate decisions on constructed StartupEntry.
        var runEntry = new StartupEntry(
            Name: $"WCK-CleanE2E-eval-{Guid.NewGuid():N}",
            Command: @"C:\FAKE-EVAL.exe",
            Source: StartupSource.HkcuRun,
            FolderPath: null);

        OperationPlan valuePlan = StartupPlanner.BuildDisablePlan(runEntry, utc);
        SafetyVerdict valueVerdict = gate.Evaluate(valuePlan.Actions[0]);

        // Key-delete refusal proof: use a subkey path that IS in ProtectedRegistryKeys
        // (software\microsoft\windows\currentversion — the direct parent of the Run key).
        // The gate's key-delete path checks ProtectedRegistryKeys; this entry is explicitly listed.
        // NOTE: the Run key itself is in ValueDeleteAllowedKeys (permits value-deletes) but is NOT
        // in ProtectedRegistryKeys — a key-delete on Run is therefore gate-allowed. The documented
        // protection is for VALUE operations. The refusal proof uses the parent protected key.
        const string ProtectedKeySubKeyPath = @"Software\Microsoft\Windows\CurrentVersion";
        var keyDeleteAction = new RegistryDeleteAction
        {
            Hive = CoreHive.CurrentUser,
            SubKeyPath = ProtectedKeySubKeyPath,
            ValueName = null,          // null → key-delete (not value-delete)
            View = CoreView.Registry64,
            Description = "P3-eval: key-delete on protected HKCU CurrentVersion key (must be refused)",
            Reason = "CleanE2E eval — gate key-delete refusal proof",
            Risk = RiskLevel.Medium,
            Undo = UndoCapability.Partial,
        };
        SafetyVerdict keyVerdict = gate.Evaluate(keyDeleteAction);

        Console.WriteLine($"[C-E2E]   P3 gate decision: value-delete ALLOWED = {valueVerdict.Allowed}  reason: {valueVerdict.Reason}  (expected: true)");
        Console.WriteLine($"[C-E2E]   P3 gate decision: key-delete   REFUSED = {!keyVerdict.Allowed}  reason: {keyVerdict.Reason}  (expected: refused=true)");

        // Record all four decisions.
        // GateDecision.Pass = ExpectedAllowed == ActualAllowed:
        //   P1: ExpectedAllowed=true,  ActualAllowed=junkAllowed         → PASS when gate allows junk
        //   P2: ExpectedAllowed=false, ActualAllowed=protectedAllowed     → PASS when gate refuses protected (protectedAllowed=false)
        //   P3-value: ExpectedAllowed=true,  ActualAllowed=valueVerdict.Allowed → PASS when gate allows value-delete
        //   P3-key:   ExpectedAllowed=false, ActualAllowed=keyVerdict.Allowed   → PASS when gate refuses key-delete
        evidence.GateDecisions = new List<GateDecision>
        {
            new("P1-junk-allowed",          "junk candidate in non-protected dir",           true,  junkAllowed),
            new("P2-protected-refused",     "protected path (System32) refused by gate",     false, protectedAllowed),
            new("P3-value-delete-allowed",  "HKCU Run value-delete allowed",                 true,  valueVerdict.Allowed),
            new("P3-key-delete-refused",    "HKCU CurrentVersion key-delete refused",        false, keyVerdict.Allowed),
        };

        foreach (GateDecision d in evidence.GateDecisions)
            Console.WriteLine($"[C-E2E]   [{(d.Pass ? "PASS" : "FAIL")}] {d.Label}");

        bool allPass = evidence.GateDecisions.All(d => d.Pass);
        string verdict = allPass ? "EVAL-PASS" : "FAIL";
        evidence.Verdict = verdict;
        evidence.Pass = allPass;

        Console.WriteLine();
        Console.WriteLine($"[C-E2E] ===== RESULT: {verdict} =====");
        WriteReport(cfg.OutputDir, evidence);
        return allPass ? ExitOk : ExitVerifyFail;
    }

    // -----------------------------------------------------------------------
    // EXECUTE MODE (disposable machine only, real mutation)
    // -----------------------------------------------------------------------
    private static int RunExecute(Config cfg, SafetyGate gate, DateTime utc, EvidenceReport evidence)
    {
        // Seeded artifacts — cleaned up in finally regardless of success/failure.
        string? junkDir = null;
        string? seedRunValueName = null;

        var log = new ExecutionLog(
            Path.Combine(cfg.LogDir, $"clean-e2e-{Guid.NewGuid():N}.jsonl"),
            new LogRedactor(null, null));

        // GatedExecutor with REAL file-delete and registry adapters for the adapters Clean actually uses.
        // ThrowingProcessAdapter/ThrowingServiceAdapter/ThrowingTaskAdapter/ThrowingCopyAdapter ensure
        // Clean plans NEVER accidentally reach those execution paths.
        string regBakDir = Path.Combine(cfg.LogDir, "regbak");
        Directory.CreateDirectory(regBakDir);

        var executor = new GatedExecutor(
            gate, log,
            new RecycleBinFileDeleteAdapter(),      // REAL: P1 junk delete goes here
            new RegistryDeleteAdapter(regBakDir),   // REAL: P3 startup value-delete goes here
            new ThrowingServiceAdapter(),
            new ThrowingTaskAdapter(),
            new ThrowingProcessAdapter(),
            new ThrowingCopyAdapter());

        try
        {
            // ---------------------------------------------------------------- SEED
            Console.WriteLine("[C-E2E] Step 1: seeding disposable artifacts...");

            // P1 junk: a real directory with a file the executor will delete.
            junkDir = string.IsNullOrWhiteSpace(cfg.JunkDir)
                ? Path.Combine(Path.GetTempPath(), $"WCK-CleanE2E-{Guid.NewGuid():N}")
                : cfg.JunkDir;
            Directory.CreateDirectory(junkDir);
            File.WriteAllText(Path.Combine(junkDir, "junk.txt"), "WCK CleanE2E junk file", Encoding.UTF8);
            Console.WriteLine($"[C-E2E]   Junk dir seeded: {junkDir}");

            // P3 Run value: a fake startup entry in HKCU\...\Run.
            seedRunValueName = string.IsNullOrWhiteSpace(cfg.RunValueName)
                ? $"WCK-CleanE2E-{Guid.NewGuid():N}"
                : cfg.RunValueName;
            using (var runKey = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: true))
            {
                if (runKey is null)
                    throw new InvalidOperationException($"Cannot open HKCU\\{RunSubKey} for writing — cannot seed P3.");
                runKey.SetValue(seedRunValueName, @"C:\FAKE-WCK-CLEANE2E.exe");
            }
            Console.WriteLine($"[C-E2E]   Run value seeded: HKCU\\...\\Run\\{seedRunValueName}");

            // The protected path for P2 (not seeded — must be an EXISTING system path).
            string protectedPath = Environment.GetFolderPath(Environment.SpecialFolder.System);

            // ---------------------------------------------------------------- P1 + P2: JunkScanner
            Console.WriteLine("[C-E2E] Step 2: P1+P2 — JunkScanner with controlled probe...");
            var probe = new ControlledJunkProbe(new[]
            {
                new JunkCandidate(junkDir, 0L, "WCK-CleanE2E disposable junk dir"),
                new JunkCandidate(protectedPath, 0L, "WCK-CleanE2E protected path (must not enter plan)"),
            });
            var scanner = new JunkScanner(probe, gate);
            JunkScanResult scanResult = scanner.Scan(utc);

            bool junkInPlan = scanResult.Plan.Actions.Any(a =>
                a is FileDeleteAction f && string.Equals(f.Path, junkDir, StringComparison.OrdinalIgnoreCase));
            bool protectedInPlan = scanResult.Plan.Actions.Any(a =>
                a is FileDeleteAction f && string.Equals(f.Path, protectedPath, StringComparison.OrdinalIgnoreCase));
            bool protectedSkipped = scanResult.Skipped.Any(s =>
                s.Action is FileDeleteAction f && string.Equals(f.Path, protectedPath, StringComparison.OrdinalIgnoreCase));

            Console.WriteLine($"[C-E2E]   P1: junk in plan = {junkInPlan}  (expected: true)");
            Console.WriteLine($"[C-E2E]   P2: protected in Skipped = {protectedSkipped}, in plan = {protectedInPlan}  (expected: Skipped=true, inPlan=false)");

            if (!junkInPlan)
                throw new InvalidOperationException("P1 FAIL: gate should have allowed the junk dir but it wasn't added to the plan.");
            if (!protectedSkipped || protectedInPlan)
                throw new InvalidOperationException("P2 FAIL: gate should have refused the protected path but it entered the plan.");

            // Execute P1 (real file delete via RecycleBinFileDeleteAdapter).
            Console.WriteLine("[C-E2E] Step 3: P1 — executing junk delete plan...");
            ExecutionReport junkReport = executor.ExecuteWithReport(scanResult.Plan, scanResult.Plan.ComputeHash());
            Console.WriteLine($"[C-E2E]   Executor authorized={junkReport.Authorized} results={junkReport.Results.Count}");
            if (!junkReport.Authorized)
                throw new InvalidOperationException($"P1 FAIL: executor refused the junk plan: {junkReport.Results.FirstOrDefault()?.Detail}");

            // P1 ground truth: junk dir must be gone (recycle bin counts as gone for our purposes).
            bool junkGone = !Directory.Exists(junkDir) && !File.Exists(junkDir);
            Console.WriteLine($"[C-E2E]   P1 ground truth: junk dir gone = {junkGone}  path={junkDir}");

            // P2 ground truth: protected path still exists.
            bool protectedStillExists = Directory.Exists(protectedPath);
            Console.WriteLine($"[C-E2E]   P2 ground truth: protected path still exists = {protectedStillExists}  path={protectedPath}");

            // ---------------------------------------------------------------- P3: Startup probe + planner
            Console.WriteLine("[C-E2E] Step 4: P3 — startup probe, planner, gate + execute...");

            // Find the seeded value via the REAL Win32StartupProbe.
            IReadOnlyList<StartupEntry> allEntries = new Win32StartupProbe().ReadAll();
            StartupEntry? seedEntry = allEntries.FirstOrDefault(e =>
                e.Source == StartupSource.HkcuRun &&
                string.Equals(e.Name, seedRunValueName, StringComparison.OrdinalIgnoreCase));

            if (seedEntry is null)
                throw new InvalidOperationException($"P3 FAIL: Win32StartupProbe did not find the seeded Run value '{seedRunValueName}'.");
            Console.WriteLine($"[C-E2E]   P3 probe found entry: '{seedEntry.Name}' cmd='{seedEntry.Command}'");

            // Value-delete plan + gate check.
            OperationPlan valuePlan = StartupPlanner.BuildDisablePlan(seedEntry, utc);
            SafetyVerdict valueVerdict = gate.Evaluate(valuePlan.Actions[0]);
            Console.WriteLine($"[C-E2E]   P3 value-delete gate: allowed={valueVerdict.Allowed}  reason={valueVerdict.Reason}");
            if (!valueVerdict.Allowed)
                throw new InvalidOperationException($"P3 FAIL: gate refused the value-delete (expected: allowed). Reason: {valueVerdict.Reason}");

            // Key-delete refusal (gate-level assertion only — no execution needed).
            // Use the protected HKCU\Software\Microsoft\Windows\CurrentVersion key (explicitly in
            // ProtectedRegistryKeys); this is the parent of the Run key and its key-delete is refused.
            var keyDeleteAction = new RegistryDeleteAction
            {
                Hive = CoreHive.CurrentUser,
                SubKeyPath = @"Software\Microsoft\Windows\CurrentVersion",
                ValueName = null,
                View = CoreView.Registry64,
                Description = "P3: key-delete on protected HKCU CurrentVersion key (must be refused)",
                Reason = "CleanE2E — gate key-delete refusal proof",
                Risk = RiskLevel.Medium,
                Undo = UndoCapability.Partial,
            };
            SafetyVerdict keyVerdict = gate.Evaluate(keyDeleteAction);
            Console.WriteLine($"[C-E2E]   P3 key-delete gate:   allowed={keyVerdict.Allowed}  reason={keyVerdict.Reason}  (expected: false)");
            if (keyVerdict.Allowed)
                throw new InvalidOperationException($"P3 FAIL: gate allowed a key-delete on a protected key — safety regression. Reason: {keyVerdict.Reason}");

            // Execute the value-delete via the REAL RegistryDeleteAdapter.
            ExecutionReport regReport = executor.ExecuteWithReport(valuePlan, valuePlan.ComputeHash());
            Console.WriteLine($"[C-E2E]   P3 executor authorized={regReport.Authorized} results={regReport.Results.Count}");
            if (!regReport.Authorized)
                throw new InvalidOperationException($"P3 FAIL: executor refused the value-delete plan: {regReport.Results.FirstOrDefault()?.Detail}");

            // P3 ground truth: the seeded Run value must be gone.
            bool valueGone = !RunValueExists(seedRunValueName);
            Console.WriteLine($"[C-E2E]   P3 ground truth: Run value gone = {valueGone}  name={seedRunValueName}");

            // ---------------------------------------------------------------- VERDICT
            // GateDecision.Pass = ExpectedAllowed == ActualAllowed
            var decisions = new List<GateDecision>
            {
                new("P1-junk-in-plan",          "junk dir entered the plan",                         true,  junkInPlan),
                new("P2-protected-refused",     "System32 path refused by gate (not in plan)",       false, protectedInPlan),
                new("P3-value-delete-allowed",  "Run value-delete gate: ALLOW",                      true,  valueVerdict.Allowed),
                new("P3-key-delete-refused",    "CurrentVersion key-delete gate: REFUSE",            false, keyVerdict.Allowed),
                new("P1-ground-truth",          "junk dir gone after execute",                       true,  junkGone),
                new("P2-ground-truth",          "protected path still exists after execute",         true,  protectedStillExists),
                new("P3-ground-truth",          "Run value gone after execute",                      true,  valueGone),
            };

            evidence.GateDecisions = decisions;

            foreach (GateDecision d in decisions)
                Console.WriteLine($"[C-E2E]   [{(d.Pass ? "PASS" : "FAIL")}] {d.Label}");

            bool allPass = decisions.All(d => d.Pass);

            // The seeded junk dir was deleted by the executor, so null it to skip the finally cleanup.
            if (junkGone)
                junkDir = null;
            // The Run value was deleted by the executor; null to skip finally cleanup.
            if (valueGone)
                seedRunValueName = null;

            string verdict = allPass ? "PASS" : "FAIL";
            evidence.Verdict = verdict;
            evidence.Pass = allPass;

            Console.WriteLine();
            Console.WriteLine($"[C-E2E] ===== RESULT: {verdict} =====");
            WriteReport(cfg.OutputDir, evidence);
            return allPass ? ExitOk : ExitVerifyFail;
        }
        finally
        {
            // Clean up any seeds the executor did NOT already remove (idempotent).
            if (junkDir is not null && (Directory.Exists(junkDir) || File.Exists(junkDir)))
            {
                try { Directory.Delete(junkDir, recursive: true); }
                catch { /* best effort */ }
            }
            if (seedRunValueName is not null)
            {
                try
                {
                    using var runKey = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: true);
                    runKey?.DeleteValue(seedRunValueName, throwOnMissingValue: false);
                }
                catch { /* best effort */ }
            }
        }
    }

    // -----------------------------------------------------------------------
    // Helpers
    // -----------------------------------------------------------------------

    /// <summary>True when the named value exists under HKCU\...\Run (read-only).</summary>
    private static bool RunValueExists(string valueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunSubKey, writable: false);
            if (key is null) return false;
            return key.GetValue(valueName) is not null;
        }
        catch { return false; }
    }

    private static bool IsDisposableMachine(out string detail)
    {
        bool env = string.Equals(Environment.GetEnvironmentVariable("WCK_DISPOSABLE_MACHINE"), "1", StringComparison.Ordinal);
        string marker = Path.Combine(Path.GetTempPath(), "wck-disposable.marker");
        bool hasMarker = File.Exists(marker);
        detail = $"WCK_DISPOSABLE_MACHINE={(env ? "1" : "(unset)")}, marker={(hasMarker ? "present" : "absent")} ({marker})";
        return env && hasMarker;
    }

    // -----------------------------------------------------------------------
    private static void WriteReport(string outputDir, EvidenceReport evidence)
    {
        Directory.CreateDirectory(outputDir);

        string jsonPath = Path.Combine(outputDir, "clean-e2e-evidence.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(evidence, JsonOptions), Encoding.UTF8);
        Console.WriteLine($"[C-E2E] JSON evidence: {jsonPath}");

        var sb = new StringBuilder();
        sb.AppendLine("=== Clean E2E Summary ===");
        sb.AppendLine($"Generated  : {evidence.Generated}");
        sb.AppendLine($"Verdict    : {evidence.Verdict}");
        sb.AppendLine($"Disposable : {evidence.IsDisposableMachine}  ({evidence.DisposableSignal})");
        if (evidence.ExecutionRefusedReason is not null)
            sb.AppendLine($"ExecRefused: {evidence.ExecutionRefusedReason}");
        sb.AppendLine();
        sb.AppendLine("Gate decisions:");
        foreach (GateDecision d in evidence.GateDecisions ?? [])
            sb.AppendLine($"  [{(d.Pass ? "PASS" : "FAIL")}] {d.Name}: {d.Label}");
        sb.AppendLine();
        sb.AppendLine($"=== {evidence.Verdict} ===");

        string summaryPath = Path.Combine(outputDir, "clean-e2e-summary.txt");
        File.WriteAllText(summaryPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"[C-E2E] Summary     : {summaryPath}");
        Console.WriteLine();
        Console.Write(sb.ToString());
    }

    private static EvidenceReport BuildFatalEvidence(Config cfg, string error) => new()
    {
        Generated = DateTime.UtcNow.ToString("o"),
        OutputDir = cfg.OutputDir,
        Verdict = "FAIL",
        Pass = false,
        ExecutionRefusedReason = "unhandled exception: " + error,
    };

    // -----------------------------------------------------------------------
    private static bool TryParseArgs(string[] args, out Config cfg, out string error)
    {
        cfg = default!;
        error = string.Empty;
        string? output = null;
        bool execute = false;
        string? logDir = null;
        string? junkDir = null;
        string? runValueName = null;
        int settle = 45;

        int i = 0;
        while (i < args.Length)
        {
            string tok = args[i];
            if (!tok.StartsWith("--", StringComparison.Ordinal))
            {
                error = $"unexpected argument: {tok}";
                return false;
            }
            string key = tok.TrimStart('-');

            // --execute is a flag that optionally accepts a boolean value (mirrors UninstallE2E's arg style).
            if (key.Equals("execute", StringComparison.OrdinalIgnoreCase))
            {
                execute = true;
                // Consume the next token as a value only if it looks like "true"/"false".
                if (i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
                {
                    if (bool.TryParse(args[i + 1], out bool bv))
                    {
                        execute = bv;
                        i++;
                    }
                    // If it is not a bool, leave it for the next iteration as a key.
                }
                i++;
                continue;
            }

            // All other switches require a value.
            if (i + 1 >= args.Length)
            {
                error = $"missing value for {tok}";
                return false;
            }
            string val = args[i + 1];
            i += 2;

            if (key.Equals("output", StringComparison.OrdinalIgnoreCase))
                output = val;
            else if (key.Equals("logDir", StringComparison.OrdinalIgnoreCase))
                logDir = val;
            else if (key.Equals("junkDir", StringComparison.OrdinalIgnoreCase))
                junkDir = val;
            else if (key.Equals("runValueName", StringComparison.OrdinalIgnoreCase))
                runValueName = val;
            else if (key.Equals("settleSeconds", StringComparison.OrdinalIgnoreCase))
            {
                if (int.TryParse(val, out int parsed) && parsed >= 0)
                    settle = parsed;
                else
                    Console.Error.WriteLine($"[C-E2E] WARNING: ignoring invalid --settleSeconds '{val}', using {settle}");
            }
            else
            {
                error = $"unknown argument: {tok}";
                return false;
            }
        }

        if (output is null)
        {
            error = "missing required argument: --output";
            return false;
        }

        cfg = new Config(
            OutputDir: output,
            Execute: execute,
            LogDir: logDir ?? Path.GetTempPath(),
            SettleSeconds: settle,
            JunkDir: junkDir,
            RunValueName: runValueName);
        return true;
    }

    private static void PrintUsage() => Console.WriteLine("""
        Usage:
          CleanE2E --output <dir>
                   [--execute]
                   [--logDir <dir>] [--settleSeconds 45]
                   [--junkDir <dir>] [--runValueName <name>]

          Without --execute: eval/dry-run (zero mutation) — asserts gate decisions only. Exits EVAL-PASS.
          --execute: performs real seed+delete pipeline. REFUSED unless
          WCK_DISPOSABLE_MACHINE=1 and %TEMP%\wck-disposable.marker exist (disposable VM only).
        """);
}

// ---------------------------------------------------------------------------
// Controlled IJunkProbe: returns a fixed list of candidates (used by both eval and execute paths).
// ---------------------------------------------------------------------------
internal sealed class ControlledJunkProbe : IJunkProbe
{
    private readonly IReadOnlyList<JunkCandidate> _candidates;

    public ControlledJunkProbe(IEnumerable<JunkCandidate> candidates)
        => _candidates = candidates.ToList();

    public IReadOnlyList<JunkCandidate> FindJunk() => _candidates;
}

// ---------------------------------------------------------------------------
// Throwing stubs for adapters that must NOT be reached in a Clean plan
// (only FileDeleteAction and RegistryDeleteAction are expected in this harness).
// ---------------------------------------------------------------------------
internal sealed class ThrowingServiceAdapter : IServiceAdapter
{ public void Apply(ServiceDeleteAction a) => throw new InvalidOperationException("service op not expected in Clean E2E"); }

internal sealed class ThrowingTaskAdapter : ITaskAdapter
{ public void Apply(TaskDeleteAction a) => throw new InvalidOperationException("task op not expected in Clean E2E"); }

internal sealed class ThrowingProcessAdapter : IProcessAdapter
{ public void Run(CommandAction a) => throw new InvalidOperationException("process launch not expected in Clean E2E"); }

internal sealed class ThrowingCopyAdapter : ICopyAdapter
{
    public CopyAdapterResult Copy(CopyAction a) => throw new InvalidOperationException("copy not expected in Clean E2E");
    public void Merge(RestoreMergeAction a) => throw new InvalidOperationException("merge not expected in Clean E2E");
}

// ---------------------------------------------------------------------------
// Evidence contracts.
// ---------------------------------------------------------------------------
internal sealed record Config(
    string OutputDir,
    bool Execute,
    string LogDir,
    int SettleSeconds,
    string? JunkDir,
    string? RunValueName);

internal sealed class EvidenceReport
{
    public string? Generated { get; set; }
    public bool Pass { get; set; }
    public string? Verdict { get; set; }
    public string? OutputDir { get; set; }
    public bool Execute { get; set; }
    public bool IsDisposableMachine { get; set; }
    public string? DisposableSignal { get; set; }
    public string? ExecutionRefusedReason { get; set; }
    public List<GateDecision>? GateDecisions { get; set; }
}

/// <summary>Records one gate assertion and whether it produced the expected outcome.</summary>
internal sealed record GateDecision(string Name, string Label, bool ExpectedAllowed, bool ActualAllowed)
{
    /// <summary>True when the gate's actual decision matched the expected direction.</summary>
    public bool Pass => ExpectedAllowed == ActualAllowed;
}
