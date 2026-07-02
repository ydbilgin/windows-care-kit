using System.Globalization;
using System.IO;
using System.Text;
using WindowsCareKit.Core.Logging;
using WindowsCareKit.Core.Planning;
using WindowsCareKit.Core.Safety;

namespace WindowsCareKit.Core.Modules.Backup;

/// <summary>The two report file names the Backup module writes into the payload dir (spec §1.3).</summary>
public static class BackupReportFiles
{
    /// <summary>The human-readable backup report (what was copied / skipped / reinstall list).</summary>
    public const string Report = "REPORT.md";

    /// <summary>The manual checklist (re-login for secrets, manual-todo items).</summary>
    public const string ManualTodo = "MANUAL_TODO.md";
}

/// <summary>
/// Builds <c>REPORT.md</c> and <c>MANUAL_TODO.md</c> from a <see cref="BackupPlanResult"/> and the post-execution
/// <see cref="CopySkipReport"/> (spec §1.3). The markdown construction is pure (testable). Writing the two files
/// is a plain <see cref="File.WriteAllText"/> into the payload dir (outside the repo) — that write API is NOT on
/// <c>BannedSymbols.txt</c> (only Delete/Move/registry/Process are banned), so it is allowed from Suite.Core.
/// The reports list: copied OK, skipped (locked/forbidden/too-long), manual to-do (never-read + re-login
/// guidance), and the reinstall list (<c>install-*</c> entries the Kur module consumes).
///
/// <para>The reports are written to the payload root, which is frequently external/USB/shared media, so every
/// emitted path / detail string is run through <see cref="ILogRedactor"/> first — the same redaction the
/// ExecutionLog already applies — so the real username and profile layout do not travel off-machine in
/// cleartext (spec §3 redaction intent).</para>
/// </summary>
public sealed class BackupReportWriter
{
    private readonly ILogRedactor _redactor;

    public BackupReportWriter(ILogRedactor redactor)
        => _redactor = redactor ?? throw new ArgumentNullException(nameof(redactor));

    private string R(string? text) => _redactor.Redact(text);

    /// <summary>
    /// Build the <c>REPORT.md</c> text: copied OK, skipped, and the reinstall list. <paramref name="integrityCount"/>
    /// is how many per-leaf rows the integrity manifest holds; it surfaces a one-line pointer to
    /// <c>backup_integrity.json</c> in the summary (W4). Defaults to 0 so existing callers are unaffected.
    /// </summary>
    public string BuildReport(BackupPlanResult plan, CopySkipReport copyReport, DateTime utc, int integrityCount = 0)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(copyReport);

        var sb = new StringBuilder();
        sb.Append("# REPORT — Windows Care Kit backup\n\n");
        sb.Append("Generated (UTC): ")
          .Append(utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
          .Append("\n\n");

        var copied = copyReport.Copied;
        var skippedCopies = copyReport.Skipped;

        sb.Append("## Summary\n\n");
        sb.Append("- Copied: ").Append(copied.Count).Append('\n');
        sb.Append("- Skipped: ").Append(skippedCopies.Count).Append('\n');
        sb.Append("- Manual to-do: ").Append(plan.ManualTodos.Count).Append('\n');
        sb.Append("- Reinstall list: ").Append(plan.ReinstallList.Count).Append('\n');
        sb.Append("- Integrity: ").Append(BackupIntegrityFiles.Integrity)
          .Append(" (").Append(integrityCount).Append(" hash)\n\n");

        sb.Append("## Copied\n\n");
        if (copied.Count == 0)
            sb.Append("_None._\n\n");
        else
        {
            foreach (CopyFileOutcome o in copied)
                sb.Append("- `").Append(R(o.Source)).Append("` → `").Append(R(o.Destination)).Append("`\n");
            sb.Append('\n');
        }

        sb.Append("## Skipped (locked / forbidden / secret / too long / blocked)\n\n");
        if (skippedCopies.Count == 0 && plan.Skipped.Count == 0)
            sb.Append("_None._\n\n");
        else
        {
            foreach (CopyFileOutcome o in skippedCopies)
                sb.Append("- `").Append(R(o.Source)).Append("` — Skipped (")
                  .Append(o.Reason?.ToString() ?? "Other").Append("): ").Append(R(o.Detail)).Append('\n');
            foreach (BackupSkip s in plan.Skipped)
                sb.Append("- `").Append(R(s.Entry.Id)).Append("` — ").Append(R(s.Reason)).Append('\n');
            sb.Append('\n');
        }

        sb.Append("## Reinstall list (handled by the Reinstall module — not copied)\n\n");
        if (plan.ReinstallList.Count == 0)
            sb.Append("_None._\n\n");
        else
        {
            foreach (BackupEntry e in plan.ReinstallList)
                sb.Append("- ").Append(R(e.Id)).Append(" — ").Append(e.Description).Append('\n');
            sb.Append('\n');
        }

        return sb.ToString();
    }

    /// <summary>Build the <c>MANUAL_TODO.md</c> text: never-read secrets (re-login) and manual checklist items.</summary>
    public string BuildManualTodo(BackupPlanResult plan, DateTime utc)
    {
        ArgumentNullException.ThrowIfNull(plan);

        var sb = new StringBuilder();
        sb.Append("# MANUAL_TODO — do these by hand after the format\n\n");
        sb.Append("Generated (UTC): ")
          .Append(utc.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture))
          .Append("\n\n");
        sb.Append("These items are **not** copied. Secrets (tokens, browser passwords, DPAPI data) cannot be\n");
        sb.Append("decrypted after a format, so the safe path is to sign in again on the fresh system.\n\n");

        if (plan.ManualTodos.Count == 0)
        {
            sb.Append("_Nothing to do manually._\n");
            return sb.ToString();
        }

        foreach (BackupEntry e in plan.ManualTodos)
        {
            // Redact only the path-bearing fields (id can embed a path). Description/UiWarning are authored
            // manifest prose (human guidance like "TOKEN — keep off"), not runtime paths, so they are emitted
            // as-authored — running the secret-keyword redactor over prose would mangle legitimate guidance.
            sb.Append("## ").Append(R(e.Id)).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(e.Description))
                sb.Append(e.Description).Append("\n\n");
            if (!string.IsNullOrWhiteSpace(e.UiWarning))
                sb.Append("> ⚠ ").Append(e.UiWarning).Append("\n\n");
            if (Modules.Backup.SecretHandling.ForbidsCopy(e.SecretHandling))
                sb.Append("- Action: sign in again after the format (secret not copied).\n\n");
            else
                sb.Append("- Action: complete this step by hand.\n\n");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Write both reports into <paramref name="payloadRootDir"/> and return the two paths. Uses
    /// <see cref="File.WriteAllText"/> (not a banned API) into the payload dir (outside the repo).
    ///
    /// <para>L9: the payload root is re-evaluated through <paramref name="gate"/> (as a synthetic
    /// <see cref="CopyAction"/> write target) before either file is written, so the reports cannot be
    /// dropped into a protected/system location — even though the writer is not the normal executor path.
    /// Throws <see cref="UnauthorizedAccessException"/> when the gate blocks the location.</para>
    /// </summary>
    public (string ReportPath, string ManualTodoPath) WriteReports(
        BackupPlanResult plan, CopySkipReport copyReport, string payloadRootDir, DateTime utc, ISafetyGate gate,
        int integrityCount = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(payloadRootDir);
        ArgumentNullException.ThrowIfNull(gate);

        // Gate the destination before any write. Reuse the standard write-target policy via a synthetic
        // CopyAction so this stays consistent with how every other write is judged (works with any ISafetyGate).
        var probe = new CopyAction
        {
            Source = payloadRootDir,
            Destination = payloadRootDir,
            Description = "write backup reports",
            Reason = "report output location",
        };
        SafetyVerdict verdict = gate.Evaluate(probe);
        if (!verdict.Allowed)
            throw new UnauthorizedAccessException($"report output location refused by the safety gate: {verdict.Reason}");

        Directory.CreateDirectory(payloadRootDir);

        string reportPath = Path.Combine(payloadRootDir, BackupReportFiles.Report);
        string manualPath = Path.Combine(payloadRootDir, BackupReportFiles.ManualTodo);

        File.WriteAllText(reportPath, BuildReport(plan, copyReport, utc, integrityCount));
        File.WriteAllText(manualPath, BuildManualTodo(plan, utc));

        return (reportPath, manualPath);
    }
}
