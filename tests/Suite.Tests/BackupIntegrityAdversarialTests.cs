using System.IO;
using System.Text.Json;
using WindowsCareKit.Core.Abstractions;
using WindowsCareKit.Core.Modules.Backup;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;
using WindowsCareKit.Tests.TestInfra;
using Xunit;

namespace WindowsCareKit.Tests;

/// <summary>
/// RED-TEAM adversarial / edge-case probes against the backup-integrity ring
/// (BackupIntegrity / BackupIntegrityWriter / BackupRunner / OffRootDestinationException).
/// These tests are written to TRY TO BREAK the production code, not to anchor it. They are HOST-SAFE:
/// build-path tests use only in-memory fakes (zero real IO); write/runner-path tests use only a
/// TempWorkspace under %TEMP%. Nothing destructive runs; no real profile/registry is touched.
/// </summary>
public class BackupIntegrityAdversarialTests
{
    private static readonly DateTime T0 = new(2026, 1, 1, 12, 30, 0, DateTimeKind.Utc);

    private static ISafetyGate RealGate()
        => new SafetyGate(ProtectedResources.ForCurrentSystem(), new FakeCanonicalizer());

    private static CopyFileOutcome Copied(string id, string destination)
        => new(id, $@"C:\src\{id}", destination, true, null, "ok");

    private static CopySkipReport CopiedReport(params CopyFileOutcome[] outcomes) => new(outcomes);

    // =====================================================================================================
    // A. Empty / degenerate plans.
    // =====================================================================================================

    [Fact]
    public void BuildIntegrity_on_an_empty_report_yields_zero_rows()
    {
        IReadOnlyList<BackupIntegrity> rows = new BackupIntegrityWriter().BuildIntegrity(
            CopySkipReport.Empty, @"D:\pay", new FakeFileSystem(), new FakeHasher(), new FakeClock(T0));

        Assert.Empty(rows);
    }

    [Fact]
    public void BuildIntegrity_skips_a_copied_entry_with_a_whitespace_destination()
    {
        // A "copied" outcome whose Destination is blank/whitespace must contribute nothing (line 56 guard),
        // not throw and not produce a bogus row.
        var report = CopiedReport(
            new CopyFileOutcome("blank", @"C:\src\x", "   ", true, null, "ok"),
            new CopyFileOutcome("empty", @"C:\src\y", "", true, null, "ok"));

        IReadOnlyList<BackupIntegrity> rows = new BackupIntegrityWriter().BuildIntegrity(
            report, @"D:\pay", new FakeFileSystem(), new FakeHasher(), new FakeClock(T0));

        Assert.Empty(rows);
    }

    [Fact]
    public void WriteIntegrity_writes_an_empty_json_array_for_zero_rows_into_temp_root()
    {
        using var ws = new TempWorkspace("wck-adv-emptyrows-");
        string path = new BackupIntegrityWriter().WriteIntegrity(
            Array.Empty<BackupIntegrity>(), ws.Root, RealGate());

        Assert.True(File.Exists(path));
        string json = File.ReadAllText(path);
        // A serialized empty list, not garbage / not a crash.
        using JsonDocument doc = JsonDocument.Parse(json);
        Assert.Equal(JsonValueKind.Array, doc.RootElement.ValueKind);
        Assert.Equal(0, doc.RootElement.GetArrayLength());
    }

    // =====================================================================================================
    // B. Zero-byte leaf.
    // =====================================================================================================

    [Fact]
    public void BuildIntegrity_records_a_zero_byte_leaf_with_ByteSize_zero()
    {
        string payload = @"D:\pay";
        string dest = Path.Combine(payload, "empty.bin");
        var fs = new FakeFileSystem().AddFile(dest, Array.Empty<byte>());   // 0 bytes
        var hasher = new FakeHasher().Map(dest, "e3b0c442");                 // sha256 of empty input

        IReadOnlyList<BackupIntegrity> rows = new BackupIntegrityWriter().BuildIntegrity(
            CopiedReport(Copied("e", dest)), payload, fs, hasher, new FakeClock(T0));

        BackupIntegrity row = Assert.Single(rows);
        Assert.Equal(0, row.ByteSize);
        Assert.Equal("e3b0c442", row.Sha256);
        Assert.Equal("empty.bin", row.DestinationRelativePath);
    }

    // =====================================================================================================
    // C. Deep nested tree — every leaf surfaces, relative path keeps the full sub-tree.
    // =====================================================================================================

    [Fact]
    public void BuildIntegrity_walks_a_deeply_nested_tree_and_keeps_each_full_relative_subpath()
    {
        string payload = @"D:\pay";
        string treeDest = Path.Combine(payload, "App");
        string deep = Path.Combine(treeDest, "a", "b", "c", "d", "e", "leaf.txt");
        string shallow = Path.Combine(treeDest, "top.txt");

        var fs = new FakeFileSystem()
            .AddFile(deep, "1234")
            .AddFile(shallow, "9");
        var hasher = new FakeHasher().Map(deep, "hd").Map(shallow, "hs");

        IReadOnlyList<BackupIntegrity> rows = new BackupIntegrityWriter().BuildIntegrity(
            CopiedReport(Copied("app", treeDest)), payload, fs, hasher, new FakeClock(T0));

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r =>
            r.DestinationRelativePath == Path.Combine("App", "a", "b", "c", "d", "e", "leaf.txt"));
        Assert.Contains(rows, r => r.DestinationRelativePath == Path.Combine("App", "top.txt"));
        // None escaped the root, none is rooted.
        Assert.All(rows, r => Assert.False(Path.IsPathRooted(r.DestinationRelativePath)));
        Assert.All(rows, r => Assert.DoesNotContain("..", r.DestinationRelativePath));
    }

    // =====================================================================================================
    // D. Same destination from MULTIPLE entries — both rows emitted (the writer does not de-dup).
    //    This pins the actual behavior so a silent de-dup later would be caught.
    // =====================================================================================================

    [Fact]
    public void BuildIntegrity_emits_a_row_per_entry_even_when_two_entries_share_one_destination_file()
    {
        string payload = @"D:\pay";
        string shared = Path.Combine(payload, "shared.txt");
        var fs = new FakeFileSystem().AddFile(shared, "ABC");
        var hasher = new FakeHasher().Map(shared, "hsh");

        IReadOnlyList<BackupIntegrity> rows = new BackupIntegrityWriter().BuildIntegrity(
            CopiedReport(Copied("first", shared), Copied("second", shared)),
            payload, fs, hasher, new FakeClock(T0));

        // One file on disk, but two copy entries claimed it → two rows, distinct EntryId, same path+hash.
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.EntryId == "first");
        Assert.Contains(rows, r => r.EntryId == "second");
        Assert.All(rows, r => Assert.Equal("shared.txt", r.DestinationRelativePath));
        Assert.All(rows, r => Assert.Equal("hsh", r.Sha256));
    }

    [Fact]
    public void BuildIntegrity_when_two_TREE_entries_share_a_dir_emits_a_row_per_leaf_per_entry()
    {
        string payload = @"D:\pay";
        string treeDest = Path.Combine(payload, "App");
        var fs = new FakeFileSystem()
            .AddFile(Path.Combine(treeDest, "x.txt"), "x")
            .AddFile(Path.Combine(treeDest, "y.txt"), "y");

        IReadOnlyList<BackupIntegrity> rows = new BackupIntegrityWriter().BuildIntegrity(
            CopiedReport(Copied("e1", treeDest), Copied("e2", treeDest)),
            payload, fs, new FakeHasher(), new FakeClock(T0));

        // 2 leaves x 2 entries = 4 rows. Pins the non-dedup tree behavior.
        Assert.Equal(4, rows.Count);
        Assert.Equal(2, rows.Count(r => r.EntryId == "e1"));
        Assert.Equal(2, rows.Count(r => r.EntryId == "e2"));
    }

    // =====================================================================================================
    // E. Directory branch WINS over file branch when a path is registered as BOTH (line 61 before 66).
    //    A real FS can't be both, but the contract is "directory first" — pin it so a refactor can't flip it.
    // =====================================================================================================

    [Fact]
    public void BuildIntegrity_treats_a_destination_that_is_both_dir_and_file_as_a_directory()
    {
        string payload = @"D:\pay";
        string dest = Path.Combine(payload, "Ambiguous");
        var fs = new FakeFileSystem()
            .AddFile(dest, "iAmAFile")                              // makes FileExists(dest) true
            .AddFile(Path.Combine(dest, "child.txt"), "child");    // makes DirectoryExists(dest) true
        // Note: the inner AddFile also registers `dest` as a directory.

        IReadOnlyList<BackupIntegrity> rows = new BackupIntegrityWriter().BuildIntegrity(
            CopiedReport(Copied("amb", dest)), payload, fs, new FakeHasher(), new FakeClock(T0));

        // Directory branch is taken: only the child leaf is hashed (recursive enumerate), NOT `dest` itself.
        // The fake's EnumerateFiles returns files strictly under the prefix, so `dest` (the file) is excluded.
        BackupIntegrity row = Assert.Single(rows);
        Assert.Equal(Path.Combine("Ambiguous", "child.txt"), row.DestinationRelativePath);
    }

    // =====================================================================================================
    // F. OFF-ROOT for the SINGLE-FILE branch (existing F3 tests only cover the directory/tree branch).
    //    A copied single-file outcome whose destination is on another volume / escapes the root must ALSO
    //    fail closed — not silently leak an absolute or "..\" path into backup_integrity.json.
    // =====================================================================================================

    [Fact]
    public void BuildIntegrity_single_file_on_another_volume_fails_closed()
    {
        string payload = @"D:\pay";
        string offRootFile = @"E:\elsewhere\leak.txt";   // different volume, NOT a directory
        var fs = new FakeFileSystem().AddFile(offRootFile, "X");
        var hasher = new FakeHasher().Map(offRootFile, "h");

        Assert.Throws<OffRootDestinationException>(() =>
            new BackupIntegrityWriter().BuildIntegrity(
                CopiedReport(Copied("leak", offRootFile)), payload, fs, hasher, new FakeClock(T0)));
    }

    [Fact]
    public void BuildIntegrity_single_file_escaping_via_dotdot_fails_closed()
    {
        string payload = @"D:\pay\inner";
        string escaping = @"D:\pay\outside.txt";          // sits ABOVE the payload → "..\outside.txt"
        var fs = new FakeFileSystem().AddFile(escaping, "X");
        var hasher = new FakeHasher().Map(escaping, "h");

        Assert.Throws<OffRootDestinationException>(() =>
            new BackupIntegrityWriter().BuildIntegrity(
                CopiedReport(Copied("esc", escaping)), payload, fs, hasher, new FakeClock(T0)));
    }

    [Fact]
    public void BuildIntegrity_sibling_directory_sharing_a_name_prefix_fails_closed()
    {
        // payload "D:\pay" vs leaf under "D:\payload\..." — textually prefix-similar but NOT under the root.
        // Path.GetRelativePath returns "..\payload\x.txt" → must be rejected (not a false "inside" match).
        string payload = @"D:\pay";
        string trickyDest = @"D:\payload\App";             // tree on a sibling that SHARES the "pay" prefix
        string leaf = Path.Combine(trickyDest, "x.txt");
        var fs = new FakeFileSystem().AddFile(leaf, "X");

        Assert.Throws<OffRootDestinationException>(() =>
            new BackupIntegrityWriter().BuildIntegrity(
                CopiedReport(Copied("t", trickyDest)), payload, fs, new FakeHasher(), new FakeClock(T0)));
    }

    // =====================================================================================================
    // G. copiedUtc determinism — every row carries the SAME instant, captured ONCE from the clock at start,
    //    independent of how many leaves are walked.
    // =====================================================================================================

    [Fact]
    public void BuildIntegrity_stamps_every_row_with_the_single_clock_instant()
    {
        string payload = @"D:\pay";
        string treeDest = Path.Combine(payload, "App");
        var fs = new FakeFileSystem()
            .AddFile(Path.Combine(treeDest, "1.txt"), "a")
            .AddFile(Path.Combine(treeDest, "2.txt"), "b")
            .AddFile(Path.Combine(treeDest, "sub", "3.txt"), "c");

        var clock = new FakeClock(T0);
        IReadOnlyList<BackupIntegrity> rows = new BackupIntegrityWriter().BuildIntegrity(
            CopiedReport(Copied("app", treeDest)), payload, fs, new FakeHasher(), clock);

        Assert.Equal(3, rows.Count);
        Assert.All(rows, r => Assert.Equal(T0, r.CopiedUtc));
        Assert.All(rows, r => Assert.Equal(DateTimeKind.Utc, r.CopiedUtc.Kind));

        // Mutating the clock AFTER the build does not retroactively change the captured rows.
        clock.UtcNow = T0.AddDays(5);
        Assert.All(rows, r => Assert.Equal(T0, r.CopiedUtc));
    }

    // =====================================================================================================
    // Q. UNC / device-path destinations must FAIL CLOSED. A UNC ("\\server\share\...") or an extended-length
    //    device path ("\\?\C:\...") cannot be made relative to a local payload root; GetRelativePath returns
    //    it rooted/unchanged → the off-root guard must reject it rather than leaking a network/device path.
    // =====================================================================================================

    [Fact]
    public void BuildIntegrity_unc_single_file_destination_fails_closed()
    {
        string payload = @"D:\pay";
        string unc = @"\\evil-server\share\leak.txt";
        var fs = new FakeFileSystem().AddFile(unc, "X");
        var hasher = new FakeHasher().Map(unc, "h");

        Assert.Throws<OffRootDestinationException>(() =>
            new BackupIntegrityWriter().BuildIntegrity(
                CopiedReport(Copied("unc", unc)), payload, fs, hasher, new FakeClock(T0)));
    }

    [Fact]
    public void BuildIntegrity_unc_tree_destination_fails_closed()
    {
        string payload = @"D:\pay";
        string uncTree = @"\\evil-server\share\App";
        string leaf = uncTree + @"\leak.txt";
        var fs = new FakeFileSystem().AddFile(leaf, "X");

        Assert.Throws<OffRootDestinationException>(() =>
            new BackupIntegrityWriter().BuildIntegrity(
                CopiedReport(Copied("unc", uncTree)), payload, fs, new FakeHasher(), new FakeClock(T0)));
    }

    [Fact]
    public void BuildIntegrity_device_path_single_file_destination_with_different_drive_fails_closed()
    {
        // Extended-length device path on a DIFFERENT volume than the payload → cannot be made relative.
        string payload = @"D:\pay";
        string device = @"\\?\E:\elsewhere\leak.txt";
        var fs = new FakeFileSystem().AddFile(device, "X");
        var hasher = new FakeHasher().Map(device, "h");

        Assert.Throws<OffRootDestinationException>(() =>
            new BackupIntegrityWriter().BuildIntegrity(
                CopiedReport(Copied("dev", device)), payload, fs, hasher, new FakeClock(T0)));
    }

    // =====================================================================================================
    // N. DEGENERATE: a SINGLE-FILE destination that equals the payload root itself. Path.GetRelativePath
    //    returns "." here — which is NOT rooted and NOT "..", so it slips past the off-root guard and is
    //    recorded as a literal "." relative path. This characterizes the (arguably odd) current behavior so
    //    a future change is forced to be deliberate. A file AT the payload root is not a realistic copy
    //    destination (payload root is a directory), but the writer does not reject it.
    // =====================================================================================================

    [Fact]
    public void BuildIntegrity_single_file_dest_equal_to_payload_root_is_recorded_as_dot()
    {
        string payload = @"D:\pay";
        // Destination is the payload root itself, registered ONLY as a file (no child → not a directory).
        var fs = new FakeFileSystem().AddFile(payload, "X");
        var hasher = new FakeHasher().Map(payload, "h");

        IReadOnlyList<BackupIntegrity> rows = new BackupIntegrityWriter().BuildIntegrity(
            CopiedReport(Copied("self", payload)), payload, fs, hasher, new FakeClock(T0));

        BackupIntegrity row = Assert.Single(rows);
        // Characterization: a "." relative path is emitted, NOT rejected and NOT an absolute leak.
        Assert.Equal(".", row.DestinationRelativePath);
        Assert.False(Path.IsPathRooted(row.DestinationRelativePath));
    }

    // =====================================================================================================
    // O. A leaf that sits DIRECTLY in the payload root via the directory branch (dest == payload root,
    //    a child file underneath) is recorded with just the file name (no leading separator, no "..").
    // =====================================================================================================

    [Fact]
    public void BuildIntegrity_dir_dest_equal_to_payload_root_records_bare_child_names()
    {
        string payload = @"D:\pay";
        string leaf = Path.Combine(payload, "loose.txt");
        var fs = new FakeFileSystem().AddFile(leaf, "X");   // registers `payload` as a directory too

        IReadOnlyList<BackupIntegrity> rows = new BackupIntegrityWriter().BuildIntegrity(
            CopiedReport(Copied("root", payload)), payload, fs, new FakeHasher(), new FakeClock(T0));

        BackupIntegrity row = Assert.Single(rows);
        Assert.Equal("loose.txt", row.DestinationRelativePath);
    }

    // =====================================================================================================
    // P. ALT-SEPARATOR destination: a payload root / leaf written with forward slashes. The off-root guard
    //    checks BOTH DirectorySeparatorChar and AltDirectorySeparatorChar; a forward-slash leaf that is
    //    legitimately under a forward-slash root must NOT be falsely rejected, and an escaping one MUST be.
    // =====================================================================================================

    [Fact]
    public void BuildIntegrity_handles_a_forward_slash_payload_root_and_leaf()
    {
        // Mixed/alt separators: GetRelativePath normalizes; the leaf stays under the root → accepted.
        string payload = "D:/pay/inner";
        string leaf = "D:/pay/inner/sub/file.txt";
        var fs = new FakeFileSystem().AddFile(leaf, "X");
        var hasher = new FakeHasher().Map(leaf, "h");

        IReadOnlyList<BackupIntegrity> rows = new BackupIntegrityWriter().BuildIntegrity(
            CopiedReport(Copied("fs", leaf)), payload, fs, hasher, new FakeClock(T0));

        BackupIntegrity row = Assert.Single(rows);
        Assert.False(Path.IsPathRooted(row.DestinationRelativePath));
        Assert.DoesNotContain("..", row.DestinationRelativePath);
        // The leaf name survives regardless of which separator the platform normalizes to.
        Assert.EndsWith("file.txt", row.DestinationRelativePath);
    }

    // =====================================================================================================
    // H. Argument validation — the writer fails fast on null/blank inputs rather than producing junk.
    // =====================================================================================================

    [Fact]
    public void BuildIntegrity_rejects_a_blank_payload_root()
    {
        Assert.Throws<ArgumentException>(() => new BackupIntegrityWriter().BuildIntegrity(
            CopySkipReport.Empty, "   ", new FakeFileSystem(), new FakeHasher(), new FakeClock(T0)));
    }

    [Fact]
    public void BuildIntegrity_rejects_a_null_report()
    {
        Assert.Throws<ArgumentNullException>(() => new BackupIntegrityWriter().BuildIntegrity(
            null!, @"D:\pay", new FakeFileSystem(), new FakeHasher(), new FakeClock(T0)));
    }

    [Fact]
    public void WriteIntegrity_rejects_a_null_rows_list()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BackupIntegrityWriter().WriteIntegrity(null!, @"D:\pay", RealGate()));
    }

    [Fact]
    public void WriteIntegrity_rejects_a_blank_payload_root()
    {
        Assert.Throws<ArgumentException>(() =>
            new BackupIntegrityWriter().WriteIntegrity(Array.Empty<BackupIntegrity>(), "  ", RealGate()));
    }

    // =====================================================================================================
    // I. GATE BLOCK is a HARD STOP — when the gate refuses, the writer throws AND writes NOTHING. Triple
    //    assertion: throws + no integrity file + the gate was actually consulted exactly once.
    // =====================================================================================================

    [Fact]
    public void WriteIntegrity_when_gate_blocks_throws_and_writes_no_file_and_consults_gate_once()
    {
        using var ws = new TempWorkspace("wck-adv-gateblock-");
        // Force a block by feeding a gate whose policy refuses everything (a stub that always blocks).
        var blocking = new AlwaysBlockGate();

        var rows = new[] { new BackupIntegrity("e", "a.txt", "h", 1, T0) };

        Assert.Throws<UnauthorizedAccessException>(() =>
            new BackupIntegrityWriter().WriteIntegrity(rows, ws.Root, blocking));

        // Fail-closed: nothing landed even though the temp root itself is writable.
        Assert.False(File.Exists(Path.Combine(ws.Root, BackupIntegrityFiles.Integrity)));
        Assert.Equal(1, blocking.EvaluateCount);
    }

    // =====================================================================================================
    // J. RUNNER: refusal path writes NOTHING and returns no rows EVEN when the destination tree exists and
    //    would otherwise hash to many leaves. Proves authorization short-circuits before any integrity walk.
    // =====================================================================================================

    [Fact]
    public void BackupRunner_refusal_skips_the_integrity_walk_entirely_even_with_a_populated_dest_tree()
    {
        using var ws = new TempWorkspace("wck-adv-refusal-");
        string treeDest = Path.Combine(ws.Root, "App");

        var plan = new OperationPlan("Back up", "backup",
            new[] { TestData.Copy(@"C:\src\App", treeDest) }, T0);
        var planResult = new BackupPlanResult(plan,
            Array.Empty<BackupEntry>(), Array.Empty<BackupSkip>(), Array.Empty<BackupEntry>());

        var executor = new RecordingBackupExecutor(authorized: false,
            new BackupActionResult(plan.Actions[0].Id, BackupActionStatus.NotRun, "plan refused"));

        // A fully-populated in-memory dest tree — if the runner wrongly walked it on refusal, rows would appear.
        var fs = new FakeFileSystem()
            .AddFile(Path.Combine(treeDest, "a.txt"), "a")
            .AddFile(Path.Combine(treeDest, "b.txt"), "b");

        // A throwing integrity writer would ALSO catch any accidental call on the refusal path.
        var runner = new BackupRunner(executor, new ThrowingIntegrityWriter(),
            new BackupReportWriter(new WindowsCareKit.Core.Logging.LogRedactor(null, null)),
            RealGate(), fs, new FakeHasher(), new FakeClock(T0));

        BackupRunResult result = runner.Run(planResult, plan.ComputeHash(), ws.Root);

        Assert.False(result.Authorized);
        Assert.Empty(result.Integrity);
        Assert.False(File.Exists(Path.Combine(ws.Root, BackupIntegrityFiles.Integrity)));
        Assert.False(File.Exists(Path.Combine(ws.Root, BackupReportFiles.Report)));
    }

    [Fact]
    public void BackupRunner_rejects_a_blank_payload_root()
    {
        var plan = new OperationPlan("Back up", "backup", Array.Empty<PlannedAction>(), T0);
        var planResult = new BackupPlanResult(plan,
            Array.Empty<BackupEntry>(), Array.Empty<BackupSkip>(), Array.Empty<BackupEntry>());
        var runner = new BackupRunner(
            new RecordingBackupExecutor(authorized: true),
            new BackupIntegrityWriter(),
            new BackupReportWriter(new WindowsCareKit.Core.Logging.LogRedactor(null, null)),
            RealGate(), new FakeFileSystem(), new FakeHasher(), new FakeClock(T0));

        Assert.Throws<ArgumentException>(() => runner.Run(planResult, "hash", "   "));
    }

    // =====================================================================================================
    // K. RUNNER end-to-end through a REAL gate into a forbidden payload root: the integrity write must be
    //    refused by the gate (UnauthorizedAccessException bubbles out) — the run cannot silently succeed
    //    writing into a protected location. HOST-SAFE: the executor performs NO real copy; the gate refuses
    //    BEFORE any write, so nothing is ever written to C:\Windows.
    // =====================================================================================================

    [Fact]
    public void BackupRunner_authorized_but_payload_root_is_protected_bubbles_the_gate_refusal()
    {
        string protectedRoot = @"C:\Windows\wck-evil-adv";
        string treeDest = Path.Combine(protectedRoot, "App");

        var plan = new OperationPlan("Back up", "backup",
            new[] { TestData.Copy(@"C:\src\App", treeDest) }, T0);
        var planResult = new BackupPlanResult(plan,
            Array.Empty<BackupEntry>(), Array.Empty<BackupSkip>(), Array.Empty<BackupEntry>());

        var executor = new RecordingBackupExecutor(authorized: true,
            new BackupActionResult(plan.Actions[0].Id, BackupActionStatus.Done, "ok"));

        var fs = new FakeFileSystem().AddFile(Path.Combine(treeDest, "a.txt"), "a");
        var runner = new BackupRunner(executor, new BackupIntegrityWriter(),
            new BackupReportWriter(new WindowsCareKit.Core.Logging.LogRedactor(null, null)),
            RealGate(), fs, new FakeHasher(), new FakeClock(T0));

        // The integrity writer re-gates the protected payload root and throws → the whole run fails closed.
        Assert.Throws<UnauthorizedAccessException>(() => runner.Run(planResult, plan.ComputeHash(), protectedRoot));
        // And nothing was written into the protected location.
        Assert.False(File.Exists(Path.Combine(protectedRoot, BackupIntegrityFiles.Integrity)));
    }

    // =====================================================================================================
    // R. The gate write-probe is the payload ROOT itself: a drive-root payload ("D:\") must be REFUSED by the
    //    real gate (drive roots are blocked), so the integrity manifest can never be dropped at a volume root.
    //    HOST-SAFE: the real gate refuses BEFORE any File.WriteAllText, so nothing is written to D:\.
    // =====================================================================================================

    [Fact]
    public void WriteIntegrity_refuses_a_drive_root_payload()
    {
        var rows = new[] { new BackupIntegrity("e", "a.txt", "h", 1, T0) };
        Assert.Throws<UnauthorizedAccessException>(() =>
            new BackupIntegrityWriter().WriteIntegrity(rows, @"D:\", RealGate()));
    }

    [Fact]
    public void WriteIntegrity_refuses_program_files_payload()
    {
        var rows = Array.Empty<BackupIntegrity>();
        Assert.Throws<UnauthorizedAccessException>(() =>
            new BackupIntegrityWriter().WriteIntegrity(rows, @"C:\Program Files\wck-evil", RealGate()));
    }

    [Fact]
    public void WriteIntegrity_refuses_a_unc_payload_root()
    {
        // A UNC payload root is non-local → the gate's write-target policy fails closed.
        var rows = Array.Empty<BackupIntegrity>();
        Assert.Throws<UnauthorizedAccessException>(() =>
            new BackupIntegrityWriter().WriteIntegrity(rows, @"\\server\share\backup", RealGate()));
    }

    // =====================================================================================================
    // L. Unicode + spaces in the relative path survive round-trip through JSON serialization unmangled.
    // =====================================================================================================

    [Fact]
    public void WriteIntegrity_preserves_unicode_and_spaces_in_the_relative_path_through_json()
    {
        using var ws = new TempWorkspace("wck-adv-unicode-");
        string rel = Path.Combine("Belgeler ç", "günce — 日本語.txt");
        var rows = new[] { new BackupIntegrity("u", rel, "h", 7, T0) };

        string path = new BackupIntegrityWriter().WriteIntegrity(rows, ws.Root, RealGate());
        string json = File.ReadAllText(path);

        using JsonDocument doc = JsonDocument.Parse(json);
        JsonElement only = Assert.Single(doc.RootElement.EnumerateArray().ToArray());
        Assert.Equal(rel, only.GetProperty("destinationRelativePath").GetString());
        Assert.Equal("günce — 日本語.txt", Path.GetFileName(only.GetProperty("destinationRelativePath").GetString()!));
    }

    // =====================================================================================================
    // M. NEGATIVE-SIZE / large-size sanity: ByteSize is the stream length read through the port. A leaf with
    //    a large body reports its true length (no int overflow / truncation at the long boundary in practice).
    // =====================================================================================================

    [Fact]
    public void BuildIntegrity_reports_the_true_byte_length_for_a_large_leaf()
    {
        string payload = @"D:\pay";
        string dest = Path.Combine(payload, "big.bin");
        byte[] body = new byte[100_000];                  // well past any byte/short boundary
        var fs = new FakeFileSystem().AddFile(dest, body);

        IReadOnlyList<BackupIntegrity> rows = new BackupIntegrityWriter().BuildIntegrity(
            CopiedReport(Copied("big", dest)), payload, fs, new FakeHasher(), new FakeClock(T0));

        Assert.Equal(100_000, Assert.Single(rows).ByteSize);
    }

    // ---- test doubles ------------------------------------------------------------------------------------

    /// <summary>A fake executor that records the call and returns a canned report — no real copy.</summary>
    private sealed class RecordingBackupExecutor : IBackupExecutor
    {
        private readonly bool _authorized;
        private readonly BackupActionResult[] _results;

        public RecordingBackupExecutor(bool authorized, params BackupActionResult[] results)
        {
            _authorized = authorized;
            _results = results;
        }

        public BackupExecutionReport Execute(OperationPlan plan, string approvedPlanHash)
            => new(_authorized, _results);
    }

    /// <summary>A gate that refuses every action and tallies how many times it was consulted.</summary>
    private sealed class AlwaysBlockGate : ISafetyGate
    {
        public int EvaluateCount { get; private set; }
        public SafetyVerdict Evaluate(PlannedAction action)
        {
            EvaluateCount++;
            return SafetyVerdict.Block("denied by stub");
        }
        public PlanValidationResult Validate(OperationPlan plan)
            => new(false, Array.Empty<ActionVerdict>());
    }

    /// <summary>An integrity writer that explodes if ever called — used to prove the refusal path skips it.</summary>
    private sealed class ThrowingIntegrityWriter : IIntegrityWriter
    {
        public IReadOnlyList<BackupIntegrity> BuildIntegrity(
            CopySkipReport copied, string payloadRoot, IFileSystem fs, IHasher hasher, IClock clock)
            => throw new InvalidOperationException("integrity walk must NOT run on a refused plan");

        public string WriteIntegrity(IReadOnlyList<BackupIntegrity> rows, string payloadRoot, ISafetyGate gate)
            => throw new InvalidOperationException("integrity write must NOT run on a refused plan");
    }
}
