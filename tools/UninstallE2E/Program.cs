using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Uninstall;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Execution;
using WindowsCareKit.Execution.Adapters;
using WindowsCareKit.Win32;

// ---------------------------------------------------------------------------
// UninstallE2E — end-to-end official-uninstaller harness over REAL programs.
//
// Proves the Uninstall module's command-policy (Phase 1 + Phase 2) against the
// REAL Windows registry inventory inside a throwaway Windows Sandbox VM. It
// exercises FOUR distinct command-policy outcomes with four different KINDS of
// program:
//   * MSI machine-wide      → ALLOW via the System32-msiexec pin            (7-Zip)
//   * Inno machine-wide      → ALLOW via the elevated InstallLocation anchor (Git for Windows)
//   * Per-user installer     → ALLOW, non-elevated, unanchored               (VS Code User)
//   * NSIS w/o InstallLocation → MANUAL fallback (elevated exe cannot anchor) (Notepad++)
//
// Pipeline per app:
//   1. Read installed apps from the registry (Win32InstalledAppReader) — the
//      SAME reader the WPF app uses, READ-ONLY.
//   2. Build the official-uninstaller plan (OfficialUninstallerPlanner) and vet
//      it through the PRODUCTION gate (SafetyGate over ProtectedResources.
//      ForCurrentSystem()), so each verdict EQUALS what a real user gets.
//      Classify ALLOW / MANUAL / BLOCK and assert the expected Phase-2 branch.
//   3. For the target ids in --execute, ACTUALLY run the uninstaller end to end
//      through the GatedExecutor + the REAL ProcessAdapter, then RE-READ the
//      registry to PROVE the program is gone. Registry-removal is the ground
//      truth, independent of the uninstaller's exit code.
//
// SAFETY CONTRACT (host protection)
//   - Reading the registry is read-only and safe anywhere.
//   - EXECUTING an uninstaller is destructive. The harness REFUSES to execute
//     unless the disposable-machine signal is present (env WCK_DISPOSABLE_MACHINE=1
//     AND %TEMP%\wck-disposable.marker) — the SAME opt-in step4-run.cmd uses. Run
//     on a real host, --execute degrades to evaluate-only and exits ExitGuardRefused;
//     it can NEVER uninstall a real user's programs.
//   - The harness opens no GUI of its own. The gate is the PRODUCTION gate (no
//     relaxation). Elevated (machine-wide) uninstallers run via ProcessAdapter's
//     ShellExecute "runas"; the in-VM runner self-elevates first, so the child
//     inherits the elevated token and no interactive UAC prompt appears.
//
// CLI
//   UninstallE2E
//     --output  <dir>          (required) evidence report output directory
//     --execute <id,...>       target IDS to ACTUALLY uninstall (e.g. git,vscode).
//                              Others are evaluated read-only. Empty => eval-only.
//     --require <id,...>       target ids that MUST be found installed for a
//                              non-vacuous run (default: 7zip,git,vscode,notepadpp)
//     --logDir  <dir>          execution-log dir (default: %TEMP%)
//     --settleSeconds <n>      removal-verify settle window (default 45)
// ---------------------------------------------------------------------------

namespace WindowsCareKit.Tools.UninstallE2E;

internal enum ExpectedOutcome
{
    AllowMsiPin,       // ALLOW, MSI, System32-pinned msiexec, elevated
    AllowAnchored,     // ALLOW, non-MSI, elevated, exe anchored under InstallLocation
    AllowNonElevated,  // ALLOW, non-MSI, NOT elevated (per-user)
    Manual,            // planner returns null (no usable / non-anchorable uninstaller)
    Block,             // planner builds a plan, but the gate BLOCKS it (e.g. msiexec /I maintenance verb)
}

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

    /// <summary>
    /// The programs the sandbox runner installs, each exercising a DISTINCT command-policy outcome.
    /// An app matches when its DisplayName contains <see cref="DisplayNameContains"/> and contains NONE of
    /// <see cref="Excludes"/> (so "Git" does not catch GitHub / Gitleaks).
    /// </summary>
    private sealed record TargetSpec(
        string Id, string DisplayNameContains, string[] Excludes, string Kind, ExpectedOutcome Expected);

    private static readonly TargetSpec[] Targets =
    {
        // 7-Zip's registry UninstallString is "MsiExec.exe /I{GUID}" — the maintenance/repair verb, NOT /X.
        // The gate (uninstall-only msiexec policy) correctly BLOCKS /I, so 7-Zip's official uninstaller falls
        // to manual in the real app. (A clean System32-msiexec /X{GUID} ALLOW is covered by unit tests.)
        new("7zip", "7-Zip", Array.Empty<string>(),
            "MSI machine-wide, /I maintenance verb → BLOCK (gate refuses non-/X msiexec)", ExpectedOutcome.Block),

        new("git", "Git", new[] { "GitHub", "Gitleaks", "githubprotocol", "Git LFS", "Git Credential" },
            "Inno machine-wide → ALLOW (elevated InstallLocation anchor)", ExpectedOutcome.AllowAnchored),

        new("vscode", "Visual Studio Code", Array.Empty<string>(),
            "Per-user → ALLOW (non-elevated, unanchored)", ExpectedOutcome.AllowNonElevated),

        new("notepadpp", "Notepad++", Array.Empty<string>(),
            "NSIS machine-wide, no InstallLocation → MANUAL fallback", ExpectedOutcome.Manual),
    };

    internal static int Main(string[] args)
    {
        if (!TryParseArgs(args, out Config cfg, out string argError))
        {
            Console.Error.WriteLine($"[U-E2E] ERROR: {argError}");
            PrintUsage();
            return ExitUsageError;
        }

        Console.WriteLine("[U-E2E] ===== Uninstall E2E — official-uninstaller over REAL programs =====");
        Console.WriteLine($"[U-E2E] Output         : {cfg.OutputDir}");
        Console.WriteLine($"[U-E2E] Execute ids    : {(cfg.Execute.Count == 0 ? "(none — evaluate-only)" : string.Join(", ", cfg.Execute))}");
        Console.WriteLine($"[U-E2E] Required found : {string.Join(", ", cfg.Require)}");
        Console.WriteLine($"[U-E2E] Settle seconds : {cfg.SettleSeconds}");

        try
        {
            return Run(cfg);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[U-E2E] UNHANDLED EXCEPTION: {ex}");
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
            ExecuteSet = cfg.Execute.ToList(),
            RequireSet = cfg.Require.ToList(),
            IsDisposableMachine = IsDisposableMachine(out string guardDetail),
            DisposableSignal = guardDetail,
        };

        bool executeRequested = cfg.Execute.Count > 0;

        // --- host-safety guard: executing uninstallers requires the disposable-machine opt-in. ----------
        if (executeRequested && !evidence.IsDisposableMachine)
        {
            Console.Error.WriteLine("[U-E2E] REFUSED: --execute requested but this is not a disposable machine.");
            Console.Error.WriteLine($"[U-E2E]   {guardDetail}");
            Console.Error.WriteLine("[U-E2E]   No uninstaller will be run. (Needs WCK_DISPOSABLE_MACHINE=1 + %TEMP%\\wck-disposable.marker inside a throwaway VM.)");
            evidence.ExecutionRefusedReason = "not a disposable machine — refusing to uninstall real programs";
        }

        // ------------------------------------------------------------------ 1. READ INVENTORY (read-only)
        Console.WriteLine("[U-E2E] Step 1: reading installed-app inventory (read-only)...");
        IReadOnlyList<InstalledApp> apps = new Win32InstalledAppReader().ReadAll();
        Console.WriteLine($"[U-E2E]   inventory: {apps.Count} apps with a DisplayName.");
        evidence.InventoryCount = apps.Count;

        DateTime utc = DateTime.UtcNow;
        var gate = new SafetyGate(ProtectedResources.ForCurrentSystem(), new Win32PathCanonicalizer());

        // ------------------------------------------------------------------ 2. CLASSIFY FOCUS APPS
        Console.WriteLine("[U-E2E] Step 2: planning + gate-vetting each focus app...");
        var focus = new List<FocusEntry>();
        foreach (TargetSpec t in Targets)
        {
            InstalledApp? app = Match(apps, t);
            if (app is null)
            {
                Console.WriteLine($"[U-E2E]   [{t.Id}] NOT FOUND ({t.DisplayNameContains})");
                focus.Add(new FocusEntry(t.Id, t.Kind, t.Expected, Found: false));
                continue;
            }

            FocusEntry entry = Classify(t, app, gate, utc);
            focus.Add(entry);
            Console.WriteLine($"[U-E2E]   [{t.Id}] {app.DisplayName}  ->  {entry.Classification}  branchOk={entry.BranchOk}  ({entry.GateReason})");
            if (entry.Command is not null)
                Console.WriteLine($"[U-E2E]        cmd: {entry.Command.FileName}  args=[{entry.Command.Arguments}]  elevated={entry.Command.RequiresElevation}  profile={entry.Command.Profile}  root={entry.Command.AllowedExecutableRoot ?? "(none)"}");
            foreach (string note in entry.Notes)
                Console.WriteLine($"[U-E2E]        note: {note}");
        }
        evidence.Focus = focus;

        // ------------------------------------------------------------------ 3. REQUIRED-FOUND + BRANCH CHECK
        var missingRequired = cfg.Require
            .Where(id => !focus.Any(f => f.TargetId == id && f.Found))
            .ToList();
        evidence.MissingRequired = missingRequired;
        if (missingRequired.Count > 0)
            Console.Error.WriteLine($"[U-E2E] Required programs not installed: {string.Join(", ", missingRequired)} (environment setup failed).");

        var branchMismatch = cfg.Require
            .Select(id => focus.FirstOrDefault(f => f.TargetId == id))
            .Where(f => f is { Found: true, BranchOk: false })
            .Select(f => $"{f!.TargetId}: expected {f.Expected}, got {f.Classification}")
            .ToList();
        evidence.BranchMismatch = branchMismatch;
        if (branchMismatch.Count > 0)
            Console.Error.WriteLine($"[U-E2E] Phase-2 branch mismatch: {string.Join("; ", branchMismatch)}");

        // ------------------------------------------------------------------ 4. EXECUTE (real uninstall)
        var executions = new List<ExecutionEntry>();
        if (executeRequested && evidence.IsDisposableMachine)
        {
            Console.WriteLine("[U-E2E] Step 4: executing real uninstalls (disposable machine confirmed)...");
            var log = new ExecutionLog(
                Path.Combine(cfg.LogDir, $"uninstall-e2e-{Guid.NewGuid():N}.jsonl"),
                new LogRedactor(null, null));
            var executor = new GatedExecutor(
                gate, log,
                new ThrowingFileDeleteAdapter(),
                new ThrowingRegistryAdapter(),
                new ThrowingServiceAdapter(),
                new ThrowingTaskAdapter(),
                new ProcessAdapter(),            // REAL: actually launches the uninstaller
                new ThrowingCopyAdapter());

            foreach (FocusEntry f in focus)
            {
                if (!cfg.Execute.Contains(f.TargetId, StringComparer.OrdinalIgnoreCase))
                    continue;

                ExecutionEntry exec = Execute(f, executor, cfg.SettleSeconds, cfg.ExecTimeoutSeconds);
                executions.Add(exec);
                Console.WriteLine($"[U-E2E]   EXEC [{f.TargetId}] {f.DisplayName}: authorized={exec.Authorized} status={exec.ActionStatus} removed={exec.RemovedFromRegistry} ({exec.Detail})");
            }
        }
        else if (executeRequested)
        {
            Console.WriteLine("[U-E2E] Step 4: SKIPPED — execution refused (not a disposable machine). Evaluate-only.");
        }
        else
        {
            Console.WriteLine("[U-E2E] Step 4: no --execute set; evaluate-only run.");
        }
        evidence.Executions = executions;

        // ------------------------------------------------------------------ 5. VERDICT
        // PASS requires:
        //   (a) every REQUIRED target found installed (else env setup failed — vacuous), AND
        //   (b) every REQUIRED target landed in its EXPECTED Phase-2 branch, AND
        //   (c) if execution was requested AND allowed: at least one app executed, and EVERY executed app
        //       was gate-allowed (not Blocked) AND removed from the registry (ground truth).
        // A run that requested execution but was guard-refused is ExitGuardRefused (host-safe), not PASS.
        bool requiredOk = missingRequired.Count == 0 && branchMismatch.Count == 0;

        string verdict;
        int exitCode;

        if (executeRequested && !evidence.IsDisposableMachine)
        {
            verdict = "GUARD-REFUSED";
            exitCode = ExitGuardRefused;
        }
        else if (!requiredOk)
        {
            verdict = "FAIL";
            exitCode = ExitVerifyFail;
        }
        else if (executeRequested)
        {
            // Every REQUESTED id must have produced a successful, removed execution. A requested id that was
            // skipped (no silent switch / planner-null / vanished), blocked, or not removed is a FAIL — we
            // NEVER fold a quietly-dropped target into PASS (auditor MAJOR: that would be false confidence
            // that BOTH apps were really uninstalled when one was silently dropped).
            var unproven = cfg.Execute
                .Select(id => (id, entry: executions.FirstOrDefault(e =>
                    string.Equals(e.TargetId, id, StringComparison.OrdinalIgnoreCase))))
                .Where(x => x.entry is null || x.entry.Skipped || x.entry.Blocked || !x.entry.RemovedFromRegistry)
                .Select(x => x.entry is null ? $"{x.id}: no execution produced"
                           : x.entry.Skipped ? $"{x.id}: skipped — {x.entry.Detail}"
                           : x.entry.Blocked ? $"{x.id}: gate-blocked"
                           : $"{x.id}: not removed from registry")
                .ToList();
            evidence.UnprovenExecutions = unproven;
            if (unproven.Count > 0)
                Console.Error.WriteLine($"[U-E2E] Requested uninstalls NOT proven: {string.Join("; ", unproven)}");

            bool anyExecuted = executions.Any(e => !e.Skipped);
            bool pass = anyExecuted && unproven.Count == 0;
            verdict = pass ? "PASS" : "FAIL";
            exitCode = pass ? ExitOk : ExitVerifyFail;
        }
        else
        {
            verdict = "EVAL-PASS";
            exitCode = ExitOk;
        }

        evidence.Verdict = verdict;
        evidence.Pass = exitCode == ExitOk;

        Console.WriteLine();
        Console.WriteLine($"[U-E2E] ===== RESULT: {verdict} =====");
        foreach (FocusEntry f in focus)
            Console.WriteLine($"[U-E2E]   [{f.TargetId}] found={f.Found} class={f.Classification} branchOk={f.BranchOk} ({f.Kind})");
        foreach (ExecutionEntry e in executions)
            Console.WriteLine($"[U-E2E]   exec [{e.TargetId}] skipped={e.Skipped} removed={e.RemovedFromRegistry} status={e.ActionStatus}");

        WriteReport(cfg.OutputDir, evidence);
        return exitCode;
    }

    // -----------------------------------------------------------------------
    private static InstalledApp? Match(IReadOnlyList<InstalledApp> apps, TargetSpec t) =>
        apps.FirstOrDefault(a =>
            a.DisplayName.Contains(t.DisplayNameContains, StringComparison.OrdinalIgnoreCase)
            && !t.Excludes.Any(x => a.DisplayName.Contains(x, StringComparison.OrdinalIgnoreCase)));

    private static FocusEntry Classify(TargetSpec t, InstalledApp app, SafetyGate gate, DateTime utc)
    {
        var notes = new List<string>();
        OperationPlan? plan = OfficialUninstallerPlanner.Build(app, utc);

        var baseEntry = new FocusEntry(t.Id, t.Kind, t.Expected, Found: true)
        {
            DisplayName = app.DisplayName,
            Source = app.Source.ToString(),
            RegistryKeyName = app.RegistryKeyName,
            InstallLocation = app.InstallLocation,
            RawUninstallString = app.UninstallString,
            RawQuietUninstallString = app.QuietUninstallString,
            Notes = notes,
        };

        if (plan is null || plan.IsEmpty)
        {
            bool ok = t.Expected == ExpectedOutcome.Manual;
            if (!ok)
                notes.Add("planner returned null (manual fallback) but a usable uninstaller was expected");
            return baseEntry with
            {
                Classification = "MANUAL",
                GateReason = "planner returned null (no usable or anchorable uninstaller)",
                BranchOk = ok,
            };
        }

        var cmd = (CommandAction)plan.Actions[0];
        SafetyVerdict v = gate.Validate(plan).Results[0].Verdict;
        string classification = v.Allowed ? "ALLOW" : "BLOCK";
        bool isMsi = string.Equals(Path.GetFileName(cmd.FileName), "msiexec.exe", StringComparison.OrdinalIgnoreCase);

        bool branchOk = t.Expected switch
        {
            ExpectedOutcome.AllowMsiPin => v.Allowed && isMsi && cmd.RequiresElevation,
            ExpectedOutcome.AllowAnchored => v.Allowed && !isMsi && cmd.RequiresElevation && cmd.AllowedExecutableRoot is { Length: > 0 },
            ExpectedOutcome.AllowNonElevated => v.Allowed && !isMsi && !cmd.RequiresElevation,
            ExpectedOutcome.Block => !v.Allowed, // planner built a plan, gate refused it (e.g. msiexec /I verb)
            ExpectedOutcome.Manual => false,     // a built plan never satisfies a Manual expectation
            _ => false,
        };
        if (!branchOk)
            notes.Add($"branch mismatch: expected {t.Expected}, got class={classification} msi={isMsi} elevated={cmd.RequiresElevation} root={(cmd.AllowedExecutableRoot is { Length: > 0 })}");

        return baseEntry with
        {
            Classification = classification,
            GateReason = v.Reason,
            Command = new CommandSummary(
                cmd.FileName,
                string.Join(" ", cmd.Arguments),
                cmd.RequiresElevation,
                cmd.Profile.ToString(),
                cmd.AllowedExecutableRoot),
            SilentCapable = IsSilentCapable(cmd),
            BranchOk = branchOk,
        };
    }

    // -----------------------------------------------------------------------
    private static ExecutionEntry Execute(FocusEntry f, GatedExecutor executor, int settleSeconds, int execTimeoutSeconds)
    {
        // Re-resolve the live app so we execute against the CURRENT registry (and capture identity to verify).
        InstalledApp? app = new Win32InstalledAppReader().ReadAll()
            .FirstOrDefault(a => a.Source.ToString() == f.Source
                                 && string.Equals(a.RegistryKeyName, f.RegistryKeyName, StringComparison.OrdinalIgnoreCase));
        if (app is null)
            return new ExecutionEntry(f.TargetId, f.DisplayName ?? "", Skipped: true,
                Detail: "app vanished from registry before execution");

        if (f.Classification != "ALLOW")
            return new ExecutionEntry(f.TargetId, f.DisplayName ?? "", Skipped: true,
                Detail: $"not executed: gate classification is {f.Classification} ({f.GateReason})");

        OperationPlan? plan = OfficialUninstallerPlanner.Build(app, DateTime.UtcNow);
        if (plan is null)
            return new ExecutionEntry(f.TargetId, f.DisplayName ?? "", Skipped: true,
                Detail: "not executed: planner now returns null");

        var cmd = (CommandAction)plan.Actions[0];
        if (!IsSilentCapable(cmd))
            return new ExecutionEntry(f.TargetId, f.DisplayName ?? "", Skipped: true,
                Detail: "not executed: registry uninstall string has no silent switch (would block unattended)");

        // Run the (blocking) uninstall on a worker so a hung vendor uninstaller (e.g. a GUI uninstaller that
        // stalls on a "close the app first" modal) can't wedge the whole run AND the VM forever.
        // ProcessAdapter.WaitForExit has no timeout; if it exceeds execTimeoutSeconds we ABANDON the worker
        // (a thread-pool BACKGROUND thread — it cannot keep the process alive past Main) and fall through to the
        // registry ground-truth check. The abandoned uninstaller keeps running in the disposable VM and is torn
        // down by the auto-close shutdown.
        Exception? execEx = null;
        var work = System.Threading.Tasks.Task.Run(() =>
        {
            try { return executor.ExecuteWithReport(plan, plan.ComputeHash()); }
            catch (Exception ex) { execEx = ex; return null; }
        });
        bool finished = work.Wait(TimeSpan.FromSeconds(execTimeoutSeconds));
        ExecutionReport? report = finished ? work.Result : null;

        // GROUND TRUTH regardless of how the process behaved: did the registry key go away? (Runs AFTER the
        // watchdog, so a slow-but-working uninstaller still gets the settle window to finish removing the key.)
        bool removed = VerifyRemoved(app.Source, app.RegistryKeyName, settleSeconds, out int waitedSeconds);

        if (!finished)
        {
            return new ExecutionEntry(f.TargetId, f.DisplayName ?? "", Skipped: false,
                Detail: $"uninstaller did not return within {execTimeoutSeconds}s (abandoned); registry-gone={removed} after {waitedSeconds}s")
            {
                Authorized = true, // the gate authorized it and it was launched; exit status is unknown
                ActionStatus = "TimedOut",
                Blocked = false,
                RemovedFromRegistry = removed,
                SettleSeconds = waitedSeconds,
            };
        }

        if (execEx is not null || report is null)
        {
            return new ExecutionEntry(f.TargetId, f.DisplayName ?? "", Skipped: false,
                Detail: $"executor threw: {execEx?.GetType().Name}: {execEx?.Message}; registry-gone={removed}")
            {
                Authorized = false,
                ActionStatus = "Exception",
                Blocked = false,
                RemovedFromRegistry = removed,
                SettleSeconds = waitedSeconds,
            };
        }

        ActionResult r0 = report.Results.Count > 0
            ? report.Results[0]
            : new ActionResult("", "command", ActionStatus.NotRun, "no result");
        bool blocked = !report.Authorized || r0.Status == ActionStatus.Blocked;

        string detail = report.Authorized
            ? $"executor status={r0.Status} ({r0.Detail}); registry-gone={removed} after {waitedSeconds}s"
            : $"plan refused: {r0.Detail}";

        return new ExecutionEntry(f.TargetId, f.DisplayName ?? "", Skipped: false, Detail: detail)
        {
            Authorized = report.Authorized,
            ActionStatus = r0.Status.ToString(),
            Blocked = blocked,
            RemovedFromRegistry = removed,
            SettleSeconds = waitedSeconds,
        };
    }

    private static bool VerifyRemoved(InstalledAppSource source, string regKeyName, int settleSeconds, out int waitedSeconds)
    {
        const int stepMs = 2000;
        int attempts = Math.Max(1, settleSeconds * 1000 / stepMs);
        for (int i = 0; i <= attempts; i++)
        {
            bool present = new Win32InstalledAppReader().ReadAll()
                .Any(a => a.Source == source
                          && string.Equals(a.RegistryKeyName, regKeyName, StringComparison.OrdinalIgnoreCase));
            if (!present) { waitedSeconds = i * stepMs / 1000; return true; }
            if (i < attempts) Thread.Sleep(stepMs);
        }
        waitedSeconds = settleSeconds;
        return false;
    }

    /// <summary>
    /// True when the planned uninstall command carries a silent switch, so it completes WITHOUT a UI in an
    /// unattended VM. msiexec needs /qn|/quiet; classic installers need /S|/SILENT|/VERYSILENT. This is a
    /// HARNESS constraint (the unattended VM has no human to click), NOT a product change — the planner runs
    /// the registry string verbatim either way.
    /// </summary>
    private static bool IsSilentCapable(CommandAction cmd)
    {
        bool isMsi = string.Equals(Path.GetFileName(cmd.FileName), "msiexec.exe", StringComparison.OrdinalIgnoreCase);
        var args = cmd.Arguments.Select(a => a.Trim().ToLowerInvariant()).ToList();
        if (isMsi)
            return args.Any(a => a is "/qn" or "/quiet" or "/q"
                                 || a.StartsWith("/qn", StringComparison.Ordinal)
                                 || a.StartsWith("/quiet", StringComparison.Ordinal));
        return args.Any(a => a is "/s" or "/silent" or "/verysilent" or "--silent" or "-s");
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

        string jsonPath = Path.Combine(outputDir, "uninstall-e2e-evidence.json");
        File.WriteAllText(jsonPath, JsonSerializer.Serialize(evidence, JsonOptions), Encoding.UTF8);
        Console.WriteLine($"[U-E2E] JSON evidence: {jsonPath}");

        var sb = new StringBuilder();
        sb.AppendLine("=== Uninstall E2E Summary ===");
        sb.AppendLine($"Generated  : {evidence.Generated}");
        sb.AppendLine($"Verdict    : {evidence.Verdict}");
        sb.AppendLine($"Disposable : {evidence.IsDisposableMachine}  ({evidence.DisposableSignal})");
        if (evidence.ExecutionRefusedReason is not null)
            sb.AppendLine($"ExecRefused: {evidence.ExecutionRefusedReason}");
        sb.AppendLine($"Inventory  : {evidence.InventoryCount} apps");
        if (evidence.MissingRequired is { Count: > 0 })
            sb.AppendLine($"MISSING    : {string.Join(", ", evidence.MissingRequired)}");
        if (evidence.BranchMismatch is { Count: > 0 })
            sb.AppendLine($"BRANCH-MISMATCH: {string.Join("; ", evidence.BranchMismatch)}");
        if (evidence.UnprovenExecutions is { Count: > 0 })
            sb.AppendLine($"UNPROVEN-EXEC : {string.Join("; ", evidence.UnprovenExecutions)}");
        sb.AppendLine();
        sb.AppendLine("Focus apps (plan + production-gate verdict):");
        foreach (FocusEntry f in evidence.Focus ?? [])
        {
            sb.AppendLine($"  [{f.TargetId}] {f.Kind}");
            sb.AppendLine($"      found={f.Found} class={f.Classification} expected={f.Expected} branchOk={f.BranchOk}");
            if (f.DisplayName is not null) sb.AppendLine($"      name={f.DisplayName}  src={f.Source}");
            if (f.Command is not null)
                sb.AppendLine($"      cmd={f.Command.FileName} args=[{f.Command.Arguments}] elevated={f.Command.RequiresElevation} profile={f.Command.Profile} root={f.Command.AllowedExecutableRoot ?? "(none)"} silent={f.SilentCapable}");
            sb.AppendLine($"      gate: {f.GateReason}");
            foreach (string n in f.Notes)
                sb.AppendLine($"      note: {n}");
        }
        sb.AppendLine();
        sb.AppendLine("Executions (real uninstall + registry-gone proof):");
        if ((evidence.Executions?.Count ?? 0) == 0)
            sb.AppendLine("  (none)");
        foreach (ExecutionEntry e in evidence.Executions ?? [])
        {
            sb.AppendLine($"  [{e.TargetId}] {e.DisplayName}");
            sb.AppendLine($"      skipped={e.Skipped} authorized={e.Authorized} status={e.ActionStatus} blocked={e.Blocked} removed={e.RemovedFromRegistry}");
            sb.AppendLine($"      detail: {e.Detail}");
        }
        sb.AppendLine();
        sb.AppendLine($"=== {evidence.Verdict} ===");

        string summaryPath = Path.Combine(outputDir, "uninstall-e2e-summary.txt");
        File.WriteAllText(summaryPath, sb.ToString(), Encoding.UTF8);
        Console.WriteLine($"[U-E2E] Summary     : {summaryPath}");
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
        var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < args.Length; i += 2)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal))
            {
                error = $"unexpected argument: {args[i]}";
                return false;
            }
            if (i + 1 >= args.Length)
            {
                error = $"missing value for {args[i]}";
                return false;
            }
            d[args[i].TrimStart('-')] = args[i + 1];
        }

        if (!d.TryGetValue("output", out string? output))
        {
            error = "missing required argument: --output";
            return false;
        }

        List<string> Csv(string key, IEnumerable<string> fallback) =>
            d.TryGetValue(key, out string? v) && !string.IsNullOrWhiteSpace(v)
                ? v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList()
                : fallback.ToList();

        int settle = 45;
        if (d.TryGetValue("settleSeconds", out string? ss))
        {
            if (int.TryParse(ss, out int parsed) && parsed >= 0)
                settle = parsed;
            else
                Console.Error.WriteLine($"[U-E2E] WARNING: ignoring invalid --settleSeconds '{ss}', using {settle}");
        }

        int execTimeout = 120;
        if (d.TryGetValue("execTimeoutSeconds", out string? et))
        {
            if (int.TryParse(et, out int parsedEt) && parsedEt > 0)
                execTimeout = parsedEt;
            else
                Console.Error.WriteLine($"[U-E2E] WARNING: ignoring invalid --execTimeoutSeconds '{et}', using {execTimeout}");
        }

        cfg = new Config(
            OutputDir: output,
            Execute: Csv("execute", Array.Empty<string>()),
            Require: Csv("require", Targets.Select(t => t.Id)),
            LogDir: d.TryGetValue("logDir", out string? ld) && !string.IsNullOrWhiteSpace(ld) ? ld : Path.GetTempPath(),
            SettleSeconds: settle,
            ExecTimeoutSeconds: execTimeout);
        return true;
    }

    private static void PrintUsage() => Console.WriteLine("""
        Usage:
          UninstallE2E --output <dir>
                       [--execute git,vscode]
                       [--require 7zip,git,vscode,notepadpp]
                       [--logDir <dir>] [--settleSeconds 45]

          --execute actually uninstalls the matching programs and is REFUSED unless
          WCK_DISPOSABLE_MACHINE=1 and %TEMP%\wck-disposable.marker exist (disposable VM only).
        """);
}

// ---------------------------------------------------------------------------
// Throwing stubs for adapters that must NOT be reached in an uninstall plan
// (only CommandAction → the REAL ProcessAdapter is exercised).
// ---------------------------------------------------------------------------
internal sealed class ThrowingFileDeleteAdapter : IFileDeleteAdapter
{ public void Delete(FileDeleteAction a) => throw new InvalidOperationException("file delete not expected in uninstall E2E"); }

internal sealed class ThrowingRegistryAdapter : IRegistryAdapter
{ public void Delete(RegistryDeleteAction a) => throw new InvalidOperationException("registry delete not expected in uninstall E2E"); }

internal sealed class ThrowingServiceAdapter : IServiceAdapter
{ public void Apply(ServiceDeleteAction a) => throw new InvalidOperationException("service op not expected in uninstall E2E"); }

internal sealed class ThrowingTaskAdapter : ITaskAdapter
{ public void Apply(TaskDeleteAction a) => throw new InvalidOperationException("task op not expected in uninstall E2E"); }

internal sealed class ThrowingCopyAdapter : ICopyAdapter
{
    public void Copy(CopyAction a) => throw new InvalidOperationException("copy not expected in uninstall E2E");
    public void Merge(RestoreMergeAction a) => throw new InvalidOperationException("merge not expected in uninstall E2E");
}

// ---------------------------------------------------------------------------
// Evidence contracts.
// ---------------------------------------------------------------------------
internal sealed record Config(
    string OutputDir,
    List<string> Execute,
    List<string> Require,
    string LogDir,
    int SettleSeconds,
    int ExecTimeoutSeconds);

internal sealed class EvidenceReport
{
    public string? Generated { get; set; }
    public bool Pass { get; set; }
    public string? Verdict { get; set; }
    public string? OutputDir { get; set; }
    public List<string>? ExecuteSet { get; set; }
    public List<string>? RequireSet { get; set; }
    public bool IsDisposableMachine { get; set; }
    public string? DisposableSignal { get; set; }
    public string? ExecutionRefusedReason { get; set; }
    public int InventoryCount { get; set; }
    public List<string>? MissingRequired { get; set; }
    public List<string>? BranchMismatch { get; set; }
    public List<string>? UnprovenExecutions { get; set; }
    public List<FocusEntry>? Focus { get; set; }
    public List<ExecutionEntry>? Executions { get; set; }
}

internal sealed record CommandSummary(
    string FileName, string Arguments, bool RequiresElevation, string Profile, string? AllowedExecutableRoot);

internal sealed record FocusEntry(string TargetId, string Kind, ExpectedOutcome Expected, bool Found)
{
    public string? DisplayName { get; init; }
    public string? Source { get; init; }
    public string RegistryKeyName { get; init; } = "";
    public string? InstallLocation { get; init; }
    public string? RawUninstallString { get; init; }
    public string? RawQuietUninstallString { get; init; }
    public string Classification { get; init; } = "NOT-FOUND";
    public string GateReason { get; init; } = "";
    public CommandSummary? Command { get; init; }
    public bool SilentCapable { get; init; }
    public bool BranchOk { get; init; }
    public List<string> Notes { get; init; } = new();
}

internal sealed record ExecutionEntry(string TargetId, string DisplayName, bool Skipped, string Detail)
{
    public bool Authorized { get; init; }
    public string ActionStatus { get; init; } = "NotRun";
    public bool Blocked { get; init; }
    public bool RemovedFromRegistry { get; init; }
    public int SettleSeconds { get; init; }
}
