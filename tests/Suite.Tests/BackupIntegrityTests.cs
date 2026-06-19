using System.IO;
using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// Step 3 (W2/W3/W4) host-safe verification of the integrity ring + headless BackupRunner. The build path is
/// pure (in-memory fakes, zero real IO). The write/runner paths use only a TempWorkspace under
/// <see cref="Path.GetTempPath"/> — never a real user/profile path. Nothing destructive runs.
/// </summary>
public class BackupIntegrityTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc);

    // %TEMP% lives under the real current user's profile, so the hardened write-target gate allows it.
    private static ISafetyGate RealGate()
        => new SafetyGate(ProtectedResources.ForCurrentSystem(), new FakeCanonicalizer());

    private static CopyAction Copy(string id, string source, string destination)
        => new() { Id = id, Source = source, Destination = destination, Description = "copy", Reason = "t" };

    private static CopySkipReport CopiedReport(params CopyFileOutcome[] outcomes) => new(outcomes);

    private static CopyFileOutcome Copied(string id, string destination)
        => new(id, $@"C:\src\{id}", destination, true, null, "ok");

    // ----------------------------------------------------------------------------------------------------
    // Step 6: BuildIntegrity is a pure, per-leaf walk of the DESTINATION tree (zero IO; no source path).
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void BuildIntegrity_emits_one_per_leaf_row_with_destination_relative_paths_and_no_source()
    {
        string payload = @"D:\pay";
        string treeDest = Path.Combine(payload, "App");
        string fileDest = Path.Combine(payload, "single.txt");

        var fs = new FakeFileSystem()
            .AddFile(Path.Combine(treeDest, "top.txt"), "AAA")                       // 3 bytes
            .AddFile(Path.Combine(treeDest, "nested", "deep.txt"), "BBBBB")           // 5 bytes
            .AddFile(fileDest, "CC");                                                 // 2 bytes

        var hasher = new FakeHasher()
            .Map(Path.Combine(treeDest, "top.txt"), "hash-top")
            .Map(Path.Combine(treeDest, "nested", "deep.txt"), "hash-deep")
            .Map(fileDest, "hash-single");
        var clock = new FakeClock(T0);

        var copied = CopiedReport(Copied("app", treeDest), Copied("single", fileDest));

        IReadOnlyList<BackupIntegrity> rows =
            new BackupIntegrityWriter().BuildIntegrity(copied, payload, fs, hasher, clock);

        Assert.Equal(3, rows.Count);

        BackupIntegrity top = rows.Single(r => r.DestinationRelativePath == Path.Combine("App", "top.txt"));
        Assert.Equal("app", top.EntryId);
        Assert.Equal("hash-top", top.Sha256);
        Assert.Equal(3, top.ByteSize);
        Assert.Equal(T0, top.CopiedUtc);

        BackupIntegrity deep = rows.Single(r => r.DestinationRelativePath == Path.Combine("App", "nested", "deep.txt"));
        Assert.Equal("app", deep.EntryId);
        Assert.Equal("hash-deep", deep.Sha256);
        Assert.Equal(5, deep.ByteSize);

        BackupIntegrity single = rows.Single(r => r.DestinationRelativePath == "single.txt");
        Assert.Equal("single", single.EntryId);
        Assert.Equal("hash-single", single.Sha256);
        Assert.Equal(2, single.ByteSize);

        // No source path leaks into any row (locked decision #4): every relative path stays under the payload.
        Assert.All(rows, r => Assert.DoesNotContain(@"C:\src", r.DestinationRelativePath));
        Assert.All(rows, r => Assert.False(Path.IsPathRooted(r.DestinationRelativePath)));
    }

    [Fact]
    public void BuildIntegrity_ignores_skipped_entries_and_missing_destinations()
    {
        string payload = @"D:\pay";
        string okDest = Path.Combine(payload, "ok.txt");
        var fs = new FakeFileSystem().AddFile(okDest, "x");
        var hasher = new FakeHasher();
        var clock = new FakeClock(T0);

        var report = new CopySkipReport(new[]
        {
            new CopyFileOutcome("ok", @"C:\src\ok", okDest, true, null, "ok"),
            new CopyFileOutcome("skip", @"C:\src\b", Path.Combine(payload, "b"), false, CopySkipReason.Locked, "IOException"),
            new CopyFileOutcome("gone", @"C:\src\c", Path.Combine(payload, "missing"), true, null, "ok"),
        });

        IReadOnlyList<BackupIntegrity> rows =
            new BackupIntegrityWriter().BuildIntegrity(report, payload, fs, hasher, clock);

        // Only the one copied entry whose destination actually exists contributes a row.
        BackupIntegrity only = Assert.Single(rows);
        Assert.Equal("ok", only.EntryId);
    }

    // ----------------------------------------------------------------------------------------------------
    // Step 7: WriteIntegrity produces a golden backup_integrity.json under a temp root, gate-checked.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void WriteIntegrity_writes_a_deterministic_json_file_into_the_payload_dir()
    {
        using var ws = new TempWorkspace("wck-integrity-");

        var rows = new[]
        {
            new BackupIntegrity("app", Path.Combine("App", "top.txt"), "hash-top", 3, T0),
            new BackupIntegrity("single", "single.txt", "hash-single", 2, T0),
        };

        string path = new BackupIntegrityWriter().WriteIntegrity(rows, ws.Root, RealGate());

        Assert.True(File.Exists(path));
        Assert.EndsWith(BackupIntegrityFiles.Integrity, path);

        string json = File.ReadAllText(path);
        Assert.Contains("\"entryId\": \"app\"", json);
        Assert.Contains("\"sha256\": \"hash-top\"", json);
        Assert.Contains("\"byteSize\": 3", json);
        Assert.Contains("\"destinationRelativePath\":", json);
        Assert.Contains("2026-01-01T12:30:00", json);
        // The source path is never serialized (structurally redaction-free).
        Assert.DoesNotContain("\"source\"", json);
        Assert.DoesNotContain(@"C:\\src", json);
    }

    [Fact]
    public void WriteIntegrity_refuses_a_gate_blocked_destination()
    {
        var rows = Array.Empty<BackupIntegrity>();
        Assert.Throws<UnauthorizedAccessException>(() =>
            new BackupIntegrityWriter().WriteIntegrity(rows, @"C:\Windows\wck-evil", RealGate()));
    }

    // ----------------------------------------------------------------------------------------------------
    // Step 8: BackupRunner headless — integrity + reports land in the temp root; zero real user dir touched.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void BackupRunner_writes_integrity_and_reports_into_the_payload_root_without_real_copies()
    {
        using var ws = new TempWorkspace("wck-runner-");
        string treeDest = Path.Combine(ws.Root, "App");

        var plan = new OperationPlan("Back up", "backup",
            new[] { Copy("app", @"C:\src\App", treeDest) }, T0);
        var planResult = new BackupPlanResult(plan,
            Array.Empty<BackupEntry>(), Array.Empty<BackupSkip>(), Array.Empty<BackupEntry>());

        // The executor RECORDS the request and reports Done — it performs NO real copy.
        var executor = new RecordingBackupExecutor(authorized: true,
            new BackupActionResult("app", BackupActionStatus.Done, "ok"));

        // The destination tree the integrity walk will hash is supplied in-memory (no real files created).
        var fs = new FakeFileSystem()
            .AddFile(Path.Combine(treeDest, "top.txt"), "AAA")
            .AddFile(Path.Combine(treeDest, "nested", "deep.txt"), "BBBBB");
        var hasher = new FakeHasher()
            .Map(Path.Combine(treeDest, "top.txt"), "h1")
            .Map(Path.Combine(treeDest, "nested", "deep.txt"), "h2");
        var clock = new FakeClock(T0);

        var runner = new BackupRunner(executor, new BackupIntegrityWriter(),
            new BackupReportWriter(new LogRedactor(null, null)), RealGate(), fs, hasher, clock);

        BackupRunResult result = runner.Run(planResult, plan.ComputeHash(), ws.Root);

        Assert.True(result.Authorized);
        Assert.True(executor.WasCalled);
        Assert.Equal(2, result.Integrity.Count);                 // two leaves under the tree dest
        Assert.Single(result.CopyReport.Copied);                 // one copy action, marked Done

        // The three output files (integrity + the two reports) landed in the temp payload root only.
        Assert.True(File.Exists(Path.Combine(ws.Root, BackupIntegrityFiles.Integrity)));
        Assert.True(File.Exists(Path.Combine(ws.Root, BackupReportFiles.Report)));
        Assert.True(File.Exists(Path.Combine(ws.Root, BackupReportFiles.ManualTodo)));

        // W4: the report points to the integrity manifest with the leaf count.
        string report = File.ReadAllText(Path.Combine(ws.Root, BackupReportFiles.Report));
        Assert.Contains($"Integrity: {BackupIntegrityFiles.Integrity} (2 hash)", report);
    }

    [Fact]
    public void BackupRunner_refusal_writes_nothing()
    {
        using var ws = new TempWorkspace("wck-runner-refused-");
        var plan = new OperationPlan("Back up", "backup",
            new[] { Copy("app", @"C:\src\App", Path.Combine(ws.Root, "App")) }, T0);
        var planResult = new BackupPlanResult(plan,
            Array.Empty<BackupEntry>(), Array.Empty<BackupSkip>(), Array.Empty<BackupEntry>());

        var executor = new RecordingBackupExecutor(authorized: false,
            new BackupActionResult("app", BackupActionStatus.NotRun, "plan refused"));

        var runner = new BackupRunner(executor, new BackupIntegrityWriter(),
            new BackupReportWriter(new LogRedactor(null, null)), RealGate(),
            new FakeFileSystem(), new FakeHasher(), new FakeClock(T0));

        BackupRunResult result = runner.Run(planResult, plan.ComputeHash(), ws.Root);

        Assert.False(result.Authorized);
        Assert.Empty(result.Integrity);
        // Nothing was written: a refused authorization runs and persists nothing.
        Assert.False(File.Exists(Path.Combine(ws.Root, BackupIntegrityFiles.Integrity)));
        Assert.False(File.Exists(Path.Combine(ws.Root, BackupReportFiles.Report)));
    }

    // ----------------------------------------------------------------------------------------------------
    // Step 9: invariant — the integrity step NEVER produces a new gated action; it only reads + writes.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Integrity_step_never_produces_a_new_gated_action()
    {
        using var ws = new TempWorkspace("wck-invariant-");
        string fileDest = Path.Combine(ws.Root, "a.txt");

        var fs = new FakeFileSystem().AddFile(fileDest, "x");
        var copied = CopiedReport(Copied("a", fileDest));

        // A gate that COUNTS every Evaluate call and records the action kinds it judged.
        var counting = new CountingGate(RealGate());

        var writer = new BackupIntegrityWriter();
        IReadOnlyList<BackupIntegrity> rows =
            writer.BuildIntegrity(copied, ws.Root, fs, new FakeHasher(), new FakeClock(T0));
        writer.WriteIntegrity(rows, ws.Root, counting);

        // Build evaluates the gate ZERO times (pure). Write evaluates EXACTLY ONCE — the synthetic CopyAction
        // write-target probe for the payload root — and that probe is never added to an executed plan.
        Assert.Equal(1, counting.EvaluateCount);
        Assert.All(counting.EvaluatedKinds, kind => Assert.Equal("copy", kind));
        Assert.Single(counting.EvaluatedKinds);
    }

    // ----------------------------------------------------------------------------------------------------
    // Step 10 (narrow real-IO): real CopyAdapter + real Sha256Hasher over a SYNTHETIC temp source → the
    // integrity hash matches the real file content. Source is ALSO under the temp root (never a host profile).
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void Integrity_hash_matches_a_real_copied_file_with_the_production_hasher_and_filesystem()
    {
        using var ws = new TempWorkspace("wck-integrity-real-");
        // Synthetic source AND destination both under the temp root.
        string src = ws.WriteFile("src/note.txt", "the quick brown fox");
        string dst = ws.Combine("payload", "note.txt");

        // Real copy adapter performs the copy (temp→temp), real hasher + real filesystem build the row.
        new WindowsCareKit.Execution.Adapters.CopyAdapter().Copy(
            new CopyAction { Source = src, Destination = dst, Description = "copy", Reason = "t" });

        var copied = CopiedReport(new CopyFileOutcome("note", src, dst, true, null, "ok"));
        IReadOnlyList<BackupIntegrity> rows = new BackupIntegrityWriter().BuildIntegrity(
            copied, ws.Combine("payload"), new PhysicalFileSystem(), new Sha256Hasher(), new FakeClock(T0));

        BackupIntegrity row = Assert.Single(rows);
        Assert.Equal("note.txt", row.DestinationRelativePath);
        Assert.Equal(new Sha256Hasher().ComputeFileSha256(dst), row.Sha256);
        Assert.Equal(new FileInfo(dst).Length, row.ByteSize);
    }

    // ----------------------------------------------------------------------------------------------------
    // W3: the copy-report shaping + skip classification moved from the view-model into the runner verbatim.
    // ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(BackupActionStatus.Done, "ok", true, null)]
    [InlineData(BackupActionStatus.Blocked, "gate said no", false, CopySkipReason.Blocked)]
    [InlineData(BackupActionStatus.Failed, "FileNotFoundException: missing", false, CopySkipReason.Missing)]
    [InlineData(BackupActionStatus.Failed, "PathTooLongException: too long", false, CopySkipReason.TooLong)]
    // The detail strings use the REAL exception TypeToken constants (not hand-typed copies) so a rename of
    // either execution-layer type breaks this classification test instead of silently dropping to Other (F1).
    [InlineData(BackupActionStatus.Failed, WindowsCareKit.Execution.Adapters.ForbiddenSourceException.TypeToken + ": secret", false, CopySkipReason.Forbidden)]
    [InlineData(BackupActionStatus.Failed, WindowsCareKit.Execution.Adapters.DestinationReparseException.TypeToken + ": junction", false, CopySkipReason.Forbidden)]
    [InlineData(BackupActionStatus.Failed, "IOException: being used by another process", false, CopySkipReason.Locked)]
    [InlineData(BackupActionStatus.Failed, "SomethingElseException: weird", false, CopySkipReason.Other)]
    [InlineData(BackupActionStatus.NotRun, "a prior action stopped the plan", false, CopySkipReason.Other)]
    public void BuildCopyReport_classifies_each_outcome_like_the_view_model_did(
        BackupActionStatus status, string detail, bool expectedCopied, CopySkipReason? expectedReason)
    {
        var copy = Copy("e1", @"C:\src\e1", @"D:\pay\e1");
        var plan = new OperationPlan("Back up", "backup", new[] { copy }, T0);
        var report = new BackupExecutionReport(true, new[] { new BackupActionResult("e1", status, detail) });

        CopySkipReport result = BackupRunner.BuildCopyReport(plan, report);

        CopyFileOutcome o = Assert.Single(result.Outcomes);
        Assert.Equal(expectedCopied, o.Copied);
        Assert.Equal(expectedReason, o.Reason);
        Assert.Equal(detail, o.Detail);
    }

    [Fact]
    public void BuildCopyReport_marks_an_action_with_no_result_as_not_run_other()
    {
        var copy = Copy("e1", @"C:\src\e1", @"D:\pay\e1");
        var plan = new OperationPlan("Back up", "backup", new[] { copy }, T0);
        var report = new BackupExecutionReport(true, Array.Empty<BackupActionResult>());

        CopyFileOutcome o = Assert.Single(BackupRunner.BuildCopyReport(plan, report).Outcomes);
        Assert.False(o.Copied);
        Assert.Equal(CopySkipReason.Other, o.Reason);
        Assert.Equal("not run", o.Detail);
    }

    // ----------------------------------------------------------------------------------------------------
    // F1: cross-assembly contract — Core's skip classification is keyed on the REAL exception TypeTokens.
    // The Core runner duplicates the type-name tokens as private constants (it cannot reference Suite.Execution).
    // This test feeds BuildCopyReport a detail built from the ACTUAL TypeToken value; if either exception is
    // renamed, its TypeToken value changes, the detail no longer matches the (now-stale) Core constant, and the
    // outcome silently drops to CopySkipReason.Other — which this test asserts MUST NOT happen.
    // ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(WindowsCareKit.Execution.Adapters.ForbiddenSourceException.TypeToken)]
    [InlineData(WindowsCareKit.Execution.Adapters.DestinationReparseException.TypeToken)]
    public void BuildCopyReport_classifies_real_execution_exception_tokens_as_forbidden(string typeToken)
    {
        // The execution layer records "{TypeName}: {Message}" — reproduce that exact shape from the real token.
        string detail = $"{typeToken}: refused at the boundary";
        var copy = Copy("e1", @"C:\src\e1", @"D:\pay\e1");
        var plan = new OperationPlan("Back up", "backup", new[] { copy }, T0);
        var report = new BackupExecutionReport(true,
            new[] { new BackupActionResult("e1", BackupActionStatus.Failed, detail) });

        CopyFileOutcome o = Assert.Single(BackupRunner.BuildCopyReport(plan, report).Outcomes);

        Assert.False(o.Copied);
        // The contract: a REAL forbidden/reparse token must classify as Forbidden, never silently Other.
        Assert.Equal(CopySkipReason.Forbidden, o.Reason);
        Assert.NotEqual(CopySkipReason.Other, o.Reason);
    }

    [Fact]
    public void BuildCopyReport_token_constants_match_the_actual_exception_type_names()
    {
        // The TypeToken IS the type name (nameof) — pin it so a rename can't drift the token from the type.
        Assert.Equal(nameof(WindowsCareKit.Execution.Adapters.ForbiddenSourceException),
            WindowsCareKit.Execution.Adapters.ForbiddenSourceException.TypeToken);
        Assert.Equal(nameof(WindowsCareKit.Execution.Adapters.DestinationReparseException),
            WindowsCareKit.Execution.Adapters.DestinationReparseException.TypeToken);
    }

    // ----------------------------------------------------------------------------------------------------
    // F3: BuildIntegrity fails closed on an off-root destination — it must NEVER record a raw absolute/escaping
    // leaf path. A copied outcome whose destination resolves OUTSIDE the payload root is rejected with a typed
    // exception rather than silently leaking the off-root path into backup_integrity.json.
    // ----------------------------------------------------------------------------------------------------

    [Fact]
    public void BuildIntegrity_fails_closed_when_a_destination_leaf_is_outside_the_payload_root()
    {
        string payload = @"D:\pay";
        // Destination is a directory on a DIFFERENT volume → its leaves cannot be made relative to the payload.
        string offRootDest = @"E:\elsewhere\App";
        string offRootLeaf = Path.Combine(offRootDest, "leak.txt");

        var fs = new FakeFileSystem().AddFile(offRootLeaf, "X");
        var hasher = new FakeHasher().Map(offRootLeaf, "h");
        var clock = new FakeClock(T0);

        var copied = CopiedReport(Copied("app", offRootDest));

        // Fail-closed: the writer throws instead of recording the off-root absolute path.
        Assert.Throws<OffRootDestinationException>(() =>
            new BackupIntegrityWriter().BuildIntegrity(copied, payload, fs, hasher, clock));
    }

    [Fact]
    public void BuildIntegrity_fails_closed_when_a_destination_leaf_escapes_the_payload_root_via_dotdot()
    {
        // payload and dest share a volume, but the dest sits ABOVE the payload → relative path starts with "..".
        string payload = @"D:\pay\inner";
        string escapingDest = @"D:\pay\outside.txt";

        var fs = new FakeFileSystem().AddFile(escapingDest, "X");
        var hasher = new FakeHasher().Map(escapingDest, "h");
        var clock = new FakeClock(T0);

        var copied = CopiedReport(Copied("esc", escapingDest));

        Assert.Throws<OffRootDestinationException>(() =>
            new BackupIntegrityWriter().BuildIntegrity(copied, payload, fs, hasher, clock));
    }

    // ---- test doubles ------------------------------------------------------------------------------------

    /// <summary>A fake <see cref="IBackupExecutor"/> that records the call and returns a canned report — no real copy.</summary>
    private sealed class RecordingBackupExecutor : IBackupExecutor
    {
        private readonly bool _authorized;
        private readonly BackupActionResult[] _results;

        public RecordingBackupExecutor(bool authorized, params BackupActionResult[] results)
        {
            _authorized = authorized;
            _results = results;
        }

        public bool WasCalled { get; private set; }

        public BackupExecutionReport Execute(OperationPlan plan, string approvedPlanHash)
        {
            WasCalled = true;
            return new BackupExecutionReport(_authorized, _results);
        }
    }

    /// <summary>Wraps a real gate and tallies every <see cref="ISafetyGate.Evaluate"/> call + the action kind judged.</summary>
    private sealed class CountingGate : ISafetyGate
    {
        private readonly ISafetyGate _inner;
        public CountingGate(ISafetyGate inner) => _inner = inner;

        public int EvaluateCount { get; private set; }
        public List<string> EvaluatedKinds { get; } = new();

        public SafetyVerdict Evaluate(PlannedAction action)
        {
            EvaluateCount++;
            EvaluatedKinds.Add(action.Kind);
            return _inner.Evaluate(action);
        }

        public PlanValidationResult Validate(OperationPlan plan) => _inner.Validate(plan);
    }
}
